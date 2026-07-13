# 🧭 Recomendaciones técnicas — Alquitel

> Auditoría informal del estado del sistema (julio 2026) con recomendaciones priorizadas:
> seguridad, arquitectura, features y diseño visual. Prioridades: **P0** = urgente,
> **P1** = próximo ciclo, **P2** = mediano plazo, **P3** = deseable.

---

## 1. 🔐 Seguridad

### P0 — ServiceKey de Supabase en `appsettings.json` (working copy)
El commit `774edf8` limpió los secretos del repo, pero la copia de trabajo actual de
`Alquitel.UI/appsettings.json` volvió a tener `AnonKey` **y `ServiceKey`** (service_role = acceso
total a la base, saltea RLS). Si se commitea tal cual, la key queda en el historial de git para siempre.

- Mover `ServiceKey` a `appsettings.local.json` (gitignoreado) **antes del próximo commit**. La AnonKey sí puede viajar en el binario.
- Si la ServiceKey llegó a pushearse alguna vez, **rotarla** desde el dashboard de Supabase.
- Considerar un hook de pre-commit (o `dotnet tool` como gitleaks) que bloquee commits con `eyJ...`/`sk_...`.

### P0 — Row Level Security en Supabase
El rol `anon` (AnonKey distribuida en el binario) no debería poder leer/escribir tablas de negocio.
Verificar que todas las tablas (`Orders`, `Clients`, `Products`, `Users`, …) tengan RLS activo y
que solo el rol `alquitel_app` (connection string por máquina) tenga acceso real.

### P1 — Contraseñas de usuarios
El login admite usuarios sin contraseña (Admin inicial se crea sin hash). Para un sistema
multi-puesto con datos de facturación: obligar a setear contraseña al primer login de un rol Admin
y auditar el algoritmo de hash usado (ideal: PBKDF2/Argon2 con salt, no SHA plano).

### P2 — Backups: restore y cifrado
`DatabaseBackupService` copia la DB cada 6 h (retiene 20), pero **no existe flujo de restauración**:
ante un desastre hay que copiar archivos a mano. Agregar en Configuración → "Restaurar backup"
(lista de copias con fecha + confirmación). Evaluar cifrar los backups si el equipo es compartido.

---

## 2. 🏗️ Arquitectura

### P0 — Cero tests automatizados
No hay ningún proyecto de test en la solución. `Alquitel.Core` es lógica pura ideal para unit tests
baratos: `CuitValidator`, `TagParser`, `BudgetNumberHelper`, `SpanishDateFormatter`. Crear
`Alquitel.Core.Tests` (xUnit) y correrlo en CI. Sin esto, cada refactor del armador es ruleta rusa.

### P1 — Extraer el motor de Smart Search del ViewModel
`BudgetBuilderViewModel` supera las 1.400 líneas y mezcla 5 responsabilidades: carrito, persistencia,
generación de documentos, autosave y el motor completo de scoring (tokens/trigramas/Dice) + retrieval
para la IA. Plan de división incremental:

1. `Alquitel.Core/Search/ProductMatcher.cs` — scoring + segmentación + retrieval (hoy métodos privados del VM). Se vuelve testeable y reutilizable.
2. `OrderPersistenceService` (Infrastructure) — `PersistOrderAsync`/`ResolveClientIdAsync` no son responsabilidad de un VM.
3. `DraftService` — autosave loop + lectura/borrado de drafts.

### P1 — `DeleteBehavior.Restrict` explícito en las FK de `Order`
Las FK `Order→Location` y `Order→Client` quedaron en **Cascade** por convención EF: borrar un padre
borra presupuestos en silencio. La UI ya lo mitiga (reasignación a "(Sin ubicación)"), pero la defensa
debe estar en la base: configurar `OnDelete(DeleteBehavior.Restrict)` en `AlquitelDbContext` +
migración. También resuelve los warnings de EF sobre query filters (`Client`/`Product` filtrados
siendo extremo requerido de una relación).

### P1 — Unificar el acceso a datos
Convivencia de dos patrones: repositorios (`IOrderRepository`, etc.) y `IDbContextFactory` directo en
ViewModels (`BudgetBuilderViewModel`, `LocationsViewModel`). Elegir uno (repositorios) y migrar
gradualmente; simplifica el testeo y la eventual migración total a servidor.

