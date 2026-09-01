-- ─────────────────────────────────────────────────────────────────────────────
-- 20260829000900_approval_retention
--
-- El portal de aprobación publica en internet, detrás de un secreto de portador,
-- datos personales de un tercero que nunca aceptó nada: razón social, CUIT,
-- nombre y apellido del contacto, mail, teléfono, precios negociados. Ese link
-- vive para siempre en la bandeja de entrada del cliente y en cualquier reenvío
-- suyo. Hoy no hay ninguna política de retención: respondido o no, la página
-- sigue sirviendo todo indefinidamente.
--
-- POLÍTICA
--
--   Pendiente         → detalle completo hasta el vencimiento (30 días).
--   Respondido        → detalle completo 90 días más, como comprobante de lo que
--                       el cliente aceptó (es el motivo por el que el link sigue
--                       vivo después de responder).
--   Respondido +90d   → la página se reduce al sello: número de presupuesto,
--                       veredicto y fecha. Sin PII, sin importes, sin ítems.
--   Respondido +180d  → se anonimiza la IP registrada (se conserva solo el /24
--                       para estadística de abuso) y se marca "AnonymizedAt".
--   Sin responder,
--   vencido +180d     → el link se revoca definitivamente.
--
-- Los plazos se centralizan en funciones IMMUTABLE para que la página, la app y
-- la purga no puedan divergir.
--
-- Nada de esto borra la fila: `OrderApprovals` es evidencia de que el cliente
-- aprobó, y esa evidencia se conserva. Lo que se retira es la exposición pública
-- y el dato personal que ya no cumple ninguna función.
-- ─────────────────────────────────────────────────────────────────────────────

CREATE OR REPLACE FUNCTION app.approval_anonymize_days()
RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 180 $$;

-- ── Anonimización de la IP ───────────────────────────────────────────────────
-- La IP se guarda para poder demostrar desde dónde se aprobó si hay una disputa.
-- Pasado el plazo deja de ser necesaria como dato individual: se conserva el
-- prefijo /24 (IPv4) o /48 (IPv6), que sirve para detectar abuso y ya no
-- identifica a una persona.

CREATE OR REPLACE FUNCTION app.anonymize_ip(p_ip text)
RETURNS text
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog
AS $$
    SELECT CASE
        WHEN p_ip IS NULL OR btrim(p_ip) = '' THEN NULL
        WHEN p_ip LIKE '%:%' THEN
            -- IPv6: se conservan los tres primeros grupos.
            array_to_string((string_to_array(p_ip, ':'))[1:3], ':') || '::/48'
        WHEN p_ip ~ '^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$' THEN
            array_to_string((string_to_array(p_ip, '.'))[1:3], '.') || '.0/24'
        ELSE NULL
    END
$$;

-- ── Purga programada ─────────────────────────────────────────────────────────
-- Idempotente: correrla dos veces no cambia nada. Devuelve cuántas filas tocó,
-- para poder verificar que efectivamente corre.

CREATE OR REPLACE FUNCTION app.purge_approval_pii()
RETURNS TABLE (anonimizadas integer, revocadas integer, rate_limit_purgado integer)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, app, pg_catalog
AS $$
DECLARE
    v_anon    integer := 0;
    v_revoked integer := 0;
    v_rl      integer := 0;
BEGIN
    -- 1. Anonimizar respuestas viejas.
    UPDATE public."OrderApprovals"
    SET    "ClientIp"     = app.anonymize_ip("ClientIp"),
           "AnonymizedAt" = now()
    WHERE  "Status" <> 0
      AND  "AnonymizedAt" IS NULL
      AND  "RespondedAt" < (now() AT TIME ZONE 'UTC')
                           - make_interval(days => app.approval_anonymize_days());
    GET DIAGNOSTICS v_anon = ROW_COUNT;

    -- 2. Revocar links que vencieron hace mucho y nunca se usaron: dejan de ser
    --    una superficie viva en bandejas de entrada ajenas.
    UPDATE public."OrderApprovals"
    SET    "RevokedAt" = now()
    WHERE  "Status" = 0
      AND  "RevokedAt" IS NULL
      AND  "CreatedAt" < (now() AT TIME ZONE 'UTC')
                         - make_interval(days => app.approval_max_age_days() + app.approval_anonymize_days());
    GET DIAGNOSTICS v_revoked = ROW_COUNT;

    -- 3. Limpiar cubetas de rate limit ya vencidas (la tabla no debe crecer sin
    --    techo con una cubeta por IP que pegó una vez).
    DELETE FROM app.approval_rate_limit WHERE window_start < now() - interval '1 day';
    GET DIAGNOSTICS v_rl = ROW_COUNT;

    RETURN QUERY SELECT v_anon, v_revoked, v_rl;
END
$$;

REVOKE ALL ON FUNCTION app.purge_approval_pii() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.purge_approval_pii() TO service_role;

COMMENT ON FUNCTION app.purge_approval_pii() IS
'Retención del portal público. Programar diaria. Con pg_cron:
   CREATE EXTENSION IF NOT EXISTS pg_cron;
   SELECT cron.schedule(''alquitel-purge-approval-pii'', ''17 4 * * *'',
                        $$SELECT app.purge_approval_pii()$$);
Sin pg_cron, invocarla desde una Edge Function con Supabase Scheduled Functions.';

-- ── Verificación manual ──────────────────────────────────────────────────────
-- SELECT * FROM app.purge_approval_pii();
--   → (0,0,N) en una base recién purgada.
-- SELECT "Id", "Status", "ClientIp", "AnonymizedAt" FROM public."OrderApprovals";
--   → ninguna IP completa en filas con AnonymizedAt no nulo.
