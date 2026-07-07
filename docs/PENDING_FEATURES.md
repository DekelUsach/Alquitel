# 🧭 Features Pendientes (para implementar en una sesión futura)

> **Actualización 2026-07-06 — estado de implementación:**
> - **§1 Multi-usuario**: ✅ IMPLEMENTADO (variante "una sola DB compartida"). Proyecto Supabase
>   `Alquitel` (ref `qgtaugmxmoxtpxvmugvt`, sa-east-1) con EF Core + Npgsql (Opción A del plan),
>   entidad `User` con roles Admin/Vendedor, login al inicio, gates de rol en la sidebar y
>   `Order.CreatedByUserId` conviviendo con `AdminName`.
> - **§2 Stock**: ✅ IMPLEMENTADO (cantidad total por producto, advertencia sin bloqueo, ⚠ en grilla).
> - **§3 Reportes**: ✅ IMPLEMENTADO (`ReportsView` con rango de fechas, facturación por
>   cliente/mes, rentabilidad con `Product.Cost`, export CSV). Sin gráfico (no se agregó
>   dependencia de charting; la tendencia mensual es una grilla).
> - **§4 Firma digital / aprobación por link**: ❌ PENDIENTE (único faltante; ahora que el backend
>   Supabase existe, su prerequisito está resuelto).

Este documento describe 4 mejoras identificadas para Alquitel. Cada sección da suficiente
contexto para que otra IA (o desarrollador) las implemente sin haber participado en la
conversación original.

> Antes de tocar código: preguntar al usuario las "Decisiones pendientes" de la sección
> correspondiente. Implementar sin esas respuestas obliga a adivinar el modelo de datos.

---

## 1. Multi-usuario con roles

### Qué es
Hoy la app es mono-usuario: cada instalación tiene su propia SQLite en
`%LocalAppData%\Alquitel\Alquitel.db` (ver [AppPaths.cs](../Alquitel.Infrastructure/AppPaths.cs)).
No hay login, no hay noción de "quién hizo qué" salvo el campo de texto libre
`Order.AdminName` (que el usuario tipea a mano, no se valida contra nada).

### Por qué importa
Si el equipo crece a 2+ personas cargando presupuestos, hoy tienen bases de datos
**separadas y no sincronizadas** — cada vendedor ve solo lo que él cargó en su máquina.
No hay control de quién puede archivar clientes, editar precios de catálogo, etc.

### Decisiones pendientes (preguntar al usuario)
1. ¿Multi-usuario significa "varias personas comparten una sola base de datos" o
   "cada persona sigue con su base local pero necesitamos saber quién hizo cada acción"?
   — Esto determina si hace falta backend compartido (ver §4 de
   [SUPABASE_MIGRATION.md](SUPABASE_MIGRATION.md)) o alcanza con agregar un `User` local.
2. ¿Qué roles existen? (ej. Admin: todo, Vendedor: solo presupuestos, no toca catálogo/settings).
3. ¿Necesitan contraseña o alcanza con "elegir tu nombre de una lista" al abrir la app?

### Estado actual del código relevante
- `Order.AdminName` (string libre) en [Order.cs](../Alquitel.Core/Entities/Order.cs) — sin
  relación a ninguna tabla de usuarios.
- No existe entidad `User` en `Alquitel.Core/Entities/`.
- `App.xaml.cs > ConfigureServices` registra todos los servicios como Singleton/Transient sin
  contexto de sesión de usuario.
- `IDialogService`, `INavigationService` ya están inyectados vía DI — un futuro
  `ICurrentUserService` seguiría el mismo patrón.

### Pasos de implementación sugeridos (si la respuesta es "un solo local, con roles")
1. Nueva entidad `Alquitel.Core/Entities/User.cs`: `Id (Guid)`, `Name`, `Role (enum Admin/Vendedor)`,
   `PasswordHash` (opcional, usar `System.Security.Cryptography` con salt, nunca texto plano).
2. Migración EF: `dotnet ef migrations add AddUsers --project Alquitel.Infrastructure --startup-project Alquitel.UI`.
3. `ICurrentUserService` en Core (interfaz) + implementación simple en Infrastructure que guarda
   el usuario logueado en memoria durante la sesión de la app.
