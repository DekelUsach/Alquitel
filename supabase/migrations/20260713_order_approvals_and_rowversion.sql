-- ─────────────────────────────────────────────────────────────────────────────
-- Migración Supabase (PostgreSQL): portal de aprobación + concurrencia optimista
-- Aplicar UNA vez en el proyecto qgtaugmxmoxtpxvmugvt (SQL Editor o supabase db push).
-- El esquema del servidor se administra con SQL (no con las migraciones EF locales,
-- que son solo para SQLite) — ver DataInitializationService.Initialize().
-- ─────────────────────────────────────────────────────────────────────────────

-- 1. Concurrencia optimista: token que rota en cada guardado de la orden.
--    Guid.Empty en filas existentes = "sin control" (el chequeo se saltea).
ALTER TABLE "Orders"
    ADD COLUMN IF NOT EXISTS "RowVersion" uuid NOT NULL
    DEFAULT '00000000-0000-0000-0000-000000000000';

-- 2. Tabla de links de aprobación (espejo de Alquitel.Core.Entities.OrderApproval).
CREATE TABLE IF NOT EXISTS "OrderApprovals" (
    "Id"          uuid PRIMARY KEY,
    "OrderId"     uuid NOT NULL REFERENCES "Orders" ("Id") ON DELETE RESTRICT,
    "Token"       uuid NOT NULL,
    "Status"      integer NOT NULL DEFAULT 0,          -- 0 Pending / 1 Approved / 2 Rejected
    "CreatedAt"   timestamp NOT NULL,
    "RespondedAt" timestamp NULL,
    "ClientIp"    text NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_OrderApprovals_Token" ON "OrderApprovals" ("Token");
CREATE INDEX IF NOT EXISTS "IX_OrderApprovals_OrderId" ON "OrderApprovals" ("OrderId");

-- 3. Permisos: la app de escritorio se conecta por el pooler con el rol alquitel_app.
GRANT SELECT, INSERT, UPDATE ON "OrderApprovals" TO alquitel_app;

-- 4. RLS: la tabla queda cerrada para anon/authenticated (PostgREST). Solo la
--    service role (que usa la Edge Function "aprobar") y el rol directo alquitel_app
--    (que NO pasa por RLS al ser conexión directa sin FORCE) la tocan.
ALTER TABLE "OrderApprovals" ENABLE ROW LEVEL SECURITY;

CREATE POLICY "app_full_access_orderapprovals" ON "OrderApprovals"
    FOR ALL
    TO alquitel_app
    USING (true)
    WITH CHECK (true);

