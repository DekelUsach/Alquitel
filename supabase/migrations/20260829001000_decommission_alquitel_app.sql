-- ─────────────────────────────────────────────────────────────────────────────
-- 20260829001000_decommission_alquitel_app
--
-- ⚠️  NO APLICAR HASTA COMPLETAR LA CONMUTACIÓN DEL CLIENTE.
--     Ver docs/SUPABASE_MIGRATION_CONTRACT.md. Esta migración corta la conexión
--     directa a PostgreSQL: cualquier puesto que todavía use el connection
--     string del pooler deja de funcionar en el acto.
--
-- QUÉ ELIMINA Y POR QUÉ
--
-- `alquitel_app` es una cuenta de PostgreSQL con login, DELETE/INSERT/SELECT/
-- UPDATE sobre las 10 tablas del esquema y una política `FOR ALL USING(true)`
-- en cada una. Su contraseña está en `appsettings.local.json` o en una variable
-- de entorno de CADA máquina del equipo. Es decir: la aplicación de escritorio
-- actúa hoy como administrador de la base compartida, y esa credencial:
--
--   * no identifica a nadie (todos los empleados son la misma cuenta);
--   * no se puede revocar por persona (rotarla obliga a tocar todos los puestos);
--   * se lleva puesta toda la separación de roles, que solo existe en la UI;
--   * viaja en texto plano en un archivo del disco de cada empleado, legible por
--     cualquier proceso que corra con su usuario de Windows.
--
-- Reemplazo: Supabase Auth (una cuenta por empleado) + PostgREST/RPC con el JWT
-- del usuario + las políticas de 20260829000300_rls_policies.sql.
--
-- ANTES DE CORRER ESTO, verificar:
--   [ ] Todos los empleados tienen cuenta en Supabase Auth y su fila de
--       public."Users" tiene "AuthUserId" cargado.
--       SELECT "Name","Role","AuthUserId" FROM public."Users" WHERE "IsArchived"=false;
--       → ninguna con AuthUserId NULL.
--   [ ] La app de escritorio en producción ya no lee
--       Database:Supabase:ConnectionString.
--   [ ] Ningún puesto tiene la variable de entorno
--       ALQUITEL_Database__Supabase__ConnectionString definida.
--   [ ] Hay al menos un Admin activo (si no, el sistema queda sin quien
--       administre usuarios):
--       SELECT count(*) FROM public."Users"
--        WHERE "Role"=0 AND "IsArchived"=false AND "DisabledAt" IS NULL;
-- ─────────────────────────────────────────────────────────────────────────────

DO $$
DECLARE
    v_sin_auth integer;
    v_admins   integer;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'alquitel_app') THEN
        RAISE NOTICE 'El rol alquitel_app ya no existe: nada que hacer.';
        RETURN;
    END IF;

    SELECT count(*) INTO v_sin_auth
    FROM public."Users"
    WHERE "IsArchived" = false AND "DisabledAt" IS NULL AND "AuthUserId" IS NULL;

    IF v_sin_auth > 0 THEN
        RAISE EXCEPTION
            'Hay % usuario(s) activo(s) sin cuenta de Supabase Auth vinculada ("AuthUserId" NULL). Completá la conmutación antes de dar de baja alquitel_app.',
            v_sin_auth;
    END IF;

    SELECT count(*) INTO v_admins
    FROM public."Users"
    WHERE "Role" = 0 AND "IsArchived" = false AND "DisabledAt" IS NULL;

    IF v_admins = 0 THEN
        RAISE EXCEPTION 'No hay ningún Admin activo: dar de baja alquitel_app dejaría el sistema sin administración.';
    END IF;
END
$$;

-- ── 1. Se van las políticas del modelo compartido ────────────────────────────
DROP POLICY IF EXISTS app_full_access_clients            ON public."Clients";
DROP POLICY IF EXISTS app_full_access_products           ON public."Products";
DROP POLICY IF EXISTS app_full_access_locations          ON public."Locations";
DROP POLICY IF EXISTS app_full_access_users              ON public."Users";
DROP POLICY IF EXISTS app_full_access_mobilepermissions  ON public."UserMobilePermissions";
DROP POLICY IF EXISTS app_full_access_orders             ON public."Orders";
DROP POLICY IF EXISTS app_full_access_orderitems         ON public."OrderItems";
DROP POLICY IF EXISTS app_full_access_orderauditevents   ON public."OrderAuditEvents";
DROP POLICY IF EXISTS app_full_access_eventtemplates     ON public."EventTemplates";
DROP POLICY IF EXISTS app_full_access_orderapprovals     ON public."OrderApprovals";

-- ── 2. Se va la credencial ───────────────────────────────────────────────────
DO $$
DECLARE t record;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'alquitel_app') THEN RETURN; END IF;

    FOR t IN SELECT c.oid::regclass AS rel
             FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'public' AND c.relkind IN ('r','v','m','p')
    LOOP
        EXECUTE format('REVOKE ALL ON %s FROM alquitel_app', t.rel);
    END LOOP;

    EXECUTE 'REVOKE ALL ON SCHEMA public FROM alquitel_app';
    EXECUTE 'REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM alquitel_app';
    -- Login cortado antes del DROP: si quedara alguna dependencia y el DROP
    -- fallara, la cuenta igual no puede conectarse.
    EXECUTE 'ALTER ROLE alquitel_app NOLOGIN';
    EXECUTE 'DROP OWNED BY alquitel_app';
    EXECUTE 'DROP ROLE alquitel_app';
END
$$;

-- ── 3. Sobre FORCE ROW LEVEL SECURITY ────────────────────────────────────────
-- Deliberadamente NO se activa. `postgres` es el dueño de las 10 tablas y, sin
-- FORCE, salta RLS — que es exactamente lo que hace funcionar a las funciones
-- SECURITY DEFINER de este directorio (app.current_user_id, respond_approval,
-- get_approval_page, los RPC de admin). Con FORCE habría que escribir políticas
-- para el propio dueño y cualquier olvido rompería el portal público en
-- silencio, cambiando un riesgo real por uno peor.
--
-- Lo que sí importa es que `postgres` no tenga login desde afuera: en Supabase
-- su contraseña vive solo en el panel del proyecto y no se distribuye. Ese es el
-- control que reemplaza a FORCE.

-- ── 4. Verificación ──────────────────────────────────────────────────────────
-- SELECT rolname FROM pg_roles WHERE rolname='alquitel_app';  → 0 filas
-- SELECT tablename, policyname, roles FROM pg_policies WHERE schemaname='public';
--   → ninguna política con {alquitel_app}
