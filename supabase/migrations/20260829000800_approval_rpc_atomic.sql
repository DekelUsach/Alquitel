-- ─────────────────────────────────────────────────────────────────────────────
-- 20260829000800_approval_rpc_atomic
--
-- CARRERA CONFIRMADA EN LA EDGE FUNCTION ACTUAL
-- (supabase/functions/aprobar/index.ts, rama POST):
--
--     const { error: upErr } = await supabase.from("OrderApprovals")
--         .update({ Status: ..., RespondedAt: ..., ClientIp: ... })
--         .eq("Id", approval.Id)
--         .eq("Status", APPROVAL_PENDING);   // "idempotencia ante doble clic"
--
--     if (!upErr) {                          // ← acá está el bug
--         await supabase.from("Orders").update({ Status: ... }).eq("Id", ...);
--     }
--     return html("Muchas gracias ... Aprobado");
--
-- Un UPDATE que no matchea ninguna fila NO devuelve error en PostgREST: devuelve
-- 0 filas y `upErr` queda null. Entonces:
--
--   1. Dos pedidos simultáneos (doble clic, o "Aprobar" en una pestaña y
--      "Rechazar" en otra) leen ambos `Status = Pending` en el SELECT inicial.
--   2. El primero actualiza la aprobación a Approved y la orden a Approved.
--   3. El segundo hace su UPDATE condicional: 0 filas, sin error. Igual entra al
--      `if (!upErr)` y pisa `Orders.Status` con Rejected.
--   → La aprobación queda "Approved" y la orden "Rejected". Estados
--     contradictorios, y al cliente se le muestra "Rechazado" en los dos casos.
--   4. Peor aún: si el UPDATE de OrderApprovals falla de verdad, la función
--      igual responde "Muchas gracias / Aprobado". Éxito sin escritura.
--
-- Además las dos escrituras son dos requests HTTP independientes: no hay
-- transacción. Un corte entre la 1 y la 2 deja el token consumido y la orden sin
-- actualizar, sin forma de reintentar (el token ya no está pendiente).
--
-- SOLUCIÓN
--
-- Una sola función, una sola transacción, con:
--   * SELECT ... FOR UPDATE que serializa los pedidos concurrentes sobre la fila
--     del token (el segundo espera al primero y después ve el estado real).
--   * GET DIAGNOSTICS ROW_COUNT verificado explícitamente en las dos escrituras;
--     si no es exactamente 1, se levanta excepción y se revierte TODO — incluido
--     el consumo del token, que queda disponible para reintentar.
--   * Estados permitidos validados en la base (la orden tiene que estar en
--     Borrador o Aprobada para admitir la respuesta del cliente).
--   * Respuestas idempotentes: repetir la misma acción devuelve el mismo
--     resultado sin volver a escribir.
--   * Vencimiento, revocación y límite de intentos, todo del lado servidor.
--   * Registro en la bitácora del veredicto del cliente.
--
-- Se ejecuta con `anon`, no con el rol de servicio: la Edge Function deja de
-- necesitar el SERVICE_ROLE_KEY. El token del link sigue siendo la única
-- autorización, pero ahora la lógica está en la base y no en TypeScript.
-- ─────────────────────────────────────────────────────────────────────────────

-- Estados de OrderApprovals: 0 Pending · 1 Approved · 2 Rejected
-- Estados de Orders:         0 Draft · 1 Approved · 2 SentToOF · 3 SentToOT
--                            4 Archived · 5 Rejected

CREATE OR REPLACE FUNCTION app.approval_token_hash(p_token text)
RETURNS bytea
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog
AS $$
    SELECT sha256(convert_to(lower(btrim(p_token)), 'UTF8'))
$$;

-- Vigencia del link. Debe coincidir con Alquitel.Core/Security/
-- ApprovalTokenPolicy.MaxAgeDays y con APPROVAL_MAX_AGE_DAYS de la Edge Function.
CREATE OR REPLACE FUNCTION app.approval_max_age_days()
RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 30 $$;

-- Días que el presupuesto respondido sigue visible como comprobante para el
-- cliente. Pasado el plazo, la página pública se reduce al sello. La política
-- completa (y la purga de IP) está en 20260829000900_approval_retention.sql.
CREATE OR REPLACE FUNCTION app.approval_detail_days()
RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT 90 $$;

