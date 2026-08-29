-- ─────────────────────────────────────────────────────────────────────────────
-- 20260712000000_baseline_public_schema
--
-- Baseline reproducible del esquema compartido (PostgreSQL / Supabase).
--
-- Por qué existe: hasta ahora el esquema del servidor nacía de EF Core
-- (`DataInitializationService` + `EnsureCreated`) desde la máquina de un
-- desarrollador. Eso significa que NO se podía recrear el proyecto desde cero de
-- forma verificable: no había una definición versionada de tablas, índices,
-- restricciones ni RLS. Este archivo captura el estado del proyecto
-- qgtaugmxmoxtpxvmugvt al 2026-08-29 y es el punto de partida de todas las
-- migraciones posteriores.
--
-- Es IDEMPOTENTE (IF NOT EXISTS en todo): correrlo sobre la base existente no
-- cambia nada; correrlo sobre una base limpia la deja igual a producción.
--
-- No contiene datos: ni semillas reales, ni volcados, ni secretos.
--
-- Diferencias con SQLite (motor local mono-puesto):
--   * uuid nativo vs TEXT; numeric vs REAL; timestamp vs TEXT.
--   * SQLite no tiene roles, GRANT ni RLS: toda la seguridad de este directorio
--     aplica SOLO al backend compartido. El modo SQLite es mono-usuario y su
--     frontera de seguridad es el sistema de archivos de Windows.
--   * Los índices únicos parciales (WHERE ...) existen en ambos motores.
-- ─────────────────────────────────────────────────────────────────────────────

-- ── Catálogo ─────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public."Clients" (
    "Id"                     uuid PRIMARY KEY,
    "CompanyName"            text NOT NULL,
    "Cuit"                   text NOT NULL DEFAULT ''::text,
    "ContactName"            text NULL,
    "Email"                  text NULL,
    "Phone"                  text NULL,
    "IsArchived"             boolean NOT NULL DEFAULT false,
    "InternalNotes"          text NULL,
    "SpecialDiscountPercent" numeric NULL
);

-- CUIT único, pero solo entre los que efectivamente tienen CUIT cargado:
-- el sistema admite clientes sin CUIT y '' se repetiría.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Clients_Cuit"
    ON public."Clients" ("Cuit") WHERE ("Cuit" <> ''::text);

CREATE TABLE IF NOT EXISTS public."Products" (
    "Id"               uuid PRIMARY KEY,
    "Description"      text NOT NULL,
    "Category"         text NOT NULL DEFAULT 'General'::text,
    "BasePrice"        numeric NOT NULL DEFAULT 0,
    "ImagePath"        text NULL,
    "CustomFieldsJson" text NULL,
    "IsArchived"       boolean NOT NULL DEFAULT false,
    "StockQuantity"    integer NULL,
    "Cost"             numeric NULL
);

CREATE TABLE IF NOT EXISTS public."Locations" (
    "Id"   uuid PRIMARY KEY,
    "Name" text NOT NULL
);