### P2 — Reemplazar Word COM Interop por OpenXML SDK
El motor COM funciona pero es la parte más frágil del sistema: requiere Word instalado, es lento
(proceso completo de Word por documento), y deja `WINWORD.EXE` huérfanos si algo falla. Migrar a
`DocumentFormat.OpenXml` (o una lib como OpenXML PowerTools):

- Genera .docx sin Word instalado → habilita generación en servidor/nube a futuro.
- ~10× más rápido, sin STA threads ni `Marshal.ReleaseComObject`.
- Esfuerzo alto (el renderizado de productos con imágenes/tablas hay que reescribirlo) — hacerlo detrás de `IDocumentService` que ya existe, con feature flag para poder volver a COM.

### P2 — CI en GitHub Actions
Ya existe `.github/`. Agregar workflow mínimo: `dotnet build` + `dotnet test` en cada PR, y
`dotnet publish` + release de Velopack al taggear. Elimina el "compila en mi máquina".

### P3 — Consistencia en el uso de `IDispatcher`
Existe la abstracción `IDispatcher`, pero hay ViewModels que llaman `App.Current.Dispatcher.Invoke`
directo (ej. `LocationsViewModel`). Unificar para poder testear VMs sin WPF.

---

## 3. ✨ Features nuevas

### P1 — Recuperación de borradores
El autosave escribe drafts JSON cada 30 s en `%AppData%\Alquitel\Drafts`, **pero nada los lee**: si
la app se cierra con un pedido a medias, el trabajo se pierde igual. Al abrir el armador, detectar
drafts recientes y ofrecer "Recuperar pedido sin guardar (hace 5 min)".

### P1 — Calendario de disponibilidad de stock
Ya existe el chequeo puntual de conflicto (⚠ por ítem). El paso natural: vista calendario por
producto con la ocupación comprometida por fecha (qué órdenes lo usan y cuántas unidades quedan).
Transforma el control de stock de reactivo a planificable.

### P2 — Motor comercial: descuentos e IVA
Hoy el total es suma lineal de subtotales. Faltan herramientas comerciales básicas:
- Descuento global (% o monto) con visualización en el documento.
- Toggle IVA (precio neto / IVA incluido) según tipo de cliente.
- Precios especiales por cliente (lista de precios o % acordado en la ficha del cliente).

### P2 — Envío directo por correo
Botón "Enviar por mail" tras generar: adjunta el .docx/PDF y abre un borrador (mailto con Outlook
COM ya que Office está instalado, o Microsoft Graph a futuro). Ahorra el paso manual más frecuente.

### P2 — Historial y auditoría multi-usuario
Ya se registra `CreatedByUserId`. Agregar bitácora simple de eventos (quién generó, editó, borró,
cambió estado y cuándo) visible en la ficha del presupuesto. Con varios usuarios compartiendo base,
"¿quién tocó esto?" aparece rápido.

### P2 — Más IA barata (aprovechando la integración existente)
Con `IAiOrderParser` ya montado sobre Pollinations, hay quick-wins de bajo costo:
- Autocompletar **notas técnicas** de la OT a partir de los productos elegidos.
- Resumir el historial de pedidos de un cliente al abrir su ficha.
- Detectar datos del cliente (empresa, contacto, teléfono) en el mismo texto pegado del pedido automático y ofrecer completar el formulario.

### P3 — Paleta de comandos (Ctrl+K)
Buscador global: clientes, presupuestos, productos y acciones ("nueva ubicación", "generar OT") en
un solo lugar. Patrón probado (VS Code, Linear) y barato de implementar sobre los repos existentes.

### P3 — Indicador de conexión en modo servidor
Con provider Supabase, si se cae internet la app falla por timeouts crudos. Mostrar estado de
conexión en la status bar y mensaje claro de reintento (la política Polly ya existe para Word;
extenderla a red).

---

## 4. 🎨 Diseño visual y UX

