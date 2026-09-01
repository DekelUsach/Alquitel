-- ─────────────────────────────────────────────────────────────────────────────
-- Suite de seguridad del backend compartido de Alquitel.
--
-- CÓMO CORRERLA
--   Todo el archivo está dentro de BEGIN … ROLLBACK: no deja rastro. Se puede
--   correr contra una base de desarrollo, contra una rama de Supabase o —con
--   cuidado— contra el proyecto real, porque revierte al final.
--
--     psql "$SUPABASE_DB_URL" -v ON_ERROR_STOP=1 -f supabase/tests/01_security_suite.sql
--
--   Requiere un rol con permisos de dueño (postgres): crea usuarios de prueba y
--   cambia de rol con SET LOCAL ROLE.
--
--   Requiere que estén aplicadas las migraciones 20260829000100 … 20260829000900.
--
-- QUÉ NO CUBRE
--   La carrera real de dos peticiones HTTP simultáneas: eso necesita dos
--   conexiones y está en supabase/tests/02_approval_concurrency.mjs. Acá se
--   verifica el invariante que esa carrera tiene que respetar (el segundo
--   pedido no pisa el resultado del primero) más el consumo único del token.
-- ─────────────────────────────────────────────────────────────────────────────

BEGIN;

SET LOCAL client_min_messages = warning;

CREATE TEMP TABLE resultados (n serial, nombre text, ok boolean, detalle text) ON COMMIT DROP;
-- Las pruebas corren con SET LOCAL ROLE authenticated: sin esto no podrían
-- escribir sus propios resultados en la tabla temporal (que es de postgres).
GRANT ALL ON resultados TO PUBLIC;
GRANT ALL ON SEQUENCE resultados_n_seq TO PUBLIC;

CREATE OR REPLACE FUNCTION pg_temp.chk(p_nombre text, p_ok boolean, p_detalle text DEFAULT '')
RETURNS void LANGUAGE sql AS $$
    INSERT INTO resultados (nombre, ok, detalle) VALUES (p_nombre, p_ok, p_detalle);
$$;

-- Ejecuta SQL esperando que falle; ok = falló con el SQLSTATE esperado ('' = cualquiera).
CREATE OR REPLACE FUNCTION pg_temp.chk_falla(p_nombre text, p_sql text, p_errcode text DEFAULT '')
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    BEGIN
        EXECUTE p_sql;
        PERFORM pg_temp.chk(p_nombre, false, 'no falló (debería haber sido rechazado)');
    EXCEPTION WHEN others THEN
        PERFORM pg_temp.chk(p_nombre,
                            p_errcode = '' OR SQLSTATE = p_errcode,
                            format('SQLSTATE=%s %s', SQLSTATE, left(SQLERRM, 120)));
    END;
END $$;

-- Se hace pasar por un usuario de la aplicación ante PostgREST/RLS.
CREATE OR REPLACE FUNCTION pg_temp.como(p_auth_uid uuid)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    IF p_auth_uid IS NULL THEN
        RESET ROLE;
        PERFORM set_config('request.jwt.claims', NULL, true);
    ELSE
        PERFORM set_config('request.jwt.claims',
                           json_build_object('sub', p_auth_uid, 'role', 'authenticated')::text, true);
        EXECUTE 'SET LOCAL ROLE authenticated';
    END IF;
END $$;

-- ═══════════════════════════════ Datos de prueba ═════════════════════════════

INSERT INTO auth.users (id, instance_id, aud, role, email, encrypted_password, created_at, updated_at)
VALUES
 ('11111111-1111-1111-1111-111111111111','00000000-0000-0000-0000-000000000000','authenticated','authenticated','t-admin@test.local','x',now(),now()),
 ('22222222-2222-2222-2222-222222222222','00000000-0000-0000-0000-000000000000','authenticated','authenticated','t-comercial@test.local','x',now(),now()),
 ('33333333-3333-3333-3333-333333333333','00000000-0000-0000-0000-000000000000','authenticated','authenticated','t-oper@test.local','x',now(),now()),
 ('44444444-4444-4444-4444-444444444444','00000000-0000-0000-0000-000000000000','authenticated','authenticated','t-baja@test.local','x',now(),now());

