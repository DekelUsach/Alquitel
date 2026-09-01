# Modelo de amenazas — backend compartido de Alquitel

Alcance: la base PostgreSQL compartida en Supabase, el portal público de
aprobación, el Storage de plantillas y las credenciales que la app de escritorio
lleva en cada equipo. Fuera de alcance: el motor de Word, el modo SQLite local
(mono-puesto, su frontera de seguridad es el sistema de archivos de Windows) y la
seguridad física de los equipos.

Fecha del relevamiento: 2026-08-29. Todo lo marcado como **verificado** se
comprobó contra el proyecto real `qgtaugmxmoxtpxvmugvt`.

**Escala.** Impacto: Bajo / Medio / Alto / Crítico. Probabilidad: cuán fácil es
que ocurra dado quién tiene acceso hoy. El riesgo residual es el que queda
*después* de aplicar las migraciones de esta rama.

---

## Resumen

| # | Amenaza | Impacto | Prob. antes | Riesgo residual |
|---|---|---|---|---|
| T1 | Robo de la credencial compartida de PostgreSQL | Crítico | Alta | Medio → Bajo tras la baja del rol |
| T2 | Empleado interno que se eleva a Admin | Alto | Alta | Bajo |
| T3 | Acceso directo a la API con la AnonKey | Alto | Alta | Bajo |
| T4 | Filtración o reutilización de un token de aprobación | Alto | Media | Bajo |
| T5 | Exposición de PII de clientes en el portal público | Medio | Alta (permanente) | Bajo |
| T6 | Manipulación de precios, estados o bitácora | Alto | Alta | Bajo |
| T7 | Abuso del endpoint público | Medio | Media | Bajo |
| T8 | Secretos en builds, logs y configuración | Crítico | **Presente hoy** | Medio hasta rotar la key |
| T9 | Carrera en el portal de aprobación | Medio | Media | Muy bajo |
| T10 | Pérdida de reproducibilidad del esquema | Medio | Alta | Bajo |

---

## T1 — Robo de la credencial compartida de PostgreSQL

**Impacto: Crítico · Probabilidad antes: Alta**

Todos los puestos comparten la cuenta `alquitel_app`, cuya contraseña está en
`appsettings.local.json` o en una variable de entorno de cada máquina, legible
por cualquier proceso que corra con ese usuario de Windows.

Verificado: `alquitel_app` tiene `rolcanlogin = true` y
`DELETE, INSERT, SELECT, UPDATE` sobre las 10 tablas, con una política
`FOR ALL USING(true) WITH CHECK(true)` en cada una — incluidas `Users` y
`OrderAuditEvents`.

Quien copie esa cadena desde su propio equipo puede, desde cualquier cliente SQL:
leer todos los presupuestos y datos de clientes, ascenderse a Admin, alterar
precios históricos y borrar la bitácora que lo registraría. Y no deja rastro
atribuible: para la base, todos son la misma cuenta.

**Mitigación.** Sustituir la credencial compartida por una cuenta de Supabase
Auth por empleado, con RLS por rol (`20260829000200`, `20260829000300`) y dar de
baja el rol (`20260829001000`).

**Evidencia.** `pg_roles`, `pg_policies` e `information_schema.role_table_grants`
consultados contra el proyecto. Las políticas nuevas se validaron con
`supabase/tests/01_security_suite.sql`, que se hace pasar por tres empleados con
roles distintos.

**Residual.** Medio mientras la app siga conectándose directo — la migración
`001000` es lo último de la secuencia y depende de trabajo de UI. Bajo una vez
aplicada. La contraseña actual conviene rotarla igual, porque ya circuló.

---

## T2 — Empleado interno que se eleva a privilegios

**Impacto: Alto · Probabilidad antes: Alta**

El rol (Admin / Vendedor / Armador) vivía solo en el proceso WPF y gobernaba qué
botones se dibujan. Un `UPDATE "Users" SET "Role" = 0` desde cualquier cliente
SQL, con la credencial que ya tiene en su equipo, convertía a cualquiera en
administrador. La visibilidad de botones no es una frontera de seguridad.

**Mitigación.**
- El rol se resuelve **en el servidor** a partir de `auth.uid()`
  (`app.current_role_code()`), nunca de un parámetro del cliente.
- `Role`, `DisabledAt` y `AuthUserId` no tienen GRANT de UPDATE para
  `authenticated`: no hay UPDATE directo posible.
- Un trigger `BEFORE` impide modificar el propio rol o estado, y evita que se
  quite el último Admin activo.
- Los cambios de rol pasan por `admin_set_user_role`, que verifica al llamador
  contra la base.
- `PasswordHash` queda fuera del GRANT de columna: ningún JWT lo lee.

