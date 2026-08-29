-- ─────────────────────────────────────────────────────────────────────────────
-- 20260829000300_rls_policies
--
-- Autorización real del lado servidor, una política por tabla y por operación,
-- keyed al rol que `app.current_role_code()` resuelve DESDE LA BASE a partir de
-- auth.uid(). Ningún parámetro del cliente participa de la decisión.
--
-- Reemplaza el modelo anterior, donde las 10 tablas tenían
-- `FOR ALL TO alquitel_app USING(true) WITH CHECK(true)`: RLS encendido pero sin
-- separar a nadie de nadie.
--
-- Matriz de permisos (filas = tabla, columnas = rol):
--
--                        admin      comercial   operaciones        lectura
--   Clients              RWD        RW          —                  R
--   Products             RWD        R           R (sin Cost)       R
--   Locations            RWD        RW          R                  R
--   Users                RWD*       R           R                  R
--   UserMobilePermiss.   RWD        R           R (propia)         —
--   Orders               RWD        RW          R (solo OF/OT)     R
--   OrderItems           RWD        RW          R (solo OF/OT)     R
--   OrderAuditEvents     R + A      A           A                  R
--   EventTemplates       RWD        RW          R                  R
--   OrderApprovals       R + A      R + A       —                  —
--
--   R = SELECT · W = INSERT/UPDATE · D = DELETE · A = solo INSERT (append-only)
--   * Users: el rol y el estado se cambian por RPC (admin_set_user_role /
--     admin_set_user_enabled), nunca por UPDATE directo — ver el trigger
--     app.guard_user_privilege_columns.
--
-- Los GRANT de tabla se rehacen acá porque 20260829000100 revocó todo. Se dan a
-- `authenticated` (el rol de PostgREST para un JWT válido) y la separación fina
-- entre empleados la hace RLS. `PasswordHash` se excluye a nivel COLUMNA: ningún
-- rol de aplicación puede leerlo por la API. Con Supabase Auth esa columna queda
-- vestigial y se elimina cuando termine la conmutación.
-- ─────────────────────────────────────────────────────────────────────────────

-- ── 0. Limpieza de las políticas del modelo viejo ────────────────────────────
-- Se dejan vivas mientras `alquitel_app` siga conectándose directo; el DROP
-- definitivo está en 20260829001000_decommission_alquitel_app.sql. Acá solo se
-- borran políticas propias por si la migración se re-corre.

DROP POLICY IF EXISTS clients_select        ON public."Clients";
DROP POLICY IF EXISTS clients_write         ON public."Clients";
DROP POLICY IF EXISTS clients_update        ON public."Clients";
DROP POLICY IF EXISTS clients_delete        ON public."Clients";
DROP POLICY IF EXISTS products_select       ON public."Products";
DROP POLICY IF EXISTS products_write        ON public."Products";
DROP POLICY IF EXISTS products_update       ON public."Products";
DROP POLICY IF EXISTS products_delete       ON public."Products";
DROP POLICY IF EXISTS locations_select      ON public."Locations";
DROP POLICY IF EXISTS locations_write       ON public."Locations";
DROP POLICY IF EXISTS locations_update      ON public."Locations";
DROP POLICY IF EXISTS locations_delete      ON public."Locations";
DROP POLICY IF EXISTS users_select          ON public."Users";
DROP POLICY IF EXISTS users_insert          ON public."Users";
DROP POLICY IF EXISTS users_update          ON public."Users";
DROP POLICY IF EXISTS ump_select            ON public."UserMobilePermissions";
DROP POLICY IF EXISTS ump_write             ON public."UserMobilePermissions";
DROP POLICY IF EXISTS ump_update            ON public."UserMobilePermissions";
DROP POLICY IF EXISTS ump_delete            ON public."UserMobilePermissions";
DROP POLICY IF EXISTS orders_select         ON public."Orders";
DROP POLICY IF EXISTS orders_insert         ON public."Orders";
DROP POLICY IF EXISTS orders_update         ON public."Orders";
DROP POLICY IF EXISTS orders_delete         ON public."Orders";
DROP POLICY IF EXISTS orderitems_select     ON public."OrderItems";
DROP POLICY IF EXISTS orderitems_insert     ON public."OrderItems";
DROP POLICY IF EXISTS orderitems_update     ON public."OrderItems";
DROP POLICY IF EXISTS orderitems_delete     ON public."OrderItems";
DROP POLICY IF EXISTS audit_select          ON public."OrderAuditEvents";
DROP POLICY IF EXISTS audit_insert          ON public."OrderAuditEvents";
DROP POLICY IF EXISTS eventtemplates_select ON public."EventTemplates";
DROP POLICY IF EXISTS eventtemplates_insert ON public."EventTemplates";
DROP POLICY IF EXISTS eventtemplates_update ON public."EventTemplates";
DROP POLICY IF EXISTS eventtemplates_delete ON public."EventTemplates";
DROP POLICY IF EXISTS approvals_select      ON public."OrderApprovals";
DROP POLICY IF EXISTS approvals_insert      ON public."OrderApprovals";
DROP POLICY IF EXISTS approvals_delete      ON public."OrderApprovals";