INSERT INTO public."Users" ("Id","Name","Role","IsArchived","AuthUserId") VALUES
 ('aaaaaaa1-0000-0000-0000-000000000001','TEST Admin',       0,false,'11111111-1111-1111-1111-111111111111'),
 ('aaaaaaa1-0000-0000-0000-000000000002','TEST Comercial',   1,false,'22222222-2222-2222-2222-222222222222'),
 ('aaaaaaa1-0000-0000-0000-000000000003','TEST Operaciones', 2,false,'33333333-3333-3333-3333-333333333333'),
 ('aaaaaaa1-0000-0000-0000-000000000004','TEST Dado de baja',1,false,'44444444-4444-4444-4444-444444444444');

UPDATE public."Users" SET "DisabledAt" = now() WHERE "Id" = 'aaaaaaa1-0000-0000-0000-000000000004';

INSERT INTO public."Clients" ("Id","CompanyName","Cuit","Email","InternalNotes","SpecialDiscountPercent")
VALUES ('ccccccc1-0000-0000-0000-000000000001','TEST Cliente SA','30711111114','cli@test.local','paga tarde',12);

INSERT INTO public."Locations" ("Id","Name") VALUES ('11111111-0000-0000-0000-0000000000f1','TEST Predio');

INSERT INTO public."Products" ("Id","Description","Category","BasePrice","Cost")
VALUES ('bbbbbbb1-0000-0000-0000-000000000001','TEST Pantalla LED','Visuales',1000,400);

INSERT INTO public."Orders" ("Id","BudgetNumber","ClientId","LocationId","CreatedDate","Status","DiscountPercent","DiscountAmount","AddVat")
VALUES ('00000001-0000-0000-0000-000000000001','TEST-0001',
        'ccccccc1-0000-0000-0000-000000000001','11111111-0000-0000-0000-0000000000f1',
        (now() AT TIME ZONE 'UTC'), 0, 0, 0, true);

INSERT INTO public."OrderItems" ("Id","OrderId","ProductId","Quantity","UnitPrice","Dias","DescriptionSnapshot")
VALUES ('00000002-0000-0000-0000-000000000001','00000001-0000-0000-0000-000000000001',
        'bbbbbbb1-0000-0000-0000-000000000001',2,1000,3,'TEST Pantalla LED [b]P3[/b]');

-- ═══════════════════════ 1. Identidad y revocación ═══════════════════════════

SELECT pg_temp.como('11111111-1111-1111-1111-111111111111');
SELECT pg_temp.chk('identidad · admin resuelve rol admin', app.current_role_code() = 'admin', coalesce(app.current_role_code(),'NULL'));

SELECT pg_temp.como('33333333-3333-3333-3333-333333333333');
SELECT pg_temp.chk('identidad · operaciones resuelve su rol', app.current_role_code() = 'operaciones', coalesce(app.current_role_code(),'NULL'));

SELECT pg_temp.como('44444444-4444-4444-4444-444444444444');
SELECT pg_temp.chk('revocación · usuario desactivado no tiene rol ni identidad',
                   app.current_role_code() IS NULL AND app.is_active_user() = false);

SELECT pg_temp.como(NULL);
SELECT pg_temp.chk('deny-by-default · sin JWT no hay rol', app.current_role_code() IS NULL);

-- ═══════════════════════ 2. RLS con dos roles distintos ══════════════════════

SELECT pg_temp.como('22222222-2222-2222-2222-222222222222');
SELECT pg_temp.chk('rls · comercial VE clientes',
                   (SELECT count(*) FROM public."Clients" WHERE "Id"='ccccccc1-0000-0000-0000-000000000001') = 1);
SELECT pg_temp.chk('rls · comercial VE su orden en borrador',
                   (SELECT count(*) FROM public."Orders" WHERE "Id"='00000001-0000-0000-0000-000000000001') = 1);

