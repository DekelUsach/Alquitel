-- ─────────────────────────────────────────────────────────────────────────────
-- 20260829000200_app_identity_and_roles
--
-- PROBLEMA QUE RESUELVE
--
-- Hoy TODOS los puestos de trabajo comparten una única credencial de PostgreSQL
-- (`alquitel_app`, cuya password viaja en `appsettings.local.json` / variable de
-- entorno de cada máquina). Verificado contra el proyecto real:
--
--   pg_policies → 10 políticas, todas `FOR ALL TO alquitel_app USING(true)
--                 WITH CHECK(true)` — incluida la tabla "Users" (roles) y
--                 "OrderAuditEvents" (bitácora).
--
-- O sea: RLS está encendido pero no separa a nadie de nadie. El "rol" de un
-- empleado (Admin / Vendedor / Armador) existe únicamente en el proceso WPF y
-- gobierna qué botones se dibujan. Cualquiera que copie el connection string de
-- su propia máquina y abra un cliente SQL puede: leerse todos los presupuestos,
-- ascenderse a Admin (`UPDATE "Users" SET "Role"=0`), reescribir precios y
-- borrar la bitácora que lo registraría. La visibilidad de botones no es una
-- frontera de seguridad.
--
-- MODELO OBJETIVO
--
--   identidad  = Supabase Auth (una cuenta por empleado, email + password,
--                JWT firmado por el servidor).
--   autorización = rol normalizado leído del SERVIDOR a partir de auth.uid(),
--                nunca del cliente.
--   acceso     = PostgREST / RPC con el JWT del usuario, sujeto a RLS.
--                Sin conexión directa a PostgreSQL desde la app de escritorio.
--
-- Esta migración instala la mitad servidor. La conmutación del cliente está en
-- docs/SUPABASE_MIGRATION_CONTRACT.md y termina con
-- 20260829001000_decommission_alquitel_app.sql.
--
-- NOTA PARA EL EQUIPO .NET (Codex): esta migración AGREGA columnas a "Users".
-- EF Core ignora columnas no mapeadas, así que la app actual sigue funcionando
-- sin cambios. Ver el contrato para las que hay que mapear.
-- ─────────────────────────────────────────────────────────────────────────────

-- ── 1. Esquema `app`: todo lo que es autorización vive acá, no en public ─────
-- Separarlo importa: `public` está expuesto por PostgREST; `app` no. Así ningún
-- helper de autorización es invocable como RPC desde el navegador.

CREATE SCHEMA IF NOT EXISTS app;

REVOKE ALL ON SCHEMA app FROM PUBLIC;
GRANT USAGE ON SCHEMA app TO authenticated, service_role;

-- ── 2. Roles normalizados ────────────────────────────────────────────────────
-- El entero se conserva porque es lo que ya está persistido y lo que mapea el
-- enum UserRole de C#. El `code` es el nombre canónico que usan las políticas:
-- así una política se lee y se audita en castellano, no en números mágicos.

CREATE TABLE IF NOT EXISTS app.roles (
    code        text PRIMARY KEY,
    legacy_int  integer NOT NULL UNIQUE,
    description text NOT NULL
);

INSERT INTO app.roles (code, legacy_int, description) VALUES
    ('admin',       0, 'Acceso total: catálogo, usuarios, configuración, reportes.'),
    ('comercial',   1, 'Presupuestos, clientes y ubicaciones. Sin costos ni catálogo.'),
    ('operaciones', 2, 'Órdenes de trabajo asignadas. Sin precios ni datos comerciales.'),
    ('lectura',     3, 'Solo consulta. No escribe nada.')
ON CONFLICT (code) DO UPDATE
    SET legacy_int = EXCLUDED.legacy_int,
        description = EXCLUDED.description;

-- Solo lectura, y solo para quien ya está autenticado.
GRANT SELECT ON app.roles TO authenticated;