### P1 — Consistencia con el nuevo lenguaje visual
El armador rediseñado (tarjetas, steppers, rail) convive con vistas de generación anterior
(`ClientsView`, `ProductEditorView`, `SettingsView` con formularios más crudos). Migrarlas al mismo
sistema: tarjetas `Card`/`CardInner`, `SectionIconBox`, mini-labels, steppers donde haya cantidades.

### P1 — Toasts en lugar de MessageBox para confirmaciones
Cada acción exitosa hoy interrumpe con un modal ("Archivo guardado correctamente…"). Para éxitos y
avisos no críticos usar toast/snackbar auto-descartable (3-5 s) con acción opcional ("Abrir carpeta",
"Deshacer"). Los modales quedan solo para errores y confirmaciones destructivas.

### P2 — Validación inline con feedback positivo
El CUIT ya se valida (Módulo 11) pero silenciosamente. Mostrar ✓ verde / ✗ con mensaje al pie del
campo mientras se tipea (validar en blur, no por tecla). Ídem fechas incoherentes (fin < inicio)
antes de llegar al diálogo de error de generación.

### P2 — Estados de carga
Las listas grandes (presupuestos, clientes) cargan sin feedback. Skeleton rows o shimmer simple
mientras llegan datos (crítico en modo Supabase con latencia de red).

### P2 — Iconografía Fluent
`Segoe MDL2 Assets` funciona pero es la generación Windows 10. En Windows 11 existe
`Segoe Fluent Icons` (mismos codepoints, trazo moderno): detectarla y usarla con fallback a MDL2 —
cambio de una línea en `App.xaml` con `FontFamily` compuesta.

### P3 — Micro-animaciones
Transiciones de 150-250 ms (fade/slide sutil) al navegar entre secciones y al agregar/quitar tarjetas
del pedido. WPF Storyboards; respetar `SystemParameters.ClientAreaAnimation` para accesibilidad.

### P3 — Accesibilidad
- `AutomationProperties.Name` en botones de solo-ícono (steppers, papelera) para lectores de pantalla.
- Revisar contraste del tema oscuro (`MutedText #7D8590` sobre `Surface #161B22` roza el límite AA para texto chico).
- Targets táctiles/click: varios botones quedaron en 30 px; llevar los de uso frecuente a ≥36-40 px.

### P3 — Rail colapsable
En ventanas angostas (~1000 px) el armador queda apretado. Permitir colapsar el rail derecho a un
resumen mínimo (total + botón generar) con expansión al click.

---

## 5. 🧹 Higiene de repo (quick wins, < 1 h total)

| Ítem | Acción | Estado |
|---|---|---|
| `~$mplateOT.docx` suelto en la raíz | `~$*.docx`/`~$*.doc` agregados al `.gitignore`; lock file huérfano borrado | ✅ Hecho |
| Converters muertos | `ProductButtonTextConverter`, `ProductButtonBackgroundConverter` y `ProductRemoveButtonVisibilityConverter` eliminados | ✅ Hecho |
| Seed de demo en producción | Productos DEMO ahora solo se insertan en builds `DEBUG`; ubicaciones y usuario Admin se siguen sembrando siempre | ✅ Hecho |
| Estilo legado | `DarkPillButton` retirado de `App.xaml` (sin usos) | ✅ Hecho |
| Warnings NU1701 | Investigado: `OpenTK`/`SkiaSharp.Views.WPF` son dependencias **transitivas** de `LiveChartsCore.SkiaSharpView.WPF 2.0.0-rc5.4` (gráficos de Reportes). No se pueden quitar; el warning desaparece al actualizar LiveCharts cuando salga la versión estable | ✅ Investigado |

---

## 6. ✅ Estado de implementación (12/07/2026)

