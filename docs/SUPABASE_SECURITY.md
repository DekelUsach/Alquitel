# Configuración segura de Supabase

Qué es público, qué es secreto, dónde va cada cosa y qué hacer cuando algo se
filtra. Vale para el proyecto `qgtaugmxmoxtpxvmugvt`.

---

## 1. Clasificación de credenciales

| Credencial | ¿Secreto? | Dónde vive | Quién la tiene |
|---|---|---|---|
| **URL del proyecto** | No | `appsettings.json` | Todos |
| **AnonKey** (`role: anon`) | **No** — es pública por diseño | `appsettings.json`, dentro del instalador | Todos, y cualquiera que abra el ejecutable |
| **Access token del usuario** (JWT de Auth) | Sí, pero efímero (1 h) | Solo en memoria del proceso | Cada empleado, el suyo |
| **Refresh token** | Sí | Almacenamiento protegido del equipo (DPAPI) | Cada empleado, el suyo |
| **ServiceKey** (`role: service_role`) | **Sí, crítico** — BYPASSRLS sobre todo el proyecto | **Solo en el servidor.** Nunca en un equipo de trabajo, nunca en un build | Nadie, en el cliente |
| **Password del rol `alquitel_app`** | Sí, crítico | En baja: ver `20260829001000` | Nadie, después de la conmutación |
| **API key de Pollinations** | Sí | `appsettings.local.json` (gitignoreado) | Solo los equipos que usan la IA |

**La regla que resume todo:** si una credencial permite hacer algo que un
empleado cualquiera no debería poder hacer, no puede estar en el disco de un
empleado cualquiera. La AnonKey pasa esa prueba porque, con RLS bien puesto, no
permite nada por sí sola. La ServiceKey no la pasa.

---

## 2. Acción pendiente: rotar la ServiceKey

La `SUPABASE_SERVICE_ROLE_KEY` del proyecto está hoy en:

- `Alquitel.UI/appsettings.local.json` (gitignoreado, pero presente en disco)
- `Alquitel.UI/bin/Debug/net8.0-windows/appsettings.local.json`
- `Alquitel.UI/bin/Release/net8.0-windows/appsettings.local.json`

Los dos últimos son salida de compilación: cualquier empaquetado que copie el
directorio de salida **distribuye la llave maestra del proyecto**. Esa key
permite leer y escribir todas las tablas ignorando RLS, listar usuarios de Auth y
vaciar el Storage.

Pasos, en este orden:

1. Panel de Supabase → *Project Settings → API → Service role* → **Reset**.
   Esto invalida la key vieja de inmediato.
2. Borrar la clave de los tres archivos:
   ```powershell
   Remove-Item Alquitel.UI\bin\Debug\net8.0-windows\appsettings.local.json
   Remove-Item Alquitel.UI\bin\Release\net8.0-windows\appsettings.local.json
   ```
   y quitar el bloque `ServiceKey` de `Alquitel.UI\appsettings.local.json`.
3. Confirmar que la key nueva NO se copia a ningún equipo. Su único uso legítimo
   es desde el panel del proyecto o desde una máquina de operación.
4. Con `20260829001200` aplicada, publicar plantillas ya no la necesita: alcanza
   el JWT de un Admin.

La Edge Function `aprobar` tampoco la usa más. Después del redeploy, borrar el
secret:

```bash
supabase secrets unset SUPABASE_SERVICE_ROLE_KEY --project-ref qgtaugmxmoxtpxvmugvt
```

---

## 3. Variables de entorno

La app lee configuración en este orden (lo último pisa lo anterior):

1. `appsettings.json` — solo valores públicos, versionado
2. `appsettings.local.json` — por máquina, gitignoreado
3. Variables de entorno con prefijo `ALQUITEL_`
4. User Secrets (`dotnet user-secrets`) — solo en compilaciones DEBUG

Cada nivel de la jerarquía JSON se separa con **doble guion bajo**:

| Clave | Variable | ¿Secreto? |
|---|---|---|
| `Database:Supabase:Url` | `ALQUITEL_Database__Supabase__Url` | No |
| `Database:Supabase:AnonKey` | `ALQUITEL_Database__Supabase__AnonKey` | No |
| `Database:Supabase:ConnectionString` | `ALQUITEL_Database__Supabase__ConnectionString` | **Sí** — en baja |
| `Database:Supabase:ServiceKey` | `ALQUITEL_Database__Supabase__ServiceKey` | **Sí — no usar en clientes** |
| `Ai:Pollinations:ApiKey` | `ALQUITEL_Ai__Pollinations__ApiKey` | **Sí** |

Preferir la variable de entorno sobre `appsettings.local.json`: no queda en un
archivo que se copia con el directorio de salida.

```powershell
[Environment]::SetEnvironmentVariable("ALQUITEL_Ai__Pollinations__ApiKey", "<key>", "User")
```

Hay que reiniciar la app y la terminal desde donde se lanza: los procesos ya
abiertos no ven variables nuevas.

---

## 4. Secretos de las Edge Functions

