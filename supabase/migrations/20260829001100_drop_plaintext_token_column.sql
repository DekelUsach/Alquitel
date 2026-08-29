-- ─────────────────────────────────────────────────────────────────────────────
-- 20260829001100_drop_plaintext_token_column
--
-- ⚠️  APLICAR SOLO DESPUÉS de que la app de escritorio deje de mapear
--     OrderApproval.Token como columna (ver docs/SUPABASE_MIGRATION_CONTRACT.md,
--     punto "Token de aprobación"). Mientras EF siga incluyendo "Token" en sus
--     SELECT/INSERT, dropear la columna rompe la lectura de OrderApprovals.
--
-- Desde 20260829000700 la columna existe pero está siempre en NULL: un trigger
-- calcula el hash y descarta el texto plano. Este paso la elimina para que no
-- quede ni el hueco.
--
-- Después de esto, un token de aprobación NO se puede recuperar de la base por
-- ningún medio. Reenviar un presupuesto emite un link nuevo y revoca el anterior.
-- ─────────────────────────────────────────────────────────────────────────────

DO $$
DECLARE
    v_con_plano integer;
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'OrderApprovals' AND column_name = 'Token'
    ) THEN
        RAISE NOTICE 'La columna "Token" ya no existe: nada que hacer.';
        RETURN;
    END IF;

    EXECUTE 'SELECT count(*) FROM public."OrderApprovals" WHERE "Token" IS NOT NULL'
        INTO v_con_plano;

    IF v_con_plano > 0 THEN
        RAISE EXCEPTION 'Todavía hay % fila(s) con token en texto plano. Correr primero 20260829000700.', v_con_plano;
    END IF;
END
$$;

ALTER TABLE public."OrderApprovals" DROP CONSTRAINT IF EXISTS "CK_Approvals_sin_token_plano";
ALTER TABLE public."OrderApprovals" DROP COLUMN IF EXISTS "Token";

-- El trigger de hash ya no recibe texto plano: a partir de acá exige que el
-- llamador provea "TokenHash" (lo hace public.issue_approval_token).
CREATE OR REPLACE FUNCTION app.hash_approval_token()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = public, pg_catalog
AS $$
BEGIN
    IF NEW."TokenHash" IS NULL THEN
        RAISE EXCEPTION 'OrderApprovals requiere "TokenHash". Emitir el link con public.issue_approval_token(orden).'
            USING ERRCODE = '23502';
    END IF;
    RETURN NEW;
END
$$;

-- ── Emisión del link desde el servidor ───────────────────────────────────────
-- El token se genera EN LA BASE y se devuelve una única vez al llamador
-- autorizado. Así el texto plano nunca se persiste y el cliente no necesita
-- saber cómo se hashea.

CREATE OR REPLACE FUNCTION public.issue_approval_token(p_order_id uuid)
RETURNS TABLE (approval_id uuid, token text)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, app, pg_catalog
AS $$
DECLARE
    v_token uuid := gen_random_uuid();
    v_id    uuid := gen_random_uuid();
BEGIN
    IF NOT app.has_role('admin', 'comercial') THEN
        RAISE EXCEPTION 'Solo Admin o Comercial pueden emitir links de aprobación'
            USING ERRCODE = '42501';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM public."Orders" WHERE "Id" = p_order_id) THEN
        RAISE EXCEPTION 'La orden no está persistida' USING ERRCODE = 'P0002';
    END IF;

    INSERT INTO public."OrderApprovals" ("Id", "OrderId", "TokenHash", "Status", "CreatedAt")
    VALUES (v_id, p_order_id, app.approval_token_hash(v_token::text), 0, (now() AT TIME ZONE 'UTC'));

    -- El trigger AFTER INSERT revoca los links pendientes anteriores de la orden.
    RETURN QUERY SELECT v_id, v_token::text;
END
$$;

REVOKE ALL ON FUNCTION public.issue_approval_token(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.issue_approval_token(uuid) TO authenticated;

COMMENT ON FUNCTION public.issue_approval_token(uuid) IS
'Emite un link de aprobación. Devuelve el token en claro UNA sola vez: la base guarda solo su SHA-256. No loguear el valor devuelto.';
