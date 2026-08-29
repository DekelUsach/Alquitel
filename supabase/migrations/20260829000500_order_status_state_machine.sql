-- ─────────────────────────────────────────────────────────────────────────────
-- 20260829000500_order_status_state_machine
--
-- Hoy el estado de un presupuesto es un combo libre en la UI: cualquier estado
-- puede pasar a cualquier otro. Con base compartida eso permite dos cosas que no
-- deberían poder pasar:
--
--   * Emitir una Orden de Trabajo (SentToOT) de un presupuesto que el cliente
--     nunca aprobó — se despacha equipamiento sin respaldo comercial.
--   * Llevar a "Aprobado" un presupuesto que el cliente RECHAZÓ por el portal
--     público, borrando el rastro de la negativa.
--
-- La transición válida se define acá, en la base, y aplica a todo cliente (WPF,
-- mobile, script). Las transiciones se registran además en la bitácora.
--
-- Estados: 0 Draft · 1 Approved · 2 SentToOF · 3 SentToOT · 4 Archived · 5 Rejected
--
--   Draft(0)     → Draft, Approved, Rejected, Archived
--   Approved(1)  → Approved, SentToOF, SentToOT, Rejected, Archived, Draft
--   SentToOF(2)  → SentToOF, SentToOT, Approved, Archived
--   SentToOT(3)  → SentToOT, SentToOF, Archived
--   Rejected(5)  → Rejected, Draft, Archived
--   Archived(4)  → Archived, Draft
--
-- Lo que queda PROHIBIDO y antes se podía hacer:
--   Draft    → SentToOF / SentToOT   (despachar sin aprobación)
--   Rejected → Approved / SentToOF / SentToOT
--   Archived → Approved / SentToOF / SentToOT
--
-- Camino legítimo para revivir un presupuesto rechazado o archivado: pasarlo a
-- Draft y volver a recorrer el circuito. Queda registrado en la bitácora.
--
-- CAMBIO DE COMPORTAMIENTO PARA LA UI (Codex/Antigravity): el combo de estados
-- del armador y del pool debería ofrecer solo los destinos válidos desde el
-- estado actual; hoy los ofrece todos y ahora la base rechazará algunos. La
-- función `public.order_status_transitions(int)` expone la tabla para que la UI
-- arme el combo sin duplicar la regla.
-- ─────────────────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS app.order_status_transitions (
    from_status integer NOT NULL,
    to_status   integer NOT NULL,
    PRIMARY KEY (from_status, to_status)
);

INSERT INTO app.order_status_transitions (from_status, to_status) VALUES
    (0,0),(0,1),(0,5),(0,4),
    (1,1),(1,2),(1,3),(1,5),(1,4),(1,0),
    (2,2),(2,3),(2,1),(2,4),
    (3,3),(3,2),(3,4),
    (4,4),(4,0),
    (5,5),(5,0),(5,4)
ON CONFLICT DO NOTHING;

GRANT SELECT ON app.order_status_transitions TO authenticated;

CREATE OR REPLACE FUNCTION app.enforce_order_status_transition()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = public, app, pg_catalog
AS $$
BEGIN
    IF NEW."Status" IS NOT DISTINCT FROM OLD."Status" THEN
        RETURN NEW;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM app.order_status_transitions t
        WHERE t.from_status = OLD."Status" AND t.to_status = NEW."Status"
    ) THEN
        RAISE EXCEPTION
            'Transición de estado inválida para el presupuesto %: % → %. Pasá primero por Borrador si querés reabrirlo.',
            OLD."BudgetNumber", OLD."Status", NEW."Status"
            USING ERRCODE = '23514';
    END IF;

    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS trg_orders_status_transition ON public."Orders";
CREATE TRIGGER trg_orders_status_transition
    BEFORE UPDATE OF "Status" ON public."Orders"
    FOR EACH ROW EXECUTE FUNCTION app.enforce_order_status_transition();

-- Un presupuesto nace en Borrador o Aprobado (importación de histórico); nunca
-- directamente en "enviado a OT".
CREATE OR REPLACE FUNCTION app.enforce_order_initial_status()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = public, pg_catalog
AS $$
BEGIN
    IF NEW."Status" NOT IN (0, 1, 4, 5) THEN
        RAISE EXCEPTION
            'Un presupuesto no puede crearse en estado %: empieza en Borrador (0).', NEW."Status"
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS trg_orders_initial_status ON public."Orders";
CREATE TRIGGER trg_orders_initial_status
    BEFORE INSERT ON public."Orders"
    FOR EACH ROW EXECUTE FUNCTION app.enforce_order_initial_status();

-- Contrato para la UI: destinos válidos desde un estado dado.
CREATE OR REPLACE FUNCTION public.order_status_transitions(p_from integer)
RETURNS SETOF integer
LANGUAGE sql
STABLE
SET search_path = app, pg_catalog
AS $$
    SELECT to_status FROM app.order_status_transitions
    WHERE from_status = p_from ORDER BY to_status
$$;

REVOKE ALL ON FUNCTION public.order_status_transitions(integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.order_status_transitions(integer) TO authenticated;