SELECT pg_temp.como('33333333-3333-3333-3333-333333333333');
SELECT pg_temp.chk('rls · operaciones NO ve clientes (dato comercial)',
                   (SELECT count(*) FROM public."Clients") = 0);
SELECT pg_temp.chk('rls · operaciones NO ve órdenes en borrador',
                   (SELECT count(*) FROM public."Orders" WHERE "Id"='00000001-0000-0000-0000-000000000001') = 0);
SELECT pg_temp.chk('rls · operaciones NO ve ítems de una orden que no puede ver',
                   (SELECT count(*) FROM public."OrderItems" WHERE "OrderId"='00000001-0000-0000-0000-000000000001') = 0);
SELECT pg_temp.chk('rls · operaciones SÍ ve el catálogo',
                   (SELECT count(*) FROM public."Products" WHERE "Id"='bbbbbbb1-0000-0000-0000-000000000001') = 1);

SELECT pg_temp.como(NULL);
UPDATE public."Orders" SET "Status" = 1 WHERE "Id"='00000001-0000-0000-0000-000000000001';
UPDATE public."Orders" SET "Status" = 3 WHERE "Id"='00000001-0000-0000-0000-000000000001';

SELECT pg_temp.como('33333333-3333-3333-3333-333333333333');
SELECT pg_temp.chk('rls · operaciones SÍ ve la orden una vez despachada a OT',
                   (SELECT count(*) FROM public."Orders" WHERE "Id"='00000001-0000-0000-0000-000000000001') = 1);

SELECT pg_temp.como(NULL);
-- Vuelta a Borrador respetando la máquina de estados: OT → Archivado → Borrador.
UPDATE public."Orders" SET "Status" = 4 WHERE "Id"='00000001-0000-0000-0000-000000000001';
UPDATE public."Orders" SET "Status" = 0 WHERE "Id"='00000001-0000-0000-0000-000000000001';

-- ═════════════════ 3. Escalada de privilegios (insider) ══════════════════════

SELECT pg_temp.como('22222222-2222-2222-2222-222222222222');

SELECT pg_temp.chk_falla('escalada · comercial NO puede ascenderse a Admin',
    $q$ UPDATE public."Users" SET "Role" = 0 WHERE "Id" = 'aaaaaaa1-0000-0000-0000-000000000002' $q$);

-- RLS FILTRA, no lanza: un UPDATE que no matchea la política afecta 0 filas y
-- devuelve éxito. Por eso acá se verifica el EFECTO (la fila no cambió), que es
-- la propiedad de seguridad real. Asumir que "tiene que tirar error" sería el
-- mismo malentendido que causaba el bug de la Edge Function de aprobación.
UPDATE public."Users" SET "IsArchived" = true WHERE "Id" = 'aaaaaaa1-0000-0000-0000-000000000003';
SELECT pg_temp.chk('escalada · comercial NO archiva cuentas ajenas (RLS filtra, 0 filas)',
    (SELECT "IsArchived" FROM public."Users" WHERE "Id" = 'aaaaaaa1-0000-0000-0000-000000000003') = false);

SELECT pg_temp.chk_falla('escalada · comercial NO puede cambiar roles por el RPC de admin',
    $q$ SELECT public.admin_set_user_role('aaaaaaa1-0000-0000-0000-000000000002','admin') $q$, '42501');

SELECT pg_temp.chk_falla('escalada · comercial NO puede leer PasswordHash',
    $q$ SELECT "PasswordHash" FROM public."Users" LIMIT 1 $q$, '42501');

DELETE FROM public."Clients" WHERE "Id"='ccccccc1-0000-0000-0000-000000000001';
SELECT pg_temp.chk('escalada · comercial NO borra clientes (RLS filtra, 0 filas)',
    (SELECT count(*) FROM public."Clients" WHERE "Id"='ccccccc1-0000-0000-0000-000000000001') = 1);