| Ítem | Estado | Notas |
|---|---|---|
| P0 ServiceKey fuera de appsettings.json | ✅ | Movida a `appsettings.local.json` + hook pre-commit anti-secretos (`.githooks/pre-commit`, activar con `git config core.hooksPath .githooks`). Nunca se pusheó: no hace falta rotar. |
| P0 RLS en Supabase | ✅ Verificado | RLS activo en las 6 tablas, policies solo para `alquitel_app`, `anon` sin grants. Advisors de seguridad: 0 hallazgos. |
| P0 Tests | ✅ | `Alquitel.Core.Tests` (xUnit): 87 tests — CuitValidator, TagParser, BudgetNumberHelper, SpanishDateFormatter, ProductMatcher, totales de Order. |
| P1 Contraseña Admin | ✅ | Primer login de Admin sin password obliga a definir una (PasswordPromptWindow). Hash ya era PBKDF2-SHA256 100k iteraciones + salt + comparación en tiempo constante. |
| P1 Extraer motor del VM | ✅ | `Core/Search/ProductMatcher`, `OrderPersistenceService`, `DraftService` (+ interfaces). BudgetBuilderViewModel bajó ~400 líneas. |
| P1 DeleteBehavior.Restrict | ✅ | Migración `RestrictOrderFks` (Order→Client/Location, OrderItem→Product). Remoto ya estaba en NO ACTION. |
| P1 Recuperación de borradores | ✅ | Al abrir el armador ofrece el draft más reciente (&lt;3 días), una vez por sesión. |
| P1 Calendario de stock | ✅ | Card "Disponibilidad" en Productos: 30 días con unidades libres + órdenes que comprometen. |
| P1 Toasts | ✅ | `IToastService` + host en MainWindow; éxitos ya no interrumpen (acción "Abrir carpeta"). |
| P1 Consistencia visual | ✅ | Las vistas ya estaban migradas; se compartieron StepperButton/MiniLabel en App.xaml, stepper en stock y targets 36px. |
| P2 Validación inline + skeletons + Fluent | ✅ | CUIT ✓/✗ al pie (armador y ficha), error de fechas fin&lt;inicio, skeleton en Presupuestos/Clientes, `Segoe Fluent Icons` con fallback MDL2. |
| P2 Motor comercial | ✅ | Descuento % + monto, toggle IVA 21%, precio especial por cliente (se aplica al seleccionarlo). Placeholders `{{SUBTOTAL}}/{{DESCUENTO}}/{{IVA}}/{{TOTAL}}`. Migración local + columnas en Supabase. |
| P2 Envío por mail | ✅ | Botón en el rail: borrador de Outlook (COM) con .docx + PDF adjuntos. |
| P2 Auditoría | ✅ | Tabla `OrderAuditEvents` (local + Supabase con RLS): creado/editado/generado/cambio de estado. Botón Historial en Seguimiento. |
| P2 Restore de backups | ✅ | Configuración → Restaurar backup (lista + confirmación + copia PreRestore). Solo modo SQLite. |
| P2 IA barata | ✅ | `IAiTextAssistant` (Pollinations): notas técnicas OT ("Notas IA"), resumen de historial en ficha de cliente, detección de datos del cliente en el texto pegado. |
| P2 CI | ✅ | `.github/workflows/ci.yml`: build + test en PR/push; publish self-contained al taggear `v*` (empaquetado Velopack pendiente sobre ese publish). |
| P2 OpenXML | ✅ Experimental | `OpenXmlDocumentService` detrás del flag `Documents:Engine` (`"com"` default / `"openxml"`). Smoke-test verde contra templateOT.docx. Limitaciones: sin imagen flotante, sin PDF, sin BK_EQUIPMENT_TABLE. |
| P3 IDispatcher | ✅ | Clients y Locations ya no usan `App.Current.Dispatcher`. |
| P3 Micro-animaciones | ✅ | Fade+slide 200 ms al navegar (respeta `ClientAreaAnimation`), entrada animada de toasts. |
| P3 Accesibilidad | ✅ | `AutomationProperties.Name` en botones de ícono, MutedText oscuro `#7D8590`→`#8B949E` (~5.4:1), targets 30→36 px. |
| P3 Paleta de comandos | ✅ | Ctrl+K: navegación + búsqueda de clientes/productos/presupuestos (abre la orden en el armador). |
| P3 Indicador de conexión | ✅ | Chequeo periódico en modo servidor: status bar en rojo + "Sin conexión — reintentando…". |
| P3 Rail colapsable | ✅ | Botón chevron: colapsa a 64 px (total + generar) y se expande al click. |

---