**Evidencia.** Pruebas de la suite: un usuario `comercial` no se asciende a
Admin (falla con `42501`), no usa el RPC de admin (`42501`), no lee
`PasswordHash` (`42501`), y su intento de archivar una cuenta ajena afecta 0
filas dejando la fila intacta.

**Residual.** Bajo. Un Admin legítimo sigue pudiendo hacer daño — eso es
inherente al rol— pero ahora queda registrado con nombre y apellido en una
bitácora que no puede borrar (T6).

---

## T3 — Acceso directo a la API con la AnonKey

**Impacto: Alto · Probabilidad antes: Alta**

La AnonKey viaja en `appsettings.json`, dentro del instalador y en el
repositorio. Es pública por diseño, y por lo tanto todo lo que se le conceda es
público.

Dos hallazgos verificados:

1. `anon` y `authenticated` tenían
   `DELETE, INSERT, REFERENCES, SELECT, TRIGGER, TRUNCATE, UPDATE` sobre
   `OrderApprovals`, `OrderAuditEvents`, `UserMobilePermissions` y
   `EventTemplates`. Hoy RLS lo contiene (una consulta con la AnonKey devuelve
   200 y `[]`, o sea el permiso de tabla existe y solo frena la política), pero
   **TRUNCATE no pasa por RLS** y la primera política permisiva que alguien
   agregue abriría DELETE y UPDATE de golpe.
2. El bucket `templates` está marcado como privado, pero su única política era
   `templates_read_anon | {anon} | SELECT`. Es decir: cualquiera con el
   instalador podía descargar `presupuesto.docx` y `ot.docx` — membrete,
   estructura de precios y condiciones comerciales.

**Mitigación.** `20260829000100` revoca todo y corta la herencia con
`ALTER DEFAULT PRIVILEGES`, para que toda tabla futura nazca deny-by-default.
`20260829001200` reemplaza la lectura anónima del bucket por lectura para
empleados activos y escritura solo para Admin.

**Evidencia.** `information_schema.role_table_grants` antes y después (0 filas
para anon/authenticated/PUBLIC); `pg_policies` del esquema `storage` antes y
después (0 políticas para anon). La confirmación por HTTP de la descarga anónima
del `.docx` no se ejecutó — un hook local bloqueó el `curl` —, pero la política
es la fuente de la decisión de acceso.

**Residual.** Bajo. Las dos RPC del portal quedan expuestas a `anon` a
propósito: es como funciona el link público. Están acotadas por token, límite de
intentos y validación de estado.

---

## T4 — Filtración o reutilización de un token de aprobación

**Impacto: Alto · Probabilidad antes: Media**

El token del link es una credencial de portador: quien lo tiene ve el
presupuesto completo y aprueba en nombre del cliente. Se guardaba **en texto
plano** en `OrderApprovals."Token"`, en una tabla que la credencial compartida
lee entera. Verificado: hay 6 links reales en esa condición.

Vectores: volcado o backup de la base, empleado con el connection string, o el
propio cliente reenviando el correo a un tercero.

**Mitigación.**
- Solo se guarda el SHA-256 (`20260829000700`). Un trigger hashea y descarta el
  plano, así que ni un cliente viejo puede persistirlo.
- Vencimiento a 30 días, verificado en la base y no en el navegador.
- Emitir un link nuevo revoca los pendientes anteriores de esa orden: un link
  viejo reenviado deja de servir, y con él los precios que mostraba.
- El token nunca se loguea (ni en Serilog, ni en la Edge Function, ni en un
  mensaje de error).
- `Referrer-Policy: no-referrer` + `<meta name="referrer">`, `Cache-Control:
  no-store, private`, `X-Robots-Tag: noindex`.

**Evidencia.** Pruebas de la suite: el texto plano no se persiste; el hash
coincide y la búsqueda es insensible a mayúsculas; ninguna fila conserva token en
claro; un link vencido devuelve `expired` tanto en la página como al responder;
emitir uno nuevo marca `RevokedAt` en el anterior y el revocado deja de
responder.

**Residual.** Bajo. El token sigue viajando en la query string, así que queda en
el historial del navegador del cliente y en su casilla de correo. Cambiarlo a un
fragmento (`#`) o a un POST rompería el formato de los links ya emitidos; las
cabeceras y el vencimiento acotan el daño. Es una decisión consciente, no un
olvido.

---

## T5 — Exposición de PII de clientes en el portal público

**Impacto: Medio · Probabilidad antes: Alta (permanente)**

El portal publica en internet, detrás de un secreto de portador, datos personales
de un tercero que nunca aceptó nada: razón social, CUIT, nombre del contacto,
email, teléfono, y los importes negociados. No había política de retención: el
link seguía sirviendo todo indefinidamente, respondido o no.