CREATE OR REPLACE FUNCTION app.approval_detail_visible(
    p_status        integer,
    p_responded_at  timestamp,
    p_anonymized_at timestamptz
)
RETURNS boolean
LANGUAGE sql
STABLE
SET search_path = app, pg_catalog
AS $$
    SELECT p_anonymized_at IS NULL
       AND (p_status = 0
            OR p_responded_at IS NULL
            OR p_responded_at > (now() AT TIME ZONE 'UTC')
                                - make_interval(days => app.approval_detail_days()))
$$;

-- ─────────────────────────────────────────────────────────────────────────────
-- respond_approval — consumo atómico del token
-- ─────────────────────────────────────────────────────────────────────────────
-- Devuelve jsonb con "outcome" ∈
--   ok | already_same | already_other | not_found | revoked | expired
--   | invalid_action | rate_limited | order_state_conflict
-- Nunca devuelve el token ni detalles internos de la base.

CREATE OR REPLACE FUNCTION public.respond_approval(
    p_token     text,
    p_action    text,
    p_client_ip text DEFAULT NULL
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, app, pg_catalog
AS $$
DECLARE
    v_hash        bytea;
    v_ap          public."OrderApprovals"%ROWTYPE;
    v_rows        integer;
    v_target      integer;
    v_order_stat  integer;
    v_now         timestamptz := now();
    v_budget      text;
    v_ip_bucket   text;
BEGIN
    IF p_action IS NULL OR p_action NOT IN ('approve', 'reject') THEN
        RETURN jsonb_build_object('outcome', 'invalid_action');
    END IF;

    IF p_token IS NULL OR btrim(p_token) = '' THEN
        RETURN jsonb_build_object('outcome', 'not_found');
    END IF;

    v_hash := app.approval_token_hash(p_token);

    -- Límite de intentos ANTES de tocar la tabla: por IP (contra el barrido de
    -- tokens desde un origen) y por token (contra el martilleo de un link).
    v_ip_bucket := 'respond:ip:' || coalesce(p_client_ip, 'desconocida');
    IF NOT app.rate_limit_hit(v_ip_bucket, 20, interval '10 minutes')
       OR NOT app.rate_limit_hit('respond:token:' || encode(v_hash, 'hex'), 10, interval '10 minutes')
    THEN
        RETURN jsonb_build_object('outcome', 'rate_limited');
    END IF;

    -- FOR UPDATE: acá se serializan los pedidos concurrentes. El segundo espera
    -- a que el primero termine y después relee la fila YA actualizada, así que
    -- entra por la rama de idempotencia en vez de pisar el resultado.
    SELECT * INTO v_ap
    FROM   public."OrderApprovals"
    WHERE  "TokenHash" = v_hash
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN jsonb_build_object('outcome', 'not_found');
    END IF;

    SELECT o."BudgetNumber", o."Status" INTO v_budget, v_order_stat
    FROM   public."Orders" o WHERE o."Id" = v_ap."OrderId";

    -- Ya respondido: idempotente. Misma acción → mismo resultado, sin reescribir.
    -- Acción distinta → 409, el veredicto original se respeta.
    IF v_ap."Status" <> 0 THEN
        RETURN jsonb_build_object(
            'outcome', CASE
                WHEN (v_ap."Status" = 1 AND p_action = 'approve')
                  OR (v_ap."Status" = 2 AND p_action = 'reject')
                THEN 'already_same' ELSE 'already_other' END,
            'status',        v_ap."Status",
            'responded_at',  v_ap."RespondedAt",
            'budget_number', v_budget);
    END IF;

    IF v_ap."RevokedAt" IS NOT NULL THEN
        RETURN jsonb_build_object('outcome', 'revoked', 'budget_number', v_budget);
    END IF;

    IF v_ap."CreatedAt" IS NOT NULL
       AND v_ap."CreatedAt" < (v_now AT TIME ZONE 'UTC') - make_interval(days => app.approval_max_age_days())
    THEN
        RETURN jsonb_build_object('outcome', 'expired',
                                  'max_age_days', app.approval_max_age_days(),
                                  'budget_number', v_budget);
    END IF;

    v_target := CASE WHEN p_action = 'approve' THEN 1 ELSE 2 END;

    -- Bloque interno con manejador propio: si las escrituras chocan, se revierte
    -- SOLO este tramo (el token queda pendiente y reintentable) y el conteo del
    -- rate limit de más arriba se conserva. Si el manejador envolviera toda la
    -- función, un token en conflicto podría martillearse sin límite.
    BEGIN
        -- ── Escritura 1: consumir el token ──────────────────────────────────
        UPDATE public."OrderApprovals"
        SET    "Status"      = v_target,
               "RespondedAt" = (v_now AT TIME ZONE 'UTC'),
               "ClientIp"    = nullif(left(coalesce(p_client_ip, ''), 45), '')
        WHERE  "Id" = v_ap."Id"
          AND  "Status" = 0;

        GET DIAGNOSTICS v_rows = ROW_COUNT;
        IF v_rows <> 1 THEN
            -- Con FOR UPDATE esto no debería pasar nunca; si pasa es que alguien
            -- escribió por otro camino. Se aborta: el token no se consume.
            RAISE EXCEPTION USING ERRCODE = 'AL409', MESSAGE = 'approval_not_consumed',
                DETAIL = format('filas afectadas=%s', v_rows);
        END IF;

        -- ── Escritura 2: estado de la orden, misma transacción ──────────────
        -- Solo desde Borrador(0) o Aprobada(1). Si el presupuesto ya se despachó
        -- (OF/OT) o se archivó, la respuesta del cliente llega tarde y no puede
        -- revertir el circuito operativo: se aborta y el link sigue utilizable.
        -- RowVersion rota para que cualquier puesto con la orden abierta detecte
        -- el cambio por concurrencia optimista en vez de pisarlo.
        UPDATE public."Orders"
        SET    "Status"     = CASE WHEN p_action = 'approve' THEN 1 ELSE 5 END,
               "RowVersion" = gen_random_uuid()
        WHERE  "Id" = v_ap."OrderId"
          AND  "Status" IN (0, 1);

        GET DIAGNOSTICS v_rows = ROW_COUNT;
        IF v_rows <> 1 THEN
            RAISE EXCEPTION USING ERRCODE = 'AL409', MESSAGE = 'order_state_conflict',
                DETAIL = format('La orden %s está en estado %s y no admite la respuesta del cliente',
                                coalesce(v_budget, '?'), coalesce(v_order_stat::text, '?'));
        END IF;

        -- ── Bitácora ────────────────────────────────────────────────────────
        INSERT INTO public."OrderAuditEvents" ("Id", "OrderId", "UserName", "UserId", "EventType", "Detail")
        VALUES (gen_random_uuid(), v_ap."OrderId", 'Cliente (portal de aprobación)', NULL,
                CASE WHEN p_action = 'approve' THEN 'Aprobado por el cliente' ELSE 'Rechazado por el cliente' END,
                format('Link de aprobación %s · IP %s', v_ap."Id", coalesce(p_client_ip, 'desconocida')));

    EXCEPTION WHEN SQLSTATE 'AL409' THEN
        -- Ni el mensaje interno ni el DETAIL viajan al navegador: solo el código.
        RETURN jsonb_build_object('outcome', SQLERRM);
    END;

    RETURN jsonb_build_object(
        'outcome',       'ok',
        'status',        v_target,
        'responded_at',  (v_now AT TIME ZONE 'UTC'),
        'budget_number', v_budget);
END
$$;

-- ─────────────────────────────────────────────────────────────────────────────
-- get_approval_page — todo lo que la página pública necesita, en un jsonb
-- ─────────────────────────────────────────────────────────────────────────────
-- Que la selección de campos viva acá y no en TypeScript importa: la lista de
-- columnas expuestas al público es parte del esquema y se revisa con él.
-- NUNCA se incluyen: Clients.InternalNotes, Clients.SpecialDiscountPercent,
-- Products.Cost, Orders.AdminName, Orders.CreatedByUserId, ni el token.

CREATE OR REPLACE FUNCTION public.get_approval_page(
    p_token     text,
    p_client_ip text DEFAULT NULL
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
-- VOLATILE a propósito: cuenta el intento en el rate limit, así que escribe.
SET search_path = public, app, pg_catalog
AS $$
DECLARE
    v_hash     bytea;
    v_ap       public."OrderApprovals"%ROWTYPE;
    v_order    public."Orders"%ROWTYPE;
    v_now      timestamptz := now();
    v_detail   boolean;
    v_result   jsonb;
BEGIN
    IF p_token IS NULL OR btrim(p_token) = '' THEN
        RETURN jsonb_build_object('outcome', 'not_found');
    END IF;

    v_hash := app.approval_token_hash(p_token);

    IF NOT app.rate_limit_hit('page:ip:' || coalesce(p_client_ip, 'desconocida'), 120, interval '10 minutes') THEN
        RETURN jsonb_build_object('outcome', 'rate_limited');
    END IF;

    SELECT * INTO v_ap FROM public."OrderApprovals" WHERE "TokenHash" = v_hash;
    IF NOT FOUND THEN
        RETURN jsonb_build_object('outcome', 'not_found');
    END IF;

    IF v_ap."RevokedAt" IS NOT NULL THEN
        RETURN jsonb_build_object('outcome', 'revoked');
    END IF;

    IF v_ap."Status" = 0
       AND v_ap."CreatedAt" < (v_now AT TIME ZONE 'UTC') - make_interval(days => app.approval_max_age_days())
    THEN
        RETURN jsonb_build_object('outcome', 'expired', 'max_age_days', app.approval_max_age_days());
    END IF;

    -- Retención: el detalle completo (PII + importes) se sirve mientras el link
    -- está pendiente, y como comprobante durante `app.approval_detail_days()`
    -- después de la respuesta. Pasado ese plazo la página queda reducida al
    -- sello (número y veredicto) — ver 20260829000900_approval_retention.sql.
    v_detail := app.approval_detail_visible(v_ap."Status", v_ap."RespondedAt", v_ap."AnonymizedAt");

    SELECT * INTO v_order FROM public."Orders" WHERE "Id" = v_ap."OrderId";
    IF NOT FOUND THEN
        RETURN jsonb_build_object('outcome', 'order_missing');
    END IF;

    v_result := jsonb_build_object(
        'outcome',          'ok',
        'detail_visible',   v_detail,
        'approval_status',  v_ap."Status",
        'responded_at',     v_ap."RespondedAt",
        'budget_number',    v_order."BudgetNumber",
        'created_date',     v_order."CreatedDate");

    IF NOT v_detail THEN
        RETURN v_result;
    END IF;

    RETURN v_result || jsonb_build_object(
        'event_date',       v_order."EventDate",
        'event_end_date',   v_order."EventEndDate",
        'comments',         v_order."Comments",
        'discount_percent', v_order."DiscountPercent",
        'discount_amount',  v_order."DiscountAmount",
        'add_vat',          v_order."AddVat",
        'client', (
            SELECT jsonb_build_object(
                       'company_name', c."CompanyName",
                       'cuit',         c."Cuit",
                       'contact_name', c."ContactName",
                       'email',        c."Email",
                       'phone',        c."Phone")
            FROM public."Clients" c WHERE c."Id" = v_order."ClientId"),
        'location', (
            SELECT l."Name" FROM public."Locations" l WHERE l."Id" = v_order."LocationId"),
        'items', coalesce((
            SELECT jsonb_agg(jsonb_build_object(
                       'quantity',             i."Quantity",
                       'unit_price',           i."UnitPrice",
                       'dias',                 i."Dias",
                       'technical_notes',      i."TechnicalNotes",
                       'custom_fields_json',   i."CustomFieldsJson",
                       'requested_measure',    i."RequestedMeasure",
                       -- Snapshot congelado al momento de la aceptación; si falta
                       -- (ítem legado) se cae al catálogo, sin el costo.
                       'description', coalesce(
                           nullif(btrim(i."DescriptionSnapshot"), ''),
                           (SELECT p."Description" FROM public."Products" p WHERE p."Id" = i."ProductId"),
                           'Producto'))
                   ORDER BY i."Id")
            FROM public."OrderItems" i WHERE i."OrderId" = v_order."Id"), '[]'::jsonb));
END
$$;

-- ── Permisos ─────────────────────────────────────────────────────────────────
-- anon: la Edge Function las llama con la clave pública. El token del link es la
-- credencial; el rate limit y la validación viven dentro de las funciones.
-- Ninguna otra función de `public` queda expuesta a anon.

REVOKE ALL ON FUNCTION public.respond_approval(text, text, text)  FROM PUBLIC;
REVOKE ALL ON FUNCTION public.get_approval_page(text, text)       FROM PUBLIC;
REVOKE ALL ON FUNCTION app.approval_token_hash(text)              FROM PUBLIC;
REVOKE ALL ON FUNCTION app.rate_limit_hit(text, integer, interval) FROM PUBLIC;

GRANT EXECUTE ON FUNCTION public.respond_approval(text, text, text) TO anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.get_approval_page(text, text)      TO anon, authenticated, service_role;