-- ── 1. Clients ───────────────────────────────────────────────────────────────
-- Operaciones (depósito) no ve datos comerciales del cliente: arma equipos, no
-- factura. Es el caso más claro de mínimo privilegio del sistema.

GRANT SELECT, INSERT, UPDATE, DELETE ON public."Clients" TO authenticated;

CREATE POLICY clients_select ON public."Clients" FOR SELECT TO authenticated
    USING (app.has_role('admin', 'comercial', 'lectura'));

CREATE POLICY clients_write ON public."Clients" FOR INSERT TO authenticated
    WITH CHECK (app.has_role('admin', 'comercial'));

CREATE POLICY clients_update ON public."Clients" FOR UPDATE TO authenticated
    USING      (app.has_role('admin', 'comercial'))
    WITH CHECK (app.has_role('admin', 'comercial'));

CREATE POLICY clients_delete ON public."Clients" FOR DELETE TO authenticated
    USING (app.has_role('admin'));

-- ── 2. Products ──────────────────────────────────────────────────────────────
-- Todos leen el catálogo (operaciones necesita descripciones para la OT), pero
-- el catálogo lo edita solo Admin: el precio base es la fuente de la verdad
-- comercial y no puede moverlo cualquiera.

GRANT SELECT, INSERT, UPDATE, DELETE ON public."Products" TO authenticated;

CREATE POLICY products_select ON public."Products" FOR SELECT TO authenticated
    USING (app.is_active_user());

CREATE POLICY products_write ON public."Products" FOR INSERT TO authenticated
    WITH CHECK (app.has_role('admin'));

CREATE POLICY products_update ON public."Products" FOR UPDATE TO authenticated
    USING      (app.has_role('admin'))
    WITH CHECK (app.has_role('admin'));

CREATE POLICY products_delete ON public."Products" FOR DELETE TO authenticated
    USING (app.has_role('admin'));

-- ── 3. Locations ─────────────────────────────────────────────────────────────

GRANT SELECT, INSERT, UPDATE, DELETE ON public."Locations" TO authenticated;

CREATE POLICY locations_select ON public."Locations" FOR SELECT TO authenticated
    USING (app.is_active_user());

CREATE POLICY locations_write ON public."Locations" FOR INSERT TO authenticated
    WITH CHECK (app.has_role('admin', 'comercial'));

CREATE POLICY locations_update ON public."Locations" FOR UPDATE TO authenticated
    USING      (app.has_role('admin', 'comercial'))
    WITH CHECK (app.has_role('admin', 'comercial'));

CREATE POLICY locations_delete ON public."Locations" FOR DELETE TO authenticated
    USING (app.has_role('admin'));

-- ── 4. Users ─────────────────────────────────────────────────────────────────
-- GRANT por COLUMNA: "PasswordHash" no se lista, así que ningún JWT lo lee, sea
-- cual sea su rol y sin importar qué política se agregue después. El permiso de
-- columna se evalúa antes que RLS.
-- Tampoco se concede UPDATE sobre "Role"/"IsArchived"/"DisabledAt"/"AuthUserId":
-- esos cambian solo por los RPC de admin. Doble barrera con el trigger.

GRANT SELECT ("Id", "Name", "Role", "IsArchived", "AuthUserId", "Email",
              "DisabledAt", "DisabledReason", "UpdatedAt")
    ON public."Users" TO authenticated;