-- ── Usuarios ─────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public."Users" (
    "Id"           uuid PRIMARY KEY,
    "Name"         text NOT NULL,
    "Role"         integer NOT NULL DEFAULT 1,
    "PasswordHash" text NULL,
    "IsArchived"   boolean NOT NULL DEFAULT false
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Name" ON public."Users" ("Name");

CREATE TABLE IF NOT EXISTS public."UserMobilePermissions" (
    "UserId"             uuid PRIMARY KEY REFERENCES public."Users" ("Id") ON DELETE CASCADE,
    "CanManageLocations" boolean NOT NULL DEFAULT false,
    "CanCreateBudgets"   boolean NOT NULL DEFAULT false,
    "CanManageClients"   boolean NOT NULL DEFAULT false,
    "CanSeeReports"      boolean NOT NULL DEFAULT false
);

-- ── Órdenes ──────────────────────────────────────────────────────────────────
-- Las FK a Clients/Locations/Users van SIN ON DELETE (= NO ACTION = Restrict).
-- Nunca reintroducir CASCADE acá: borrar un cliente no puede llevarse puesto su
-- historial de presupuestos. La UI reasigna antes de borrar padres.

CREATE TABLE IF NOT EXISTS public."Orders" (
    "Id"              uuid PRIMARY KEY,
    "BudgetNumber"    text NOT NULL,
    "AdminName"       text NOT NULL DEFAULT ''::text,
    "ClientId"        uuid NOT NULL REFERENCES public."Clients" ("Id"),
    "LocationId"      uuid NOT NULL REFERENCES public."Locations" ("Id"),
    "CreatedDate"     timestamp NOT NULL,
    "EventDate"       timestamp NULL,
    "Status"          integer NOT NULL DEFAULT 0,
    "CreatedByUserId" uuid NULL REFERENCES public."Users" ("Id"),
    "EventEndDate"    timestamp NULL,
    "Comments"        text NULL,
    "DiscountPercent" numeric NOT NULL DEFAULT 0,
    "DiscountAmount"  numeric NOT NULL DEFAULT 0,
    "AddVat"          boolean NOT NULL DEFAULT false,
    -- Concurrencia optimista: rota en cada guardado. Guid.Empty = fila legada
    -- sin control (el chequeo se saltea para no romper histórico).
    "RowVersion"      uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_Orders_BudgetNumber" ON public."Orders" ("BudgetNumber");
CREATE INDEX IF NOT EXISTS "IX_Orders_ClientId"    ON public."Orders" ("ClientId");
CREATE INDEX IF NOT EXISTS "IX_Orders_CreatedDate" ON public."Orders" ("CreatedDate");

-- Los ítems SÍ son hijos de la orden: CASCADE es correcto acá.
CREATE TABLE IF NOT EXISTS public."OrderItems" (
    "Id"                  uuid PRIMARY KEY,
    "OrderId"             uuid NOT NULL REFERENCES public."Orders" ("Id") ON DELETE CASCADE,
    "ProductId"           uuid NOT NULL REFERENCES public."Products" ("Id"),
    "Quantity"            integer NOT NULL DEFAULT 1,
    "UnitPrice"           numeric NOT NULL DEFAULT 0,
    "Dias"                integer NOT NULL DEFAULT 1,
    "TechnicalNotes"      text NULL,
    "ImagePath"           text NULL,
    "CustomFieldsJson"    text NULL,
    "DescriptionSnapshot" text NULL,
    "RequestedMeasure"    text NULL
);

CREATE INDEX IF NOT EXISTS "IX_OrderItems_OrderId" ON public."OrderItems" ("OrderId");

-- ── Bitácora ─────────────────────────────────────────────────────────────────
-- Sin FK dura a Orders a propósito: si una orden se elimina, su historial sigue.

CREATE TABLE IF NOT EXISTS public."OrderAuditEvents" (
    "Id"        uuid PRIMARY KEY,
    "OrderId"   uuid NOT NULL,
    "UserName"  text NOT NULL DEFAULT ''::text,
    "UserId"    uuid NULL,
    "EventType" text NOT NULL DEFAULT ''::text,
    "Detail"    text NULL,
    "Timestamp" timestamp NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS "IX_OrderAuditEvents_OrderId" ON public."OrderAuditEvents" ("OrderId");

-- ── Plantillas de evento ─────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public."EventTemplates" (
    "Id"            uuid PRIMARY KEY,
    "Name"          text NOT NULL,
    "ItemsJson"     text NOT NULL,
    "CreatedDate"   timestamp NOT NULL,
    "CreatedByName" text NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_EventTemplates_Name" ON public."EventTemplates" ("Name");

-- ── Links de aprobación ──────────────────────────────────────────────────────
-- OJO: la columna "Token" en texto plano se ELIMINA en
-- 20260829000800_approval_tokens_hashed.sql. Se declara acá solo para que el
-- baseline refleje el estado histórico y las migraciones siguientes apliquen.

CREATE TABLE IF NOT EXISTS public."OrderApprovals" (
    "Id"          uuid PRIMARY KEY,
    "OrderId"     uuid NOT NULL REFERENCES public."Orders" ("Id") ON DELETE RESTRICT,
    "Token"       uuid NOT NULL,
    "Status"      integer NOT NULL DEFAULT 0,
    "CreatedAt"   timestamp NOT NULL,
    "RespondedAt" timestamp NULL,
    "ClientIp"    text NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_OrderApprovals_Token"   ON public."OrderApprovals" ("Token");
CREATE INDEX        IF NOT EXISTS "IX_OrderApprovals_OrderId" ON public."OrderApprovals" ("OrderId");

-- ── RLS encendido en todas las tablas ────────────────────────────────────────
-- Sin políticas, RLS = deny-all. Las políticas reales se definen en
-- 20260829000400_rls_policies.sql, keyed a la identidad de Supabase Auth.

ALTER TABLE public."Clients"               ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."Products"              ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."Locations"             ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."Users"                 ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."UserMobilePermissions" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."Orders"                ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."OrderItems"            ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."OrderAuditEvents"      ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."EventTemplates"        ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."OrderApprovals"        ENABLE ROW LEVEL SECURITY;