**Mitigación.** `20260829000900` define y aplica:
- Pendiente: detalle completo hasta el vencimiento (30 días).
- Respondido: detalle completo 90 días más, como comprobante de lo que el
  cliente aceptó.
- Después: solo el sello (número, veredicto, fecha).
- A los 180 días se anonimiza la IP registrada a su /24.
- Sin responder y vencido hace mucho: el link se revoca.

`app.purge_approval_pii()` es idempotente y programable con `pg_cron`. La
selección de columnas expuestas vive en el RPC —o sea en el esquema, revisable en
code review—, no en TypeScript. `InternalNotes`, `SpecialDiscountPercent`,
`Products.Cost`, `AdminName` y `CreatedByUserId` no salen nunca.

**Evidencia.** Pruebas de la suite: pasados 90 días `detail_visible` es `false` y
el sello sigue disponible; la purga deja la IP en `203.0.113.0/24`; la página no
contiene las notas internas del cliente ni el costo del producto.

**Residual.** Bajo. Los plazos son un criterio razonable, no una obligación
legal verificada: si hay un requisito específico de la Ley 25.326, ajustar las
constantes de `app.approval_detail_days()` y `app.approval_anonymize_days()`.

---

## T6 — Manipulación de precios, estados o bitácora

**Impacto: Alto · Probabilidad antes: Alta**

Tres problemas distintos con la misma raíz — la regla vivía en la UI:

1. **Bitácora editable por el auditado.** `alquitel_app` tenía UPDATE y DELETE
   sobre `OrderAuditEvents`. El mismo usuario que altera un precio podía borrar
   la línea que lo registra. Y el "actor" lo escribía el cliente, así que también
   se podía firmar con el nombre de otro.
2. **Estados libres.** El combo de la UI permitía cualquier transición: emitir
   una OT de un presupuesto que nadie aprobó, o llevar a Aprobado uno que el
   cliente rechazó por el portal.
3. **Importes sin límites.** Cantidad 0, días negativos o descuento del 350% no
   los rechazaba nada del lado base.

**Mitigación.**
- Bitácora append-only: se revocan UPDATE/DELETE/TRUNCATE y se agregan triggers
  que frenan incluso al dueño de la tabla, a quien un GRANT no alcanza. El actor
  y la fecha los pone el servidor.
- Máquina de estados en la base (`20260829000500`), con `order_status_transitions`
  expuesta para que la UI no duplique la regla.
- CHECKs de rango e integridad (`20260829000400`).
- El portal registra el veredicto del cliente en la bitácora y rota `RowVersion`
  para que los puestos con la orden abierta detecten el cambio.

**Evidencia.** Pruebas de la suite: el actor lo pone el servidor (un evento
firmado como "Admin" por un comercial queda registrado como el comercial); no se
puede editar ni borrar un evento (`42501`); TRUNCATE bloqueado incluso para el
dueño; Borrador→OT y Rechazado→Aprobado rechazados (`23514`); cantidad 0, precio
negativo, descuento 150% y fechas invertidas rechazados. Las restricciones se
validaron además contra los datos reales sin una sola violación.

**Residual.** Bajo. Un Admin sigue pudiendo cambiar precios: es su trabajo. La
diferencia es que ahora queda escrito quién y cuándo, en un registro que no puede
alterar.

---

## T7 — Abuso del endpoint público

**Impacto: Medio · Probabilidad antes: Media**

`/functions/v1/aprobar` es público y sin autenticación previa. Sin límite se
puede barrer el espacio de tokens y, sobre todo, usar el endpoint como
amplificador de tráfico contra el proyecto (cada request hacía varias consultas
a la base).

**Mitigación.** Límite por ventana deslizante dentro de la RPC: 20 respuestas por
IP y 10 por token cada 10 minutos; 120 vistas de página por IP. Se cuenta antes
de tocar la tabla. La validación de forma del UUID descarta el barrido trivial
sin llegar a la base. La cubeta se limpia en la purga diaria para que la tabla no
crezca sin techo.

**Evidencia.** Prueba de la suite: 12 intentos seguidos sobre el mismo token
terminan en `rate_limited`.

**Residual.** Bajo. El límite es por IP: un atacante distribuido lo elude, pero
el límite por token sigue aplicando y adivinar un UUID son 122 bits. Para
protección volumétrica corresponde el WAF del proveedor, que está fuera de este
alcance.

---

## T8 — Secretos en builds, logs y configuración

**Impacto: Crítico · Probabilidad: presente hoy**

