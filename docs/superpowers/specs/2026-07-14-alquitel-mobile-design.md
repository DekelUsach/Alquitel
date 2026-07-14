# Alquitel.Mobile — Diseño v1 (2026-07-14)

## Objetivo
App mobile Android con todas las funcionalidades del sistema WPF que **no dependan de archivos locales** (OneDrive, Word Interop, plantillas, explorador .docx).

## Decisiones (aprobadas por el usuario)
- **Stack**: .NET MAUI (`net8.0-android`), reutilizando `Alquitel.Core` sin cambios.
- **Plataforma**: solo Android (APK de distribución interna).
- **Datos**: EF Core + Npgsql directo contra el pooler de Supabase (mismo modelo de confianza que el desktop). ConnectionString en configuración embebida/local, nunca commiteada.
- **Alcance**: todo lo no-local.

## Arquitectura
- Proyecto nuevo `Alquitel.Mobile` (MAUI). Referencia `Alquitel.Core` (net8.0 puro: entidades, ProductMatcher, CuitValidator, TagParser, PasswordHasher, BudgetNumberHelper, SpanishDateFormatter).
- **No** referencia `Alquitel.Infrastructure` (es `net8.0-windows`).
- `MobileDbContext` propio en el proyecto mobile: mapea las mismas tablas (Clients, Products, Locations, Orders, OrderItems, Users, OrderApprovals, OrderAuditEvents, EventTemplates) con la misma configuración relevante (query filters de soft-delete, FKs Restrict). **Sin migraciones**: el schema lo gobiernan las migraciones existentes (desktop/Supabase); mobile solo lee/escribe.
- MVVM con `CommunityToolkit.Mvvm`. Navegación con MAUI Shell (TabBar inferior + rutas de detalle).
- `IDbContextFactory<MobileDbContext>` para thread-safety (regla 1 de CLAUDE.md).

## Pantallas
1. **Login**: usuario + contraseña contra tabla `Users` con `PasswordHasher`. Sesión en memoria + preferencia "recordar usuario".
2. **Dashboard**: métricas (presupuestos del mes, pendientes de aprobación, aprobados), lista de pedidos recientes.
3. **Nuevo presupuesto**: entrada de texto natural → parser IA Pollinations (cliente HTTP portable propio, misma API gen.pollinations.ai/nova-fast, key en config local) con fallback a `ProductMatcher`; carrito editable (cantidad, días, precio congelado); selección de cliente (con CUIT validado), ubicación, fechas; número de presupuesto con `BudgetNumberHelper`; guarda `Order` + `OrderItems` con `DescriptionSnapshot`. No genera Word: la orden queda en el pool para que el desktop la documente.
4. **Pool de pedidos**: lista filtrable por estado y texto; detalle de orden con ítems; cambio de estado con registro en `OrderAuditEvents`.
5. **Aprobaciones**: generar `OrderApproval` (token UUID) y compartir link con Share nativo (WhatsApp/email); ver estado (pendiente/aprobado/rechazado).
6. **Clientes**: ABM completo, validación CUIT reactiva, soft-delete (archivar).
7. **Catálogo**: lista con búsqueda y filtro por categoría; detalle renderizando descripción segmentada con colores vía `TagParser` (spans en Label FormattedText) + campos técnicos. Solo lectura: la edición del catálogo (editor segmentado, campos dinámicos, imágenes) queda en el desktop. Sin imágenes en v1 (`ImagePath` apunta a rutas locales del desktop). Fase 2: Supabase Storage.
8. **Ubicaciones**: CRUD simple.
9. **Reportes**: totales por mes y tasa de conversión (queries agregadas).

## Fuera de alcance v1
Generación Word/PDF, plantillas, explorador de documentos, rutas OneDrive, drafts locales, Velopack, Outlook, backups locales, imágenes de productos.

## Errores y resiliencia
- Sin conexión: mensajes claros + reintento manual (app requiere red; sin modo offline en v1).
- Todas las operaciones DB en `try/catch` con toasts/alerts no bloqueantes.

## Testing
- La lógica pura ya está testeada en `Alquitel.Core.Tests`. Mobile agrega solo orquestación; verificación por build + smoke manual/emulador.

## UI
Diseño con skill ui-ux-pro-max: identidad corporativa Alquitel (azul `#1F68C7`, dark mode institucional), bottom tabs, cards, tipografía Montserrat.
