-- ─────────────────────────────────────────────────────────────────────────────
-- 20260829000700_approval_tokens_hashed
--
-- El token del link público es una credencial de portador: quien lo tiene puede
-- ver el presupuesto completo (razón social, CUIT, contacto, mail, teléfono,
-- importes) y aprobarlo en nombre del cliente. Hoy se guarda EN TEXTO PLANO en
-- `OrderApprovals."Token"`, en una tabla que la credencial compartida
-- `alquitel_app` lee entera desde cualquier puesto de trabajo.
--
-- Consecuencia concreta: un volcado de la base, un backup, un empleado con el
-- connection string o un `SELECT "Token" FROM "OrderApprovals"` alcanzan para
-- aprobar presupuestos ajenos. Igual que guardar contraseñas en claro.
--
-- Esta migración deja SOLO el hash SHA-256 del token. El texto plano existe
-- únicamente en la URL que se le manda al cliente por mail.
--
-- COMPATIBILIDAD CON EL CLIENTE ACTUAL: no requiere cambios en EF ni una
-- migración de SQLite. La columna "Token" sigue existiendo pero pasa a ser
-- siempre NULL, y un trigger BEFORE hace el hash y descarta el plano. O sea: la
-- app puede seguir insertando como hasta ahora y el servidor no persiste el
-- secreto. Lo único que cambia de verdad es que el token YA NO SE PUEDE RECUPERAR
-- desde la base — ver la nota de rotación más abajo.
--
-- Se elimina definitivamente la columna en
-- 20260829001100_drop_plaintext_token_column.sql, después de que el cliente deje
-- de mapearla.
-- ─────────────────────────────────────────────────────────────────────────────

ALTER TABLE public."OrderApprovals"
    ADD COLUMN IF NOT EXISTS "TokenHash"    bytea NULL,
    -- Rotación: al emitir un link nuevo para la misma orden, los anteriores
    -- pendientes se revocan. Sin esto, un link viejo reenviado por el cliente a
    -- un tercero sigue aprobando precios que ya cambiaron.
    ADD COLUMN IF NOT EXISTS "RevokedAt"    timestamptz NULL,
    -- Retención: momento en que se anonimizaron los datos personales asociados
    -- (ver 20260829000900_approval_retention.sql).
    ADD COLUMN IF NOT EXISTS "AnonymizedAt" timestamptz NULL;

-- ── 1. Backfill: hash de los tokens existentes ──────────────────────────────
-- sha256() es builtin desde PostgreSQL 11: no hace falta pgcrypto, así que la
-- migración no depende de extensiones instaladas.
-- El texto hasheado es la representación canónica del uuid en minúsculas y con
-- guiones (formato "D" de .NET), que es exactamente lo que viaja en la URL.

UPDATE public."OrderApprovals"
SET    "TokenHash" = sha256(convert_to(lower("Token"::text), 'UTF8'))
WHERE  "TokenHash" IS NULL
  AND  "Token" IS NOT NULL;

-- ── 2. El plano se va ────────────────────────────────────────────────────────

DROP INDEX IF EXISTS public."IX_OrderApprovals_Token";

ALTER TABLE public."OrderApprovals" ALTER COLUMN "Token" DROP NOT NULL;

UPDATE public."OrderApprovals" SET "Token" = NULL WHERE "Token" IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_OrderApprovals_TokenHash"
    ON public."OrderApprovals" ("TokenHash");

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_Approvals_TokenHash_presente') THEN
        ALTER TABLE public."OrderApprovals"
            ADD CONSTRAINT "CK_Approvals_TokenHash_presente" CHECK ("TokenHash" IS NOT NULL);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_Approvals_sin_token_plano') THEN
        ALTER TABLE public."OrderApprovals"
            ADD CONSTRAINT "CK_Approvals_sin_token_plano" CHECK ("Token" IS NULL);
    END IF;
END
$$;

-- ── 3. El servidor hashea y descarta ─────────────────────────────────────────
-- Vale también para clientes viejos, para el outbox y para cualquier script: no
-- hay forma de escribir un token en claro en esta tabla.