SELECT pg_temp.chk_falla('escalada · nadie borra usuarios (son el actor de la bitácora)',
    $q$ DELETE FROM public."Users" WHERE "Id"='aaaaaaa1-0000-0000-0000-000000000003' $q$);

SELECT pg_temp.como('11111111-1111-1111-1111-111111111111');
SELECT public.admin_set_user_role('aaaaaaa1-0000-0000-0000-000000000003','lectura');
SELECT pg_temp.chk('autorización · el Admin SÍ cambia roles por el RPC',
                   (SELECT "Role" FROM public."Users" WHERE "Id"='aaaaaaa1-0000-0000-0000-000000000003') = 3);
SELECT public.admin_set_user_role('aaaaaaa1-0000-0000-0000-000000000003','operaciones');

-- Para que "el último Admin" sea comprobable, se archivan los demás Admin de la
-- base (dentro del ROLLBACK, no persiste). Queda solo TEST Admin activo.
UPDATE public."Users" SET "IsArchived" = true
 WHERE "Role" = 0 AND "Id" <> 'aaaaaaa1-0000-0000-0000-000000000001';

SELECT pg_temp.chk_falla('autorización · no se puede quitar el último Admin activo',
    $q$ UPDATE public."Users" SET "IsArchived" = true WHERE "Id" = 'aaaaaaa1-0000-0000-0000-000000000001' $q$, '23514');

-- ═══════════════════════ 4. Bitácora append-only ═════════════════════════════

SELECT pg_temp.como('22222222-2222-2222-2222-222222222222');

INSERT INTO public."OrderAuditEvents" ("Id","OrderId","UserName","UserId","EventType","Detail")
VALUES ('00000003-0000-0000-0000-000000000001','00000001-0000-0000-0000-000000000001',
        'TEST Admin','aaaaaaa1-0000-0000-0000-000000000001','Editado','firmado con identidad ajena');

SELECT pg_temp.chk('bitácora · el actor lo pone el servidor, no el cliente',
    (SELECT "UserName" FROM public."OrderAuditEvents" WHERE "Id"='00000003-0000-0000-0000-000000000001') = 'TEST Comercial',
    (SELECT "UserName" FROM public."OrderAuditEvents" WHERE "Id"='00000003-0000-0000-0000-000000000001'));

SELECT pg_temp.chk_falla('bitácora · no se puede editar un evento',
    $q$ UPDATE public."OrderAuditEvents" SET "Detail"='borrado' WHERE "Id"='00000003-0000-0000-0000-000000000001' $q$, '42501');

SELECT pg_temp.chk_falla('bitácora · no se puede borrar un evento',
    $q$ DELETE FROM public."OrderAuditEvents" WHERE "Id"='00000003-0000-0000-0000-000000000001' $q$, '42501');

SELECT pg_temp.como(NULL);
SELECT pg_temp.chk_falla('bitácora · TRUNCATE bloqueado incluso para el dueño',
    $q$ TRUNCATE public."OrderAuditEvents" $q$, '42501');

-- ═══════════════ 5. Máquina de estados de la orden ═══════════════════════════

SELECT pg_temp.chk_falla('estados · Borrador → Enviado a OT rechazado (despacho sin aprobación)',
    $q$ UPDATE public."Orders" SET "Status"=3 WHERE "Id"='00000001-0000-0000-0000-000000000001' $q$, '23514');

UPDATE public."Orders" SET "Status"=1 WHERE "Id"='00000001-0000-0000-0000-000000000001';
SELECT pg_temp.chk('estados · Borrador → Aprobado permitido',
                   (SELECT "Status" FROM public."Orders" WHERE "Id"='00000001-0000-0000-0000-000000000001') = 1);

UPDATE public."Orders" SET "Status"=5 WHERE "Id"='00000001-0000-0000-0000-000000000001';
SELECT pg_temp.chk_falla('estados · Rechazado → Aprobado rechazado (no se pisa la negativa del cliente)',
    $q$ UPDATE public."Orders" SET "Status"=1 WHERE "Id"='00000001-0000-0000-0000-000000000001' $q$, '23514');

