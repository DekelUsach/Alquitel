-- ─────────────────────────────────────────────────────────────────────────────
-- 20260829000600_audit_append_only
--
-- La bitácora es el único registro de quién cambió qué. Hoy `alquitel_app` —la
-- credencial que tiene TODA máquina del equipo— tiene DELETE y UPDATE sobre
-- "OrderAuditEvents", más una política `FOR ALL USING(true)`. O sea: el mismo
-- usuario que altera un precio puede borrar la línea que lo registra. Una
-- bitácora que el auditado puede editar no es una bitácora.
--
-- Además el "actor" lo escribe el cliente (UserName/UserId vienen del proceso
-- WPF), así que también se puede firmar con el nombre de otro.
--
-- Esta migración:
--   1. Quita UPDATE/DELETE/TRUNCATE del rol de la app (efecto inmediato, sin
--      cambios en el cliente: `EfOrderAuditService` solo hace INSERT, así que
--      no rompe nada — verificado en el código).
--   2. Bloquea UPDATE/DELETE con triggers, que también frenan al dueño de la
--      tabla y al rol de servicio de Supabase (los GRANT no los alcanzan).
--   3. Deriva el actor y la fecha en el SERVIDOR cuando hay sesión de Supabase
--      Auth: el cliente ya no puede firmar un evento con otra identidad.
-- ─────────────────────────────────────────────────────────────────────────────

-- ── 1. Sin permiso de tabla no hay UPDATE ni DELETE ─────────────────────────
-- Se hace condicional porque el rol `alquitel_app` es propio de este proyecto y
-- puede no existir en un entorno recién creado desde las migraciones.

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'alquitel_app') THEN
        REVOKE UPDATE, DELETE, TRUNCATE ON public."OrderAuditEvents" FROM alquitel_app;
    END IF;
END
$$;

REVOKE UPDATE, DELETE, TRUNCATE ON public."OrderAuditEvents" FROM authenticated, anon;

-- ── 2. Inmutabilidad, también para el dueño y el rol de servicio ────────────
-- Un GRANT no frena al dueño (`postgres`) ni al rol de servicio, que tiene
-- BYPASSRLS. Un trigger sí: corre siempre.
-- Para una purga legítima por retención hay que deshabilitar el
-- trigger a mano y queda registrado en los logs del proyecto.

-- search_path fijo aunque la función no referencie ningún objeto: es la regla
-- para todo lo que corre dentro de un trigger. Si mañana alguien le agrega una
-- consulta, el agujero ya estaría abierto. El linter de Supabase lo marca.
CREATE OR REPLACE FUNCTION app.deny_audit_mutation()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    RAISE EXCEPTION
        'La bitácora es append-only: no se puede % una fila de OrderAuditEvents.', lower(TG_OP)
        USING ERRCODE = '42501';
END
$$;

DROP TRIGGER IF EXISTS trg_audit_no_update ON public."OrderAuditEvents";
CREATE TRIGGER trg_audit_no_update
    BEFORE UPDATE ON public."OrderAuditEvents"
    FOR EACH ROW EXECUTE FUNCTION app.deny_audit_mutation();

DROP TRIGGER IF EXISTS trg_audit_no_delete ON public."OrderAuditEvents";
CREATE TRIGGER trg_audit_no_delete
    BEFORE DELETE ON public."OrderAuditEvents"
    FOR EACH ROW EXECUTE FUNCTION app.deny_audit_mutation();

-- TRUNCATE no dispara triggers FOR EACH ROW ni pasa por RLS: necesita el suyo.
DROP TRIGGER IF EXISTS trg_audit_no_truncate ON public."OrderAuditEvents";
CREATE TRIGGER trg_audit_no_truncate
    BEFORE TRUNCATE ON public."OrderAuditEvents"
    FOR EACH STATEMENT EXECUTE FUNCTION app.deny_audit_mutation();

-- ── 3. El actor lo pone el servidor ──────────────────────────────────────────
-- Con sesión de Supabase Auth, UserId/UserName/Timestamp se sobreescriben con lo
-- que dice la base sobre quién está llamando. Lo que mande el cliente en esos
-- campos se ignora. Sin JWT (conexión directa legada) se conserva lo enviado,
-- porque no hay identidad que verificar — otra razón para completar la
-- conmutación de 20260829001000.

CREATE OR REPLACE FUNCTION app.stamp_audit_actor()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, app, pg_catalog
AS $$
DECLARE
    v_user_id uuid;
BEGIN
    NEW."Timestamp" := now();

    IF auth.uid() IS NOT NULL THEN
        v_user_id := app.current_user_id();
        IF v_user_id IS NULL THEN
            RAISE EXCEPTION 'Sesión sin usuario de aplicación activo: no se puede registrar en la bitácora'
                USING ERRCODE = '42501';
        END IF;
        NEW."UserId"   := v_user_id;
        NEW."UserName" := (SELECT u."Name" FROM public."Users" u WHERE u."Id" = v_user_id);
    END IF;

    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS trg_audit_stamp_actor ON public."OrderAuditEvents";
CREATE TRIGGER trg_audit_stamp_actor
    BEFORE INSERT ON public."OrderAuditEvents"
    FOR EACH ROW EXECUTE FUNCTION app.stamp_audit_actor();

-- ── 4. La bitácora también registra el veredicto del cliente final ──────────
-- Aprobar/rechazar por el portal público es un cambio de estado del presupuesto
-- como cualquier otro y tiene que dejar rastro. Lo escribe el RPC atómico
-- (20260829000800) con el actor "Cliente (portal de aprobación)".

CREATE INDEX IF NOT EXISTS "IX_OrderAuditEvents_Timestamp"
    ON public."OrderAuditEvents" ("Timestamp" DESC);