-- ── 3. "Users" pasa a ser el puente con Supabase Auth ────────────────────────

ALTER TABLE public."Users"
    ADD COLUMN IF NOT EXISTS "AuthUserId"     uuid NULL,
    ADD COLUMN IF NOT EXISTS "Email"          text NULL,
    -- Desactivación/revocación explícita, distinta del borrado lógico:
    -- "IsArchived" saca al usuario de las grillas; "DisabledAt" le corta el
    -- acceso al backend aunque su JWT siga sin vencer (RLS lo relee en cada
    -- request, así que la revocación es efectiva en el siguiente pedido).
    ADD COLUMN IF NOT EXISTS "DisabledAt"     timestamptz NULL,
    ADD COLUMN IF NOT EXISTS "DisabledReason" text NULL,
    ADD COLUMN IF NOT EXISTS "UpdatedAt"      timestamptz NOT NULL DEFAULT now();

-- FK a auth.users: el borrado de la cuenta de Auth no debe borrar al empleado
-- (es el actor de la bitácora), solo desvincularlo.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'Users_AuthUserId_fkey' AND conrelid = 'public."Users"'::regclass
    ) THEN
        ALTER TABLE public."Users"
            ADD CONSTRAINT "Users_AuthUserId_fkey"
            FOREIGN KEY ("AuthUserId") REFERENCES auth.users (id) ON DELETE SET NULL;
    END IF;
END
$$;

-- Una cuenta de Auth = un usuario de la app. Sin esto, dos filas podrían
-- reclamar la misma identidad y `app.current_role_code()` elegiría al azar.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_AuthUserId"
    ON public."Users" ("AuthUserId") WHERE "AuthUserId" IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Email"
    ON public."Users" (lower("Email")) WHERE "Email" IS NOT NULL;

-- El rol tiene que ser uno de los definidos. Sin esto, `UPDATE "Users" SET
-- "Role"=99` deja al usuario sin rol resoluble y las políticas fallan abierto
-- o cerrado según cómo se escriban.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_Users_Role_valido') THEN
        ALTER TABLE public."Users"
            ADD CONSTRAINT "CK_Users_Role_valido"
            CHECK ("Role" IN (0, 1, 2, 3));
    END IF;
END
$$;

-- ── 4. Helpers de identidad ──────────────────────────────────────────────────
-- SECURITY DEFINER porque tienen que poder leer "Users" incluso cuando las
-- políticas de "Users" todavía no dejarían (evita la recursión clásica de RLS:
-- "para saber si podés leer Users hay que leer Users").
-- search_path fijado: sin eso, un esquema temporal del atacante podría
-- suplantar `roles` o `Users` dentro de la función.
-- Devuelven ESCALARES, nunca la fila entera: la fila incluye PasswordHash.

CREATE OR REPLACE FUNCTION app.current_user_id()
RETURNS uuid
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = public, pg_catalog
AS $$
    SELECT u."Id"
    FROM   public."Users" u
    WHERE  u."AuthUserId" = auth.uid()
      AND  u."IsArchived" = false
      AND  u."DisabledAt" IS NULL
    LIMIT  1
$$;

COMMENT ON FUNCTION app.current_user_id() IS
'Id de public."Users" del JWT actual. NULL si no hay sesión, si la cuenta está archivada o si fue desactivada (revocación efectiva en el siguiente request).';

CREATE OR REPLACE FUNCTION app.current_role_code()
RETURNS text
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = public, app, pg_catalog
AS $$
    SELECT r.code
    FROM   public."Users" u
    JOIN   app.roles r ON r.legacy_int = u."Role"
    WHERE  u."AuthUserId" = auth.uid()
      AND  u."IsArchived" = false
      AND  u."DisabledAt" IS NULL
    LIMIT  1
$$;

COMMENT ON FUNCTION app.current_role_code() IS
'Rol normalizado del JWT actual, resuelto SIEMPRE contra la base. Nunca leer el rol de un claim del cliente ni de un parámetro: el cliente puede mentir.';