GRANT INSERT ON public."Users" TO authenticated;
-- "IsArchived" sí se concede (es el borrado lógico que ya usa la app), pero el
-- trigger app.guard_user_privilege_columns solo se lo permite a un Admin y nunca
-- sobre la propia cuenta. "Role", "DisabledAt" y "AuthUserId" quedan fuera del
-- GRANT: no hay UPDATE directo posible, solo los RPC de admin.
GRANT UPDATE ("Name", "Email", "IsArchived") ON public."Users" TO authenticated;
-- Sin DELETE: los usuarios no se borran (trigger app.guard_user_delete).

CREATE POLICY users_select ON public."Users" FOR SELECT TO authenticated
    USING (app.is_active_user());

CREATE POLICY users_insert ON public."Users" FOR INSERT TO authenticated
    WITH CHECK (app.has_role('admin'));

CREATE POLICY users_update ON public."Users" FOR UPDATE TO authenticated
    USING      (app.has_role('admin') OR "Id" = app.current_user_id())
    WITH CHECK (app.has_role('admin') OR "Id" = app.current_user_id());

-- ── 5. UserMobilePermissions ─────────────────────────────────────────────────

GRANT SELECT, INSERT, UPDATE, DELETE ON public."UserMobilePermissions" TO authenticated;

CREATE POLICY ump_select ON public."UserMobilePermissions" FOR SELECT TO authenticated
    USING (app.has_role('admin') OR "UserId" = app.current_user_id());

CREATE POLICY ump_write ON public."UserMobilePermissions" FOR INSERT TO authenticated
    WITH CHECK (app.has_role('admin'));

CREATE POLICY ump_update ON public."UserMobilePermissions" FOR UPDATE TO authenticated
    USING      (app.has_role('admin'))
    WITH CHECK (app.has_role('admin'));

CREATE POLICY ump_delete ON public."UserMobilePermissions" FOR DELETE TO authenticated
    USING (app.has_role('admin'));

-- ── 6. Orders ────────────────────────────────────────────────────────────────
-- Operaciones ve SOLO lo que ya está aprobado y despachado (OF/OT): no tiene por
-- qué leer presupuestos en borrador ni rechazados con precios en negociación.
-- Estados: 0 Draft · 1 Approved · 2 SentToOF · 3 SentToOT · 4 Archived · 5 Rejected

GRANT SELECT, INSERT, UPDATE, DELETE ON public."Orders" TO authenticated;

CREATE POLICY orders_select ON public."Orders" FOR SELECT TO authenticated
    USING (
        app.has_role('admin', 'comercial', 'lectura')
        OR (app.has_role('operaciones') AND "Status" IN (2, 3))
    );

CREATE POLICY orders_insert ON public."Orders" FOR INSERT TO authenticated
    WITH CHECK (app.has_role('admin', 'comercial'));

CREATE POLICY orders_update ON public."Orders" FOR UPDATE TO authenticated
    USING      (app.has_role('admin', 'comercial'))
    WITH CHECK (app.has_role('admin', 'comercial'));

CREATE POLICY orders_delete ON public."Orders" FOR DELETE TO authenticated
    USING (app.has_role('admin'));

-- ── 7. OrderItems ────────────────────────────────────────────────────────────
-- Hereda la visibilidad del padre: si no podés ver la orden, no ves sus ítems
-- (ni sus precios unitarios).

GRANT SELECT, INSERT, UPDATE, DELETE ON public."OrderItems" TO authenticated;

CREATE POLICY orderitems_select ON public."OrderItems" FOR SELECT TO authenticated
    USING (EXISTS (SELECT 1 FROM public."Orders" o WHERE o."Id" = "OrderId"));

CREATE POLICY orderitems_insert ON public."OrderItems" FOR INSERT TO authenticated
    WITH CHECK (app.has_role('admin', 'comercial'));

CREATE POLICY orderitems_update ON public."OrderItems" FOR UPDATE TO authenticated
    USING      (app.has_role('admin', 'comercial'))
    WITH CHECK (app.has_role('admin', 'comercial'));

CREATE POLICY orderitems_delete ON public."OrderItems" FOR DELETE TO authenticated
    USING (app.has_role('admin', 'comercial'));

-- ── 8. OrderAuditEvents — append-only ────────────────────────────────────────
-- Sin UPDATE ni DELETE para NINGÚN rol de aplicación: el permiso de tabla
-- directamente no existe, así que no hay política que pueda habilitarlo por
-- error. Los detalles (triggers, revocación a alquitel_app) van en
-- 20260829000600_audit_append_only.sql.