4. Pantalla de login simple al arrancar (antes de `MainWindow.Show()` en `App.xaml.cs`), o un
   selector de usuario en el `MainViewModel` si no hace falta contraseña.
5. Gate de permisos: en `MainViewModel`, ocultar/deshabilitar botones de sidebar según
   `ICurrentUserService.Current.Role` (ej. Vendedor no ve "Productos" ni "Configuración").
6. Reemplazar `Order.AdminName` (texto libre) por `Order.CreatedByUserId (Guid?)` — **cuidado**:
   esto es un breaking change de esquema, requiere migración de datos existentes (parsear el
   nombre libre e intentar matchear contra la tabla `Users` nueva, o dejar ambos campos convivir).

### Pasos de implementación sugeridos (si la respuesta es "varias personas, una sola DB")
Esto es un prerequisito más grande: implica resolver primero la migración a un backend
compartido (Supabase u otro). Ver [SUPABASE_MIGRATION.md](SUPABASE_MIGRATION.md) completo antes
de tocar roles — no tiene sentido implementar roles sobre una base de datos que sigue siendo
local a cada máquina.

---

## 2. Alertas de disponibilidad / control de stock

### Qué es
Avisar si dos presupuestos "Aprobados" (o en cualquier estado activo) comparten fecha de
evento y usan el mismo producto en una cantidad que excede el stock físico disponible.

### Por qué importa
Hoy nada impide vender 10 pantallas LED para el mismo fin de semana en dos eventos distintos
si solo hay 6 unidades reales. El sistema no tiene noción de cantidad física — `Product`
representa un tipo de ítem de catálogo, no un inventario serializado.

