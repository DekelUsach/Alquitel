-- ─────────────────────────────────────────────────────────────────────────────
-- 20260829000100_least_privilege_grants
--
-- VULNERABILIDAD QUE CIERRA (confirmada el 2026-08-29 contra el proyecto
-- qgtaugmxmoxtpxvmugvt con information_schema.role_table_grants):
--
--   grantee        | tabla                  | privilegios
--   ---------------+------------------------+-------------------------------------------------
--   anon           | OrderApprovals         | DELETE,INSERT,REFERENCES,SELECT,TRIGGER,TRUNCATE,UPDATE
--   anon           | OrderAuditEvents       | idem
--   anon           | UserMobilePermissions  | idem
--   anon           | EventTemplates         | idem
--   authenticated  | (las mismas 4 tablas)  | idem
--
-- Esas cuatro tablas se crearon después del bootstrap y heredaron el
-- `GRANT ALL ... TO anon, authenticated` de los default privileges de Supabase.
-- Las otras seis tablas no lo tienen, así que fue un accidente, no un diseño.
--
-- Hoy no es explotable vía PostgREST porque RLS está encendido y esas tablas no
-- tienen NINGUNA política para anon/authenticated (verificado: GET /rest/v1/
-- con la AnonKey devuelve 200 y `[]`, no un error de permisos — o sea el
-- permiso de tabla existe y lo único que frena es RLS). Pero:
--
--   1. TRUNCATE NO está sujeto a RLS en PostgreSQL. El día que alguien abra un
--      canal SQL para esos roles, `TRUNCATE "OrderAuditEvents"` borra la
--      bitácora entera sin que ninguna política lo vea.
--   2. Es una bomba de tiempo: la primera política permisiva que alguien agregue
--      para `authenticated` en esas tablas abre DELETE/UPDATE de golpe, porque
--      el permiso de tabla ya está concedido.
--   3. TRIGGER y REFERENCES permiten atar objetos propios a tablas ajenas.
--
-- RLS es la segunda línea, no la primera. La primera es no tener el GRANT.
--
-- Esta migración NO concede nada nuevo: solo revoca. Es segura de aplicar en
-- caliente sobre producción (la app de escritorio se conecta como alquitel_app,
-- que no se toca acá).
-- ─────────────────────────────────────────────────────────────────────────────

-- ── 1. Revocar el sobre-privilegio de los roles de PostgREST ────────────────

REVOKE ALL ON public."OrderApprovals"        FROM anon, authenticated;
REVOKE ALL ON public."OrderAuditEvents"      FROM anon, authenticated;
REVOKE ALL ON public."UserMobilePermissions" FROM anon, authenticated;
REVOKE ALL ON public."EventTemplates"        FROM anon, authenticated;

-- Barrido defensivo del resto del esquema: si alguna tabla futura vuelve a
-- heredar el default, esto la limpia. Los GRANT deliberados se rehacen en
-- 20260829000300_rls_policies.sql, explícitos y por operación.
DO $$
DECLARE
    t record;
BEGIN
    FOR t IN
        SELECT c.oid::regclass AS rel
        FROM   pg_class c
        JOIN   pg_namespace n ON n.oid = c.relnamespace
        WHERE  n.nspname = 'public'
          AND  c.relkind IN ('r', 'v', 'm', 'p')
    LOOP
        EXECUTE format('REVOKE ALL ON %s FROM anon', t.rel);
        EXECUTE format('REVOKE ALL ON %s FROM authenticated', t.rel);
        EXECUTE format('REVOKE ALL ON %s FROM PUBLIC', t.rel);
    END LOOP;
END
$$;

-- ── 2. Cortar la herencia futura ─────────────────────────────────────────────
-- Los default privileges de Supabase hacen que toda tabla nueva creada por
-- `postgres` nazca con GRANT ALL para anon/authenticated. Se anulan acá para
-- que el patrón sea deny-by-default y cada permiso tenga que escribirse.

ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES    FROM anon, authenticated;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM anon, authenticated;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON FUNCTIONS FROM anon, authenticated;

-- Idem para lo que crea el rol `postgres` explícitamente (dueño real de las
-- tablas: pg_class.relowner = postgres en las 10 tablas del esquema).
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
    REVOKE ALL ON TABLES FROM anon, authenticated;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
    REVOKE ALL ON SEQUENCES FROM anon, authenticated;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
    REVOKE ALL ON FUNCTIONS FROM anon, authenticated;

-- ── 3. Nadie ejecuta funciones por default ───────────────────────────────────
-- PostgreSQL concede EXECUTE a PUBLIC en toda función nueva. Con RPC de
-- SECURITY DEFINER en el esquema (ver 20260829000800) eso sería un agujero:
-- cualquiera con la AnonKey podría invocarlas. Se revoca globalmente y cada RPC
-- concede EXECUTE a los roles que la necesitan, una por una.
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public
    REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

-- ── 4. El esquema public no se extiende desde afuera ─────────────────────────
REVOKE CREATE ON SCHEMA public FROM PUBLIC, anon, authenticated;