UPDATE public."Orders" SET "Status"=0 WHERE "Id"='00000001-0000-0000-0000-000000000001';

SELECT pg_temp.chk_falla('estados · no se crea un presupuesto directamente en OT',
    $q$ INSERT INTO public."Orders" ("Id","BudgetNumber","ClientId","LocationId","CreatedDate","Status")
        VALUES (gen_random_uuid(),'TEST-9999','ccccccc1-0000-0000-0000-000000000001',
                '11111111-0000-0000-0000-0000000000f1',(now() AT TIME ZONE 'UTC'),3) $q$, '23514');

-- ═══════════════════ 6. Integridad de datos ══════════════════════════════════

SELECT pg_temp.chk_falla('integridad · cantidad 0 rechazada',
    $q$ UPDATE public."OrderItems" SET "Quantity"=0 WHERE "Id"='00000002-0000-0000-0000-000000000001' $q$, '23514');

SELECT pg_temp.chk_falla('integridad · precio unitario negativo rechazado',
    $q$ UPDATE public."OrderItems" SET "UnitPrice"=-500 WHERE "Id"='00000002-0000-0000-0000-000000000001' $q$, '23514');

SELECT pg_temp.chk_falla('integridad · descuento del 150% rechazado',
    $q$ UPDATE public."Orders" SET "DiscountPercent"=150 WHERE "Id"='00000001-0000-0000-0000-000000000001' $q$, '23514');

SELECT pg_temp.chk_falla('integridad · fin de evento anterior al inicio rechazado',
    $q$ UPDATE public."Orders" SET "EventDate"='2026-10-10', "EventEndDate"='2026-10-01'
        WHERE "Id"='00000001-0000-0000-0000-000000000001' $q$, '23514');

SELECT pg_temp.chk_falla('integridad · ubicación duplicada (distinta capitalización) rechazada',
    $q$ INSERT INTO public."Locations" ("Id","Name") VALUES (gen_random_uuid(),'  test predio ') $q$, '23505');

SELECT pg_temp.chk_falla('integridad · rol de usuario fuera del catálogo rechazado',
    $q$ UPDATE public."Users" SET "Role"=99 WHERE "Id"='aaaaaaa1-0000-0000-0000-000000000003' $q$, '23514');

-- ═════════════ 7. Token de aprobación: hash y rotación ═══════════════════════

SELECT pg_temp.como(NULL);

INSERT INTO public."OrderApprovals" ("Id","OrderId","Token","Status","CreatedAt")
VALUES ('00000004-0000-0000-0000-000000000001','00000001-0000-0000-0000-000000000001',
        'dddddddd-dddd-dddd-dddd-dddddddddddd', 0, (now() AT TIME ZONE 'UTC'));

SELECT pg_temp.chk('token · el texto plano no se persiste',
    (SELECT "Token" FROM public."OrderApprovals" WHERE "Id"='00000004-0000-0000-0000-000000000001') IS NULL);

SELECT pg_temp.chk('token · se guarda el SHA-256 del token',
    (SELECT "TokenHash" FROM public."OrderApprovals" WHERE "Id"='00000004-0000-0000-0000-000000000001')
    = app.approval_token_hash('dddddddd-dddd-dddd-dddd-dddddddddddd'));

SELECT pg_temp.chk('token · no se puede buscar por texto plano en la base',
    NOT EXISTS (SELECT 1 FROM public."OrderApprovals" WHERE "Token" IS NOT NULL));

-- ═════════════ 8. Portal público: página y respuesta ═════════════════════════

SELECT pg_temp.chk('portal · token inexistente → not_found',
    public.get_approval_page('99999999-9999-9999-9999-999999999999','203.0.113.9')->>'outcome' = 'not_found');

SELECT pg_temp.chk('portal · token válido → página con detalle',
    (public.get_approval_page('dddddddd-dddd-dddd-dddd-dddddddddddd','203.0.113.1')->>'outcome') = 'ok'
    AND (public.get_approval_page('dddddddd-dddd-dddd-dddd-dddddddddddd','203.0.113.1')->>'detail_visible')::boolean);

