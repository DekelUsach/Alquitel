# Plan: Implementación de las 10 propuestas UX/UI (2026-07-14)

**Goal:** Implementar las propuestas aprobadas que aún no existen en el código.

**Auditoría previa** (ya existen, no se tocan):
- P1 Transiciones de navegación → `MainWindow.xaml` + code-behind (fade/slide 200ms). HECHO.
- P2b Semáforo de estados → `OrderStatusToBrushConverter` en OrderPool. HECHO.
- P3a Toast con acción → `IToastService.ShowSuccess(msg, label, action)`. HECHO.
- P3c Undo carrito Ctrl+Z → `_undoSnapshots` en BudgetBuilderViewModel. HECHO.
- P4a Skeleton en Presupuestos → `SkeletonRow` + IsLoading. HECHO.
- P5a Repetir pedido (dashboard) → `LoadOrderCopyByIdAsync`. HECHO.
- P6a Client.InternalNotes + SpecialDiscountPercent. HECHO.
- P8a Validación modal + CUIT/fechas inline. HECHO.

**Tareas restantes:**

1. **T1 Indicador de autosave** — BudgetBuilderViewModel: `AutosaveStatus` (string) actualizado
   por el loop de autosave; BudgetBuilderView: punto verde + texto en barra superior.
2. **T2 Toast Deshacer al quitar ítem** — RemoveItem → toast "«X» quitado — Deshacer".
3. **T3 Skeleton en OrderPool** — IsLoading + SkeletonRow (patrón PresupuestosView).
4. **T4 Repetir desde el Pool** — OrderPoolViewModel.RepeatOrderAsync + botón en fila.
5. **T5 Precios actualizados al repetir** — LoadOrderCopyByIdAsync refresca UnitPrice al
   BasePrice actual, avisa cambios y productos archivados por toast.
6. **T6 Ficha rápida de cliente** — al elegir cliente: últimas 3 órdenes, badge frecuente
   (3+ órdenes/12 meses), notas internas. Card compacta en el rail.
7. **T7 Teclado total en buscador** — `SearchQueryParser` en Core (tests): "3*proyector" →
   (3, "proyector"); Enter agrega el primer match visible con esa cantidad.
8. **T8 Panel de advertencias pre-generación** — `GenerationWarnings` (fecha pasada, sin
   lugar, sin CUIT) visible sobre los botones Generar; no bloquea.
9. **T9 Combos de evento** — entidad `EventTemplate` (ItemsJson), DbSet + migración EF
   (SQLite) + SQL Supabase; guardar carrito como combo (InputPromptWindow) y cargar combo.
10. **T10 Resumen semanal** — `IWeeklySummaryService` (Core) + `WeeklySummaryService`
    (OpenXML puro, sin Word); trigger primer arranque de la semana (setting
    LastWeeklySummary); toast "Abrir". Carpeta `%LocalAppData%\Alquitel\Resumenes`.

**Reglas:** IDbContextFactory siempre; sin Cascade en FKs; auditar con IOrderAuditService;
migración EF verificada en SQLite y SQL equivalente aplicado a Supabase; tests de Core para
lógica pura nueva (SearchQueryParser, WeeklySummaryScheduler); `dotnet build` + tests verdes.