### Decisiones pendientes (preguntar al usuario)
1. ¿El control es por **cantidad total** del producto (ej. "tenemos 20 metros de pantalla LED
   2.6mm") o por **unidades físicas individuales** con número de serie/tracking?
   — La primera opción es mucho más simple de implementar.
2. ¿Qué se considera "conflicto"? ¿Solo presupuestos en estado `Approved`/`SentToOT`, o también
   `Draft`? (Probablemente solo confirmados, para no bloquear cotizaciones especulativas).
3. ¿El alcance de fecha es "mismo día exacto" o hay que considerar el rango completo
   `EventDate` → `EventDate + Dias` de cada `OrderItem`? (Los items ya tienen `Dias` individual,
   ver [Order.cs](../Alquitel.Core/Entities/Order.cs) línea ~74).
4. ¿Bloquea la generación del documento (hard stop) o solo advierte y deja continuar?

### Estado actual del código relevante
- `Product` ([ProductAndLocation.cs](../Alquitel.Core/Entities/ProductAndLocation.cs)) no tiene
  ningún campo de cantidad/stock hoy.
- `OrderItem.Dias` existe y representa cuántos días dura el alquiler de ese ítem específico,
  pero `Order.EventDate` es la única fecha de referencia (no hay fecha de fin explícita —
  se infiere fecha de evento + días del item).
- La validación de generación ya existe en
  `BudgetBuilderViewModel.ValidateOrderForGeneration()` — es el lugar natural para agregar
  un chequeo de disponibilidad antes de generar el documento.
- No hay ningún servicio de consulta cross-order hoy; habría que agregar un método al futuro
  `IOrderRepository` (ya creado en
  [IOrderRepository.cs](../Alquitel.Core/Interfaces/Repositories/IOrderRepository.cs)) o a
  `AlquitelDbContext` directamente.

### Pasos de implementación sugeridos
1. Agregar `Product.StockQuantity (int?)` — nullable para no romper productos sin control de
   stock (ej. servicios, traslados). Migración EF.
2. Nuevo método `IOrderRepository.GetActiveConflictsAsync(Guid productId, DateTime from, DateTime to, Guid excludeOrderId)`
   que suma cantidades de `OrderItem` para ese producto en órdenes con estado relevante cuyo
   rango de fecha se solapa con el rango pedido.
3. En `BudgetBuilderViewModel.ValidateOrderForGeneration()`, antes de generar, iterar
   `SelectedItems` y consultar disponibilidad; si `stock disponible < cantidad pedida`, agregar
   error a la lista existente de `errors` (mismo patrón que ya usa el método).
4. UI: mostrar un ícono de advertencia (⚠) directamente en la fila del `DataGrid` de
   [BudgetBuilderView.xaml](../Alquitel.UI/Views/BudgetBuilderView.xaml) cuando hay conflicto,
   no solo al momento de generar — mejor UX que enterarse recién al final.

---

## 3. Reportes (facturación por cliente/mes, rentabilidad de productos)

### Qué es
Sección dedicada de reportes con filtros de rango de fechas: facturación total por cliente,
por mes, y ranking de productos por rentabilidad (no solo por cantidad de veces alquilado,
que es lo que ya existe en el Dashboard).

### Por qué importa
El Dashboard ya tiene una versión básica (`RevenueLast30Days`, `TopProducts` por cantidad de
veces presupuestado — ver [DashboardViewModel.cs](../Alquitel.UI/ViewModels/DashboardViewModel.cs)),
pero es una ventana fija de 30 días sin filtros ni exportación. El usuario probablemente
necesita comparar mes a mes o filtrar por cliente específico para reuniones comerciales.

### Decisiones pendientes (preguntar al usuario)
1. ¿"Rentabilidad" se calcula con qué dato? Hoy `Product.BasePrice` es el precio de alquiler,
   no hay campo de costo. Sin un `Product.Cost` no se puede calcular margen real, solo
   facturación bruta.
2. ¿Necesita exportar a Excel/PDF, o alcanza con verlo en pantalla?
3. ¿Reportes por vendedor individual? (depende de si se implementa §1 primero).

### Estado actual del código relevante
- `DashboardViewModel.LoadExtendedMetricsAsync()` ya tiene la query base que se puede
  generalizar: agrupa `OrderItem` por `Product.Description` y cuenta ocurrencias. Reusar esa
  lógica con un rango de fechas parametrizable en vez de fijo a 30 días.
- `Order.Total` ya sabe sumar sus `Items` (`Items.Sum(i => i.Total)`), así que "facturación por
  cliente" es un `GroupBy(o => o.ClientId).Select(g => g.Sum(o => o.Total))`.
- No hay ninguna vista/ViewModel de reportes hoy — sería una sección nueva completa:
  `ReportsViewModel.cs` + `ReportsView.xaml`, agregada a la sidebar de
  [MainWindow.xaml](../Alquitel.UI/MainWindow.xaml) siguiendo el mismo patrón que las demás
  7 secciones (registro en `App.xaml.cs > ConfigureServices`, entrada en
  `MainWindow.xaml.cs` DataTemplate, botón de navegación en el menú, comando en
  `MainViewModel.cs`).

### Pasos de implementación sugeridos
1. (Si aplica) Agregar `Product.Cost (decimal?)` para poder calcular margen real, con
   migración EF. Si el usuario no quiere exponer costos en la UI de catálogo normal, agregarlo
   como campo opcional colapsado/oculto tras un toggle "modo avanzado".
2. Nueva vista `ReportsView.xaml` con: selector de rango de fechas (dos `DatePicker`, ya
   estilizados en `App.xaml`), grilla de facturación por cliente, grilla de top productos por
   rentabilidad, gráfico simple de tendencia mensual (evaluar `LiveCharts2` o `OxyPlot` como
   dependencia nueva — ninguna está instalada hoy).
3. `ReportsViewModel : ObservableObject, IAsyncInitialization` con métodos que reusan/generalizan
   las queries ya escritas en `DashboardViewModel`.
4. Exportación: reusar el patrón CSV ya implementado en `ClientsViewModel.ExportCsv()` y
   `ProductEditorViewModel.ExportCsv()` (separador `;`, BOM UTF-8) para consistencia.

---

## 4. Firma digital / aprobación de presupuesto por link

### Qué es
Que el cliente final pueda aprobar un presupuesto haciendo clic en un link (sin llamar por
teléfono ni responder un mail), quedando registrado en el sistema.

### Por qué importa
Reduce fricción en el ciclo de venta y deja un registro auditable de cuándo/quién aprobó,
en vez de depender de un llamado telefónico o un "ok" verbal.

### Decisiones pendientes (preguntar al usuario)
1. Esto **requiere un componente de servidor público** (una página web que el cliente abre
   desde el link, con su propio backend). ¿El usuario ya tiene o planea tener el backend de
   Supabase (ver [SUPABASE_MIGRATION.md](SUPABASE_MIGRATION.md)) funcionando? Sin backend
   remoto, esta feature no es viable — la app de escritorio no puede exponer una URL pública
   por sí sola.
2. ¿"Firma digital" es literal (firma manuscrita capturada, o certificado digital con validez
   legal) o alcanza con un botón "Aprobar" en una página web simple? La primera opción implica
   proveedores especializados (ej. DocuSign API, Firmafy) con costo por documento.
3. ¿Cómo se entrega el link al cliente? ¿Email automático desde la app, o el vendedor lo copia
   y pega manualmente en su propio cliente de correo?
4. ¿Qué pasa con el estado del presupuesto cuando se aprueba por link? (Debería mapear a
   `OrderStatus.Approved`, que ya existe en
   [Order.cs](../Alquitel.Core/Entities/Order.cs) y ya es seleccionable en el combo de
   estado del `BudgetBuilderView` — la parte de UI de estados ya está lista, falta la
   fuente de la transición automática).

### Estado actual del código relevante
- `OrderStatus` enum ya tiene `Draft/Approved/SentToOF/SentToOT/Archived/Rejected` — el modelo
  de estados que este flujo necesitaría ya existe, no hace falta tocarlo.
- No existe ningún componente web/API en el proyecto — es 100% aplicación de escritorio WPF.
  Esta feature es la única de las 4 que **no puede vivir dentro de `Alquitel.UI` en absoluto**;
  necesita un proyecto nuevo (ej. `Alquitel.ApprovalPortal`, ASP.NET Core minimal API o similar)
  desplegado en algún hosting público.
- El PDF ya se genera hoy (`IAppSettings.ExportPdf`, implementado en esta sesión) — es el
  documento candidato para adjuntar al link o mostrar embebido en la página de aprobación.

### Pasos de implementación sugeridos (asumiendo que Supabase ya está migrado)
1. Nueva tabla `order_approvals` en Supabase: `id`, `order_id`, `token (uuid único)`,
   `status (pending/approved/rejected)`, `approved_at`, `client_ip`.
2. Endpoint público (Supabase Edge Function o proyecto ASP.NET Core aparte) que:
   - `GET /aprobar/{token}` → muestra el PDF + botón "Aprobar" / "Rechazar".
   - `POST /aprobar/{token}` → marca `order_approvals.status`, dispara update de
     `orders.status` a `Approved`/`Rejected` vía Supabase client.
3. En `BudgetBuilderViewModel.GenerateDocument()`, después de generar el PDF exitosamente,
   generar el token, insertar la fila en `order_approvals` (vía el futuro repositorio remoto) y
   construir el link.
4. Mostrar el link generado en un diálogo simple con botón "Copiar" (reusar `IDialogService` o
   agregar un método nuevo tipo `ShowLinkToCopy`).
5. Polling o webhook: la app de escritorio necesita enterarse cuando el cliente aprueba desde
   afuera. Opción simple: refrescar el estado de la orden cuando el usuario abre
   `PresupuestosView` (ya tiene `FileSystemWatcher` para archivos, agregar un refresco de
   estado de `Orders` contra la base remota). Opción más elaborada: Supabase Realtime
   subscriptions (mencionado como ventaja de la Opción B en `SUPABASE_MIGRATION.md`).

---

## Resumen de dependencias entre features

```
§1 Multi-usuario (una sola DB compartida) ──┐
                                             ├──→ requieren migración a backend remoto
§4 Firma digital / aprobación por link ─────┘     (ver SUPABASE_MIGRATION.md)

§2 Stock/disponibilidad ────────→ independiente, se puede hacer 100% local hoy
§3 Reportes ────────────────────→ independiente, se puede hacer 100% local hoy
```

Si el usuario quiere priorizar por esfuerzo/impacto sin depender de infraestructura nueva,
**§2 y §3 son implementables ya, con SQLite local, sin esperar la migración a Supabase.**
§1 (si es "una sola DB compartida") y §4 dependen de resolver primero el backend remoto.