SELECT pg_temp.chk('portal · la página NO expone datos internos del cliente',
    NOT (public.get_approval_page('dddddddd-dddd-dddd-dddd-dddddddddddd','203.0.113.1')::text
         LIKE '%paga tarde%'),
    'InternalNotes no debe aparecer');

SELECT pg_temp.chk('portal · la página NO expone el costo del producto',
    NOT (public.get_approval_page('dddddddd-dddd-dddd-dddd-dddddddddddd','203.0.113.1')#>>'{items}'
         LIKE '%400%'),
    'Products.Cost no debe aparecer');

SELECT pg_temp.chk('portal · acción inválida rechazada',
    public.respond_approval('dddddddd-dddd-dddd-dddd-dddddddddddd','borrar','203.0.113.1')->>'outcome' = 'invalid_action');

-- Respuesta real
SELECT pg_temp.chk('portal · aprobación consumida correctamente',
    public.respond_approval('dddddddd-dddd-dddd-dddd-dddddddddddd','approve','203.0.113.1')->>'outcome' = 'ok');

SELECT pg_temp.chk('portal · la orden quedó Aprobada',
    (SELECT "Status" FROM public."Orders" WHERE "Id"='00000001-0000-0000-0000-000000000001') = 1);

SELECT pg_temp.chk('portal · el token quedó consumido con fecha de respuesta',
    (SELECT "Status"=1 AND "RespondedAt" IS NOT NULL
     FROM public."OrderApprovals" WHERE "Id"='00000004-0000-0000-0000-000000000001'));

SELECT pg_temp.chk('portal · quedó registrado en la bitácora',
    EXISTS (SELECT 1 FROM public."OrderAuditEvents"
            WHERE "OrderId"='00000001-0000-0000-0000-000000000001'
              AND "EventType"='Aprobado por el cliente'));

SELECT pg_temp.chk('portal · RowVersion rotó (los puestos abiertos detectan el cambio)',
    (SELECT "RowVersion" FROM public."Orders" WHERE "Id"='00000001-0000-0000-0000-000000000001')
    <> '00000000-0000-0000-0000-000000000000');

-- Idempotencia y contradicción — el escenario de la carrera
SELECT pg_temp.chk('portal · reintento de la MISMA acción es idempotente',
    public.respond_approval('dddddddd-dddd-dddd-dddd-dddddddddddd','approve','203.0.113.1')->>'outcome' = 'already_same');

SELECT pg_temp.chk('portal · la acción CONTRARIA no pisa el veredicto (bug de la versión anterior)',
    public.respond_approval('dddddddd-dddd-dddd-dddd-dddddddddddd','reject','203.0.113.2')->>'outcome' = 'already_other');

SELECT pg_temp.chk('portal · la orden SIGUE Aprobada después del intento contrario',
    (SELECT "Status" FROM public."Orders" WHERE "Id"='00000001-0000-0000-0000-000000000001') = 1,
    'con el código anterior acá quedaba Rechazada');

-- ═════════════ 9. Vencimiento, revocación y conflicto de estado ══════════════

INSERT INTO public."OrderApprovals" ("Id","OrderId","Token","Status","CreatedAt")
VALUES ('00000004-0000-0000-0000-000000000002','00000001-0000-0000-0000-000000000001',
        'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee', 0,
        (now() AT TIME ZONE 'UTC') - interval '400 days');

SELECT pg_temp.chk('portal · link vencido → expired',
    public.respond_approval('eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','approve','203.0.113.3')->>'outcome' = 'expired');

SELECT pg_temp.chk('portal · link vencido tampoco muestra la página',
    public.get_approval_page('eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','203.0.113.3')->>'outcome' = 'expired');

-- Emitir un link nuevo revoca los pendientes anteriores
INSERT INTO public."OrderApprovals" ("Id","OrderId","Token","Status","CreatedAt")
VALUES ('00000004-0000-0000-0000-000000000003','00000001-0000-0000-0000-000000000001',
        'ffffffff-ffff-ffff-ffff-ffffffffffff', 0, (now() AT TIME ZONE 'UTC'));

