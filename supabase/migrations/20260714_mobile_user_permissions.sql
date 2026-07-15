-- ─────────────────────────────────────────────────────────────────────────────
-- Migración Supabase (PostgreSQL): permisos móviles dinámicos para usuarios
-- Aplicar UNA vez en el proyecto qgtaugmxmoxtpxvmugvt (SQL Editor o supabase db push).
-- ─────────────────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "UserMobilePermissions" (
    "UserId" uuid PRIMARY KEY REFERENCES "Users" ("Id") ON DELETE CASCADE,
    "CanManageLocations" boolean NOT NULL DEFAULT FALSE,
    "CanCreateBudgets" boolean NOT NULL DEFAULT FALSE,
    "CanManageClients" boolean NOT NULL DEFAULT FALSE,
    "CanSeeReports" boolean NOT NULL DEFAULT FALSE
);

-- Permisos
GRANT SELECT, INSERT, UPDATE, DELETE ON "UserMobilePermissions" TO alquitel_app;

-- Habilitar RLS
ALTER TABLE "UserMobilePermissions" ENABLE ROW LEVEL SECURITY;

-- Política de RLS para el rol alquitel_app
CREATE POLICY "app_full_access_mobilepermissions" ON "UserMobilePermissions"
    FOR ALL
    TO alquitel_app
    USING (true)
    WITH CHECK (true);