CREATE OR REPLACE FUNCTION app.has_role(VARIADIC p_codes text[])
RETURNS boolean
LANGUAGE sql
STABLE
SET search_path = app, pg_catalog
AS $$
    SELECT app.current_role_code() = ANY (p_codes)
$$;

-- "¿Hay una sesión de aplicación válida?" — usado por las políticas para negar
-- de entrada a cualquier JWT que no corresponda a un empleado activo.
CREATE OR REPLACE FUNCTION app.is_active_user()
RETURNS boolean
LANGUAGE sql
STABLE
SET search_path = app, pg_catalog
AS $$
    SELECT app.current_user_id() IS NOT NULL
$$;

-- Nadie ejecuta estas funciones salvo sesiones autenticadas.
REVOKE ALL ON FUNCTION app.current_user_id()          FROM PUBLIC;
REVOKE ALL ON FUNCTION app.current_role_code()        FROM PUBLIC;
REVOKE ALL ON FUNCTION app.has_role(text[])           FROM PUBLIC;
REVOKE ALL ON FUNCTION app.is_active_user()           FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.current_user_id()   TO authenticated, service_role;
GRANT EXECUTE ON FUNCTION app.current_role_code() TO authenticated, service_role;
GRANT EXECUTE ON FUNCTION app.has_role(text[])    TO authenticated, service_role;
GRANT EXECUTE ON FUNCTION app.is_active_user()    TO authenticated, service_role;

-- ── 5. El cliente no puede tocar su propio privilegio ────────────────────────
-- RLS con WITH CHECK no alcanza: no ve OLD, así que no puede comparar "el rol
-- cambió". Un trigger BEFORE sí. Es la barrera contra la escalada del insider:
-- aunque una política permitiera UPDATE sobre la propia fila, el rol no se mueve.

CREATE OR REPLACE FUNCTION app.guard_user_privilege_columns()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, app, pg_catalog
AS $$
DECLARE
    v_role   text;
    v_caller uuid;
    v_admins integer;
BEGIN
    -- Sin JWT no hay identidad de aplicación: es el rol de servicio, el
    -- mantenimiento del operador o (todavía) la conexión directa legada. Esos
    -- caminos se cierran en 20260829001000_decommission_alquitel_app.sql; hasta
    -- entonces este trigger no puede distinguirlos y los deja pasar.
    IF auth.uid() IS NULL THEN
        IF TG_OP = 'UPDATE' THEN NEW."UpdatedAt" := now(); END IF;
        RETURN NEW;
    END IF;

    v_role   := app.current_role_code();
    v_caller := app.current_user_id();

    IF TG_OP = 'INSERT' THEN
        IF v_role IS DISTINCT FROM 'admin' THEN
            RAISE EXCEPTION 'Solo un Admin puede dar de alta usuarios'
                USING ERRCODE = '42501';
        END IF;
        RETURN NEW;
    END IF;

    -- UPDATE
    IF v_role IS DISTINCT FROM 'admin' THEN
        IF NEW."Id" IS DISTINCT FROM v_caller THEN
            RAISE EXCEPTION 'Solo se puede modificar la propia cuenta'
                USING ERRCODE = '42501';
        END IF;
        IF NEW."Role"       IS DISTINCT FROM OLD."Role"
        OR NEW."IsArchived" IS DISTINCT FROM OLD."IsArchived"
        OR NEW."DisabledAt" IS DISTINCT FROM OLD."DisabledAt"
        OR NEW."AuthUserId" IS DISTINCT FROM OLD."AuthUserId"
        OR NEW."Id"         IS DISTINCT FROM OLD."Id" THEN
            RAISE EXCEPTION 'No se puede modificar el rol ni el estado de la propia cuenta'
                USING ERRCODE = '42501';
        END IF;
    ELSE
        -- Un Admin sí cambia roles, pero no puede dejar el sistema sin Admin:
        -- eso sería un lockout irreversible sin acceso al panel de Supabase.
        IF OLD."Role" = 0
           AND (NEW."Role" <> 0 OR NEW."IsArchived" OR NEW."DisabledAt" IS NOT NULL) THEN
            SELECT count(*) INTO v_admins
            FROM public."Users" u
            WHERE u."Role" = 0
              AND u."IsArchived" = false
              AND u."DisabledAt" IS NULL
              AND u."Id" <> OLD."Id";
            IF v_admins = 0 THEN
                RAISE EXCEPTION 'No se puede quitar el último Admin activo'
                    USING ERRCODE = '23514';
            END IF;
        END IF;
    END IF;

    NEW."UpdatedAt" := now();
    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS trg_users_guard_privileges ON public."Users";