Se configuran en el proyecto, no en el repositorio:

```bash
supabase secrets list --project-ref qgtaugmxmoxtpxvmugvt
supabase secrets set NOMBRE=valor --project-ref qgtaugmxmoxtpxvmugvt
```

`SUPABASE_URL` y `SUPABASE_ANON_KEY` los inyecta Supabase automáticamente: no
hay que configurarlos. La función `aprobar` no necesita ningún otro secreto.

---

## 5. Deploy

### Esquema

```bash
# Con la CLI, en orden de nombre de archivo:
supabase db push --project-ref qgtaugmxmoxtpxvmugvt

# O pegando cada archivo en el SQL Editor, respetando el orden y las
# advertencias de docs/SUPABASE_MIGRATION_CONTRACT.md
```

### Edge Function

```bash
supabase functions deploy aprobar --project-ref qgtaugmxmoxtpxvmugvt --no-verify-jwt
```

`--no-verify-jwt` es necesario: el cliente final no tiene sesión de Supabase. La
autorización es el token del link, validado en la base.

### Purga de retención

Con `pg_cron`:

```sql
CREATE EXTENSION IF NOT EXISTS pg_cron;
SELECT cron.schedule('alquitel-purge-approval-pii', '17 4 * * *',
                     $$SELECT app.purge_approval_pii()$$);
```

Sin `pg_cron`, invocarla desde una Scheduled Function.

---

## 6. Alta y baja de empleados

**Alta:**

1. Panel → *Authentication → Users → Add user* (email + contraseña temporal).
2. Vincular con la fila de la aplicación:
   ```sql
   UPDATE public."Users"
   SET "AuthUserId" = '<uuid de auth.users>', "Email" = '<email>'
   WHERE "Id" = '<uuid del usuario de la app>';
   ```
3. Asignar rol (desde la app, o directo):
   ```sql
   SELECT public.admin_set_user_role('<uuid>', 'comercial');
   -- admin | comercial | operaciones | lectura
   ```

**Baja:** nunca borrar la fila — es el actor de la bitácora y de
`Orders.CreatedByUserId`.

```sql
SELECT public.admin_set_user_enabled('<uuid>', false, 'Renuncia 2026-09-01');
```

Efecto: `app.current_user_id()` devuelve NULL para esa persona, así que RLS le
niega todo en el siguiente request, aunque su `access_token` todavía no haya
vencido. Conviene además borrar la cuenta de Auth o cambiarle la contraseña para
que no pueda pedir un token nuevo.

**Verificación de la baja:**

```sql
SELECT "Name", "IsArchived", "DisabledAt" FROM public."Users" WHERE "Id" = '<uuid>';
```

---

## 7. Chequeos periódicos

Correr esto cada tanto, y siempre después de tocar el esquema:

```sql
-- 1. Nadie fuera de authenticated debería tener permisos en public
SELECT grantee, table_name, privilege_type
FROM information_schema.role_table_grants
WHERE table_schema = 'public' AND grantee IN ('anon', 'PUBLIC');
-- esperado: 0 filas

-- 2. Ninguna tabla sin RLS
SELECT relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public' AND c.relkind = 'r' AND NOT c.relrowsecurity;
-- esperado: 0 filas

-- 3. Ninguna política que le abra algo a anon
SELECT schemaname, tablename, policyname, roles FROM pg_policies
WHERE roles::text LIKE '%anon%';
-- esperado: 0 filas

-- 4. Ningún token de aprobación en claro
SELECT count(*) FROM public."OrderApprovals" WHERE "Token" IS NOT NULL;
-- esperado: 0

-- 5. Usuarios activos sin cuenta de Auth (bloquea la baja de alquitel_app)
SELECT "Name" FROM public."Users"
WHERE "IsArchived" = false AND "DisabledAt" IS NULL AND "AuthUserId" IS NULL;
```

Y el linter del proyecto:

```bash
supabase inspect db --project-ref qgtaugmxmoxtpxvmugvt
```

La suite completa está en `supabase/tests/01_security_suite.sql`: corre dentro
de `BEGIN … ROLLBACK`, así que se puede ejecutar incluso contra producción.

---

## 8. Qué hacer si se filtra algo

| Se filtró | Qué hacer |
|---|---|
| ServiceKey | Reset en el panel. Invalida la vieja al instante. Revisar `query_logs` del período |
| AnonKey | Nada urgente: es pública. Sí verificar que RLS esté cerrado (§7) |
| Password de `alquitel_app` | `ALTER ROLE alquitel_app PASSWORD '<nueva>'` y actualizar cada puesto. Mejor: acelerar `20260829001000` y dar de baja el rol |
| Un token de aprobación | `UPDATE public."OrderApprovals" SET "RevokedAt" = now() WHERE "Id" = '<id>'` — el link muere de inmediato |
| El access token de un empleado | Desactivar la cuenta (§6). Vence solo en 1 h |
| Un `.docx` de plantilla | Bajo impacto. Con `20260829001200` ya no es descargable sin autenticación |