CREATE OR REPLACE FUNCTION app.hash_approval_token()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = public, pg_catalog
AS $$
BEGIN
    IF NEW."Token" IS NOT NULL THEN
        NEW."TokenHash" := sha256(convert_to(lower(NEW."Token"::text), 'UTF8'));
        NEW."Token"     := NULL;
    END IF;

    IF NEW."TokenHash" IS NULL THEN
        RAISE EXCEPTION 'OrderApprovals necesita un token (columna "Token" al insertar, o "TokenHash" ya calculado)'
            USING ERRCODE = '23502';
    END IF;

    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS trg_approvals_hash_token ON public."OrderApprovals";
CREATE TRIGGER trg_approvals_hash_token
    BEFORE INSERT OR UPDATE ON public."OrderApprovals"
    FOR EACH ROW EXECUTE FUNCTION app.hash_approval_token();

-- ── 4. Rotación: un link nuevo revoca los anteriores ────────────────────────
-- NOTA DE COMPORTAMIENTO: como el token ya no se puede leer de la base, la app
-- no puede "reutilizar el link pendiente" al reenviar el mail (lo hacía en
-- EfApprovalLinkService). Ahora cada emisión genera un link nuevo y anula el
-- anterior. Es más seguro —un link viejo reenviado deja de servir— pero el
-- cliente final tiene que usar el último correo recibido.

CREATE OR REPLACE FUNCTION app.revoke_previous_approvals()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = public, pg_catalog
AS $$
BEGIN
    UPDATE public."OrderApprovals"
    SET    "RevokedAt" = now()
    WHERE  "OrderId"   = NEW."OrderId"
      AND  "Id"       <> NEW."Id"
      AND  "Status"    = 0
      AND  "RevokedAt" IS NULL;
    RETURN NULL;
END
$$;

DROP TRIGGER IF EXISTS trg_approvals_revoke_previous ON public."OrderApprovals";
CREATE TRIGGER trg_approvals_revoke_previous
    AFTER INSERT ON public."OrderApprovals"
    FOR EACH ROW EXECUTE FUNCTION app.revoke_previous_approvals();

CREATE INDEX IF NOT EXISTS "IX_OrderApprovals_OrderId_Status"
    ON public."OrderApprovals" ("OrderId", "Status");

-- ── 5. Límite de intentos ────────────────────────────────────────────────────
-- El portal es un endpoint público sin autenticación previa. Sin límite, se
-- puede barrer el espacio de tokens (aunque sea de 122 bits) y, sobre todo,
-- usar el endpoint como amplificador de tráfico contra el proyecto.
-- Ventana deslizante simple por cubeta (IP y token), suficiente y barata.

CREATE TABLE IF NOT EXISTS app.approval_rate_limit (
    bucket       text PRIMARY KEY,
    window_start timestamptz NOT NULL DEFAULT now(),
    hits         integer NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_approval_rate_limit_window
    ON app.approval_rate_limit (window_start);

CREATE OR REPLACE FUNCTION app.rate_limit_hit(
    p_bucket text,
    p_limit  integer,
    p_window interval
)
RETURNS boolean
LANGUAGE plpgsql
SET search_path = app, pg_catalog
AS $$
DECLARE
    v_hits integer;
BEGIN
    INSERT INTO app.approval_rate_limit AS r (bucket, window_start, hits)
    VALUES (p_bucket, now(), 1)
    ON CONFLICT (bucket) DO UPDATE
        SET hits = CASE WHEN r.window_start < now() - p_window THEN 1 ELSE r.hits + 1 END,
            window_start = CASE WHEN r.window_start < now() - p_window THEN now() ELSE r.window_start END
    RETURNING hits INTO v_hits;

    RETURN v_hits <= p_limit;
END
$$;

COMMENT ON FUNCTION app.rate_limit_hit(text, integer, interval) IS
'true = permitido. Cuenta un intento en la cubeta y aplica ventana deslizante.';