SELECT pg_temp.chk('rotación · emitir un link nuevo revoca el pendiente anterior',
    (SELECT "RevokedAt" IS NOT NULL FROM public."OrderApprovals" WHERE "Id"='00000004-0000-0000-0000-000000000002'));

SELECT pg_temp.chk('rotación · el link revocado deja de responder',
    public.respond_approval('eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','approve','203.0.113.4')->>'outcome'
      IN ('revoked','expired'));

-- Conflicto de estado: la orden ya se despachó
UPDATE public."Orders" SET "Status"=3 WHERE "Id"='00000001-0000-0000-0000-000000000001';

SELECT pg_temp.chk('portal · orden ya despachada → order_state_conflict',
    public.respond_approval('ffffffff-ffff-ffff-ffff-ffffffffffff','reject','203.0.113.5')->>'outcome' = 'order_state_conflict');

SELECT pg_temp.chk('portal · tras el conflicto el token SIGUE pendiente (reintentable)',
    (SELECT "Status" FROM public."OrderApprovals" WHERE "Id"='00000004-0000-0000-0000-000000000003') = 0,
    'la transacción interna revirtió el consumo');

SELECT pg_temp.chk('portal · el conflicto no tocó el estado de la orden',
    (SELECT "Status" FROM public."Orders" WHERE "Id"='00000001-0000-0000-0000-000000000001') = 3);

-- ═════════════════════════ 10. Límite de intentos ════════════════════════════

DELETE FROM app.approval_rate_limit;

DO $$
DECLARE i int; v text;
BEGIN
    FOR i IN 1..12 LOOP
        v := public.respond_approval('ffffffff-ffff-ffff-ffff-ffffffffffff','approve','198.51.100.7')->>'outcome';
    END LOOP;
    PERFORM pg_temp.chk('rate limit · el martilleo de un token se corta', v = 'rate_limited', v);
END $$;

-- ══════════════════════ 11. Retención / anonimización ════════════════════════

UPDATE public."OrderApprovals"
SET "RespondedAt" = (now() AT TIME ZONE 'UTC') - interval '200 days', "Status" = 1
WHERE "Id" = '00000004-0000-0000-0000-000000000001';

SELECT pg_temp.chk('retención · pasados 90 días la página ya no muestra el detalle',
    (public.get_approval_page('dddddddd-dddd-dddd-dddd-dddddddddddd','203.0.113.8')->>'detail_visible')::boolean = false);

SELECT pg_temp.chk('retención · el sello (número y veredicto) sigue disponible',
    public.get_approval_page('dddddddd-dddd-dddd-dddd-dddddddddddd','203.0.113.8')->>'budget_number' = 'TEST-0001');

SELECT * FROM app.purge_approval_pii();

SELECT pg_temp.chk('retención · la IP quedó anonimizada a /24',
    (SELECT "ClientIp" FROM public."OrderApprovals" WHERE "Id"='00000004-0000-0000-0000-000000000001') = '203.0.113.0/24',
    (SELECT coalesce("ClientIp",'NULL') FROM public."OrderApprovals" WHERE "Id"='00000004-0000-0000-0000-000000000001'));

-- ═══════════════════════════════ Resultado ═══════════════════════════════════

SELECT n, CASE WHEN ok THEN 'PASA' ELSE '*** FALLA ***' END AS estado, nombre, detalle
FROM resultados ORDER BY n;

SELECT count(*) FILTER (WHERE ok)       AS pasan,
       count(*) FILTER (WHERE NOT ok)   AS fallan,
       count(*)                         AS total
FROM resultados;

DO $$
DECLARE v int;
BEGIN
    SELECT count(*) INTO v FROM resultados WHERE NOT ok;
    IF v > 0 THEN
        RAISE WARNING 'La suite tiene % prueba(s) en falla — ver el listado de arriba.', v;
    END IF;
END $$;

ROLLBACK;