GRANT SELECT, INSERT ON public."OrderAuditEvents" TO authenticated;

CREATE POLICY audit_select ON public."OrderAuditEvents" FOR SELECT TO authenticated
    USING (app.has_role('admin', 'comercial', 'lectura'));

CREATE POLICY audit_insert ON public."OrderAuditEvents" FOR INSERT TO authenticated
    WITH CHECK (app.is_active_user());

-- ── 9. EventTemplates ────────────────────────────────────────────────────────

GRANT SELECT, INSERT, UPDATE, DELETE ON public."EventTemplates" TO authenticated;

CREATE POLICY eventtemplates_select ON public."EventTemplates" FOR SELECT TO authenticated
    USING (app.is_active_user());

CREATE POLICY eventtemplates_insert ON public."EventTemplates" FOR INSERT TO authenticated
    WITH CHECK (app.has_role('admin', 'comercial'));

CREATE POLICY eventtemplates_update ON public."EventTemplates" FOR UPDATE TO authenticated
    USING      (app.has_role('admin', 'comercial'))
    WITH CHECK (app.has_role('admin', 'comercial'));

CREATE POLICY eventtemplates_delete ON public."EventTemplates" FOR DELETE TO authenticated
    USING (app.has_role('admin'));

-- ── 10. OrderApprovals ───────────────────────────────────────────────────────
-- El cliente de escritorio EMITE links y CONSULTA su estado. Responder (aprobar
-- o rechazar) es exclusivo del RPC atómico que invoca la Edge Function con el
-- rol de servicio: por eso no hay GRANT de UPDATE para `authenticated`. Un
-- vendedor no puede marcar como "aprobado" un presupuesto que el cliente nunca
-- aprobó — que es justamente el fraude que este flujo tiene que impedir.

GRANT SELECT, INSERT, DELETE ON public."OrderApprovals" TO authenticated;

CREATE POLICY approvals_select ON public."OrderApprovals" FOR SELECT TO authenticated
    USING (app.has_role('admin', 'comercial'));

CREATE POLICY approvals_insert ON public."OrderApprovals" FOR INSERT TO authenticated
    WITH CHECK (app.has_role('admin', 'comercial'));

CREATE POLICY approvals_delete ON public."OrderApprovals" FOR DELETE TO authenticated
    USING (app.has_role('admin'));

-- ── 11. Vistas enmascaradas (contrato para el cliente) ───────────────────────
-- RLS es por FILA; estas dos columnas son sensibles por COLUMNA y el rol de
-- aplicación no se puede expresar en un GRANT de columna (todos los empleados
-- comparten el rol PostgreSQL `authenticated`). Se resuelve con vistas
-- SECURITY DEFINER que anulan el campo según el rol resuelto en el servidor.
--
-- Contrato: el cliente de escritorio debe leer el catálogo y los clientes por
-- estas vistas en lugar de las tablas. Ver docs/SUPABASE_MIGRATION_CONTRACT.md.

CREATE OR REPLACE VIEW app.v_products_masked
WITH (security_invoker = false) AS
SELECT p."Id", p."Description", p."Category", p."BasePrice", p."ImagePath",
       p."CustomFieldsJson", p."IsArchived", p."StockQuantity",
       CASE WHEN app.has_role('admin') THEN p."Cost" END AS "Cost"
FROM   public."Products" p
WHERE  app.is_active_user();

CREATE OR REPLACE VIEW app.v_clients_masked
WITH (security_invoker = false) AS
SELECT c."Id", c."CompanyName", c."Cuit", c."ContactName", c."Email", c."Phone",
       c."IsArchived",
       CASE WHEN app.has_role('admin', 'comercial') THEN c."InternalNotes" END          AS "InternalNotes",
       CASE WHEN app.has_role('admin', 'comercial') THEN c."SpecialDiscountPercent" END AS "SpecialDiscountPercent"
FROM   public."Clients" c
WHERE  app.has_role('admin', 'comercial', 'lectura');

REVOKE ALL ON app.v_products_masked FROM PUBLIC;
REVOKE ALL ON app.v_clients_masked  FROM PUBLIC;
GRANT SELECT ON app.v_products_masked TO authenticated;
GRANT SELECT ON app.v_clients_masked  TO authenticated;
