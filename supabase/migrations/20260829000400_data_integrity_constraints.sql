-- ─────────────────────────────────────────────────────────────────────────────
-- 20260829000400_data_integrity_constraints
--
-- Hasta ahora la integridad de los datos vivía en la UI de WPF: los rangos de
-- descuento, las cantidades positivas y la unicidad de nombres se validaban en
-- ViewModels. Con base compartida eso no alcanza — la app no es el único camino
-- a la base, y una validación de UI no sobrevive a un bug, a un reintento del
-- outbox ni a un cliente distinto (la app mobile).
--
-- Todas las restricciones acá son verificables y determinísticas. Ninguna
-- depende del rol ni de la sesión.
--
-- Diferencias con SQLite: SQLite acepta CHECK y índices únicos parciales con la
-- misma sintaxis, pero NO tiene `ALTER TABLE ... ADD CONSTRAINT`. En el motor
-- local esto se expresa en el modelo de EF Core (Codex) o al recrear la tabla.
-- Es el motivo por el que este archivo es específico de PostgreSQL.
-- ─────────────────────────────────────────────────────────────────────────────

-- Helper local: agrega una CHECK solo si no existe, para poder re-correr.
CREATE OR REPLACE FUNCTION app.add_check_if_absent(
    p_table text, p_name text, p_expr text
) RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = p_name) THEN
        EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I CHECK (%s)', p_table, p_name, p_expr);
    END IF;
END
$$;

-- ── Orders ───────────────────────────────────────────────────────────────────

SELECT app.add_check_if_absent('public."Orders"', 'CK_Orders_Status_valido',
    '"Status" BETWEEN 0 AND 5');

SELECT app.add_check_if_absent('public."Orders"', 'CK_Orders_BudgetNumber_no_vacio',
    'btrim("BudgetNumber") <> ''''');

-- Un descuento del 350% o negativo no es un descuento: es un error de carga o
-- una manipulación de importes.
SELECT app.add_check_if_absent('public."Orders"', 'CK_Orders_DiscountPercent_rango',
    '"DiscountPercent" >= 0 AND "DiscountPercent" <= 100');

SELECT app.add_check_if_absent('public."Orders"', 'CK_Orders_DiscountAmount_no_negativo',
    '"DiscountAmount" >= 0');

-- El evento no puede terminar antes de empezar.
SELECT app.add_check_if_absent('public."Orders"', 'CK_Orders_rango_evento',
    '"EventEndDate" IS NULL OR "EventDate" IS NULL OR "EventEndDate" >= "EventDate"');

-- ── OrderItems ───────────────────────────────────────────────────────────────
-- Cantidad o días en 0/negativo dan totales absurdos (y un total negativo es un
-- vector de manipulación de importes en un presupuesto ya aprobado).

SELECT app.add_check_if_absent('public."OrderItems"', 'CK_OrderItems_Quantity_positiva',
    '"Quantity" > 0');
SELECT app.add_check_if_absent('public."OrderItems"', 'CK_OrderItems_Dias_positivos',
    '"Dias" > 0');
SELECT app.add_check_if_absent('public."OrderItems"', 'CK_OrderItems_UnitPrice_no_negativo',
    '"UnitPrice" >= 0');

-- ── Products / Clients ───────────────────────────────────────────────────────

SELECT app.add_check_if_absent('public."Products"', 'CK_Products_BasePrice_no_negativo',
    '"BasePrice" >= 0');
SELECT app.add_check_if_absent('public."Products"', 'CK_Products_Cost_no_negativo',
    '"Cost" IS NULL OR "Cost" >= 0');
SELECT app.add_check_if_absent('public."Products"', 'CK_Products_Description_no_vacia',
    'btrim("Description") <> ''''');
SELECT app.add_check_if_absent('public."Products"', 'CK_Products_Stock_no_negativo',
    '"StockQuantity" IS NULL OR "StockQuantity" >= 0');

SELECT app.add_check_if_absent('public."Clients"', 'CK_Clients_CompanyName_no_vacio',
    'btrim("CompanyName") <> ''''');
SELECT app.add_check_if_absent('public."Clients"', 'CK_Clients_DescuentoEspecial_rango',
    '"SpecialDiscountPercent" IS NULL OR ("SpecialDiscountPercent" >= 0 AND "SpecialDiscountPercent" <= 100)');

-- ── Locations ────────────────────────────────────────────────────────────────
-- La UI ya normaliza el nombre (LocationNameNormalizer), pero la unicidad tiene
-- que estar en la base: dos puestos de trabajo pueden crear "Feria del Libro" y
-- "feria del libro " en el mismo segundo y la UI no ve la carrera.

SELECT app.add_check_if_absent('public."Locations"', 'CK_Locations_Name_no_vacio',
    'btrim("Name") <> ''''');

DO $$
DECLARE
    v_dups text;
BEGIN
    SELECT string_agg(DISTINCT lower(btrim("Name")), ', ')
    INTO   v_dups
    FROM   public."Locations"
    GROUP  BY lower(btrim("Name"))
    HAVING count(*) > 1;

    IF v_dups IS NOT NULL THEN
        RAISE EXCEPTION
            'Hay ubicaciones duplicadas (ignorando mayúsculas y espacios): %. Unificalas antes de aplicar esta migración.',
            v_dups;
    END IF;
END
$$;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_Locations_Name_normalizado"
    ON public."Locations" (lower(btrim("Name")));

-- ── Users / bitácora ─────────────────────────────────────────────────────────

SELECT app.add_check_if_absent('public."Users"', 'CK_Users_Name_no_vacio',
    'btrim("Name") <> ''''');

SELECT app.add_check_if_absent('public."OrderAuditEvents"', 'CK_Audit_EventType_no_vacio',
    'btrim("EventType") <> ''''');

-- ── OrderApprovals ───────────────────────────────────────────────────────────
-- Máquina de estados del link: 0 Pending · 1 Approved · 2 Rejected. Un link
-- respondido SIEMPRE tiene fecha de respuesta, y uno pendiente nunca la tiene.
-- Sin esto, un UPDATE parcial deja "aprobado sin fecha" y la auditoría del
-- consentimiento del cliente pierde valor probatorio.

SELECT app.add_check_if_absent('public."OrderApprovals"', 'CK_Approvals_Status_valido',
    '"Status" BETWEEN 0 AND 2');

SELECT app.add_check_if_absent('public."OrderApprovals"', 'CK_Approvals_RespondedAt_coherente',
    '("Status" = 0 AND "RespondedAt" IS NULL) OR ("Status" <> 0 AND "RespondedAt" IS NOT NULL)');

DROP FUNCTION IF EXISTS app.add_check_if_absent(text, text, text);