Verificado: la `SUPABASE_SERVICE_ROLE_KEY` del proyecto —que ignora RLS sobre
todo, lista los usuarios de Auth y puede vaciar el Storage— está en
`Alquitel.UI/appsettings.local.json` **y quedó copiada en
`bin/Debug/net8.0-windows/` y `bin/Release/net8.0-windows/`**. Son directorios de
salida de compilación: cualquier empaquetado que copie el directorio distribuye
la llave maestra del proyecto.

En el mismo archivo hay una API key de Pollinations (`sk_...`).

**Mitigación.**
- **Rotar la ServiceKey ya** (procedimiento en `docs/SUPABASE_SECURITY.md` §2).
- La Edge Function deja de necesitarla: ahora usa la AnonKey.
- Publicar plantillas deja de necesitarla: `20260829001200` lo resuelve con el
  JWT del Admin.
- `appsettings.local.example.json` deja de sugerir que un cliente lleve una
  ServiceKey.
- Los logs no registran el token de aprobación; los errores del portal no
  exponen `SQLERRM`, nombres de tabla ni connection strings.

**Evidencia.** Los tres archivos existen en disco (comprobado). La rotación es
una acción del operador y **queda pendiente**: nadie más puede hacerla.

**Residual.** Medio hasta que la key se rote. Después, bajo. `bin/` está
gitignoreado, así que la key nunca llegó al repositorio.

---

## T9 — Carrera en el portal de aprobación

**Impacto: Medio · Probabilidad antes: Media**

Confirmado por lectura del código: la Edge Function hacía un UPDATE condicional y
comprobaba `if (!upErr)`. Un UPDATE que no matchea ninguna fila **no devuelve
error** en PostgREST: devuelve 0 filas. Con dos pedidos simultáneos —doble clic,
o Aprobar en una pestaña y Rechazar en otra— ambos leían `Pending`; el primero
aprobaba; el segundo afectaba 0 filas, no veía error, entraba igual a la rama de
éxito y pisaba `Orders.Status` con el veredicto contrario. Resultado: aprobación
en `Approved`, orden en `Rejected`, y cartel de éxito para los dos. Además las
dos escrituras eran dos requests HTTP: sin transacción, un corte entre medio
consumía el token y dejaba la orden sin actualizar, sin forma de reintentar.

**Mitigación.** Un RPC, una transacción: `SELECT … FOR UPDATE` serializa los
pedidos concurrentes; `GET DIAGNOSTICS ROW_COUNT` se verifica en las dos
escrituras y si no es exactamente 1 se revierte todo, incluido el consumo del
token; los estados admitidos se validan en la base; las respuestas son
idempotentes.

**Evidencia.** Pruebas de la suite: reintento de la misma acción devuelve
`already_same`; la acción contraria devuelve `already_other` y **la orden sigue
Aprobada** (con el código anterior quedaba Rechazada); si la orden ya se despachó
devuelve `order_state_conflict` y el token sigue pendiente y reintentable.

**Residual.** Muy bajo. La carrera real de dos peticiones HTTP simultáneas queda
**pendiente de validación externa**: requiere dos conexiones y una base
descartable. Lo verificado es el invariante que esa carrera debe respetar y el
mecanismo que lo garantiza.

---

## T10 — Pérdida de reproducibilidad del esquema

**Impacto: Medio · Probabilidad antes: Alta**

`/supabase/migrations/` estaba en `.gitignore`. El esquema del servidor —donde
viven las restricciones, los índices y las políticas RLS de las que depende todo
lo anterior— nacía de EF Core desde la máquina de un desarrollador y no se podía
revisar en un pull request ni recrear desde cero.

**Mitigación.** Baseline reproducible e idempotente más doce migraciones
versionadas, sin datos ni secretos. Suite de pruebas ejecutable.

**Evidencia.** Las trece migraciones se aplicaron contra la base real dentro de
transacciones revertidas, sin error.

**Residual.** Bajo, condicionado a que Antigravity quite la línea del
`.gitignore` (pedido en `docs/SUPABASE_MIGRATION_CONTRACT.md` §5.1). Mientras
siga ahí, cualquier migración nueva se pierde en silencio.

---

## Lo que este modelo no cubre

- Seguridad física y del endpoint Windows: si el equipo está comprometido, el
  atacante tiene la sesión del empleado. Fuera de alcance.
- Disponibilidad del proveedor.
- Contenido malicioso dentro de un `.docx` procesado por Word Interop
  (superficie de Codex).
- La app mobile (`Alquitel.Mobile`), que consume la misma base: hereda todas
  estas políticas y hay que verificarla por separado.
- Backups: dónde viven, quién los puede leer y si están cifrados en reposo.
  Nada de esto se revisó.