CREATE TRIGGER trg_users_guard_privileges
    BEFORE INSERT OR UPDATE ON public."Users"
    FOR EACH ROW EXECUTE FUNCTION app.guard_user_privilege_columns();

-- Los usuarios no se borran físicamente: son el "quién" de la bitácora y de
-- Orders.CreatedByUserId. Se desactivan.
CREATE OR REPLACE FUNCTION app.guard_user_delete()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, app, pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION 'Los usuarios no se eliminan: usar "IsArchived" o "DisabledAt" (el usuario es el actor de la bitácora)'
        USING ERRCODE = '42501';
END
$$;

DROP TRIGGER IF EXISTS trg_users_no_delete ON public."Users";
CREATE TRIGGER trg_users_no_delete
    BEFORE DELETE ON public."Users"
    FOR EACH ROW EXECUTE FUNCTION app.guard_user_delete();

-- ── 6. Alta y baja de cuentas: RPC con chequeo del lado servidor ─────────────
-- El único camino por el que un Admin cambia el rol de otro. Verifica el rol
-- del LLAMADOR contra la base, no contra lo que dice el cliente.

CREATE OR REPLACE FUNCTION public.admin_set_user_role(
    p_user_id uuid,
    p_role_code text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, app, pg_catalog
AS $$
DECLARE
    v_legacy integer;
BEGIN
    IF NOT app.has_role('admin') THEN
        RAISE EXCEPTION 'Operación reservada a Admin' USING ERRCODE = '42501';
    END IF;

    SELECT legacy_int INTO v_legacy FROM app.roles WHERE code = p_role_code;
    IF v_legacy IS NULL THEN
        RAISE EXCEPTION 'Rol desconocido: %', p_role_code USING ERRCODE = '22023';
    END IF;

    UPDATE public."Users" SET "Role" = v_legacy WHERE "Id" = p_user_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Usuario inexistente' USING ERRCODE = 'P0002';
    END IF;
END
$$;

CREATE OR REPLACE FUNCTION public.admin_set_user_enabled(
    p_user_id uuid,
    p_enabled boolean,
    p_reason  text DEFAULT NULL
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, app, pg_catalog
AS $$
BEGIN
    IF NOT app.has_role('admin') THEN
        RAISE EXCEPTION 'Operación reservada a Admin' USING ERRCODE = '42501';
    END IF;

    UPDATE public."Users"
    SET "DisabledAt"     = CASE WHEN p_enabled THEN NULL ELSE now() END,
        "DisabledReason" = CASE WHEN p_enabled THEN NULL ELSE p_reason END
    WHERE "Id" = p_user_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Usuario inexistente' USING ERRCODE = 'P0002';
    END IF;
END
$$;

REVOKE ALL ON FUNCTION public.admin_set_user_role(uuid, text)             FROM PUBLIC;
REVOKE ALL ON FUNCTION public.admin_set_user_enabled(uuid, boolean, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.admin_set_user_role(uuid, text)             TO authenticated;
GRANT EXECUTE ON FUNCTION public.admin_set_user_enabled(uuid, boolean, text) TO authenticated;
