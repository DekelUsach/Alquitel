# Vista previa Word en tiempo real — Editor de Productos

**Fecha:** 2026-07-13
**Estado:** Aprobado

## Objetivo

Al editar un producto en `ProductEditorView`, el usuario puede abrir un panel lateral que muestra en tiempo real cómo se verá el producto renderizado en el documento Word del presupuesto (título estilizado, imagen, campos personalizados y tabla de costos), sin generar un documento real.

## Enfoque elegido

**Simulación WPF nativa** (opción A). Un panel XAML imita la hoja de Word replicando la geometría y estilos exactos de `ProductRenderer.cs` (Infrastructure/WordInterop). No se usa Word COM ni OpenXML: fidelidad ~90-95%, actualización instantánea, sin dependencia de Word instalado.

Alternativas descartadas: docx real vía OpenXML + conversión a imagen (complejo, lento); Word COM en background con debounce (segundos por refresh, requiere Word, riesgo de procesos huérfanos).

## UX

- Botón toggle "Vista Word" en la zona del editor (header de la columna derecha).
- Al activarlo se abre una **tercera columna** (~400-450 px) a la derecha del formulario, con una "hoja de papel" blanca (fondo blanco fijo, independiente del tema claro/oscuro) con sombra, dentro de un `ScrollViewer`.
- El estado `IsPreviewVisible` vive en `ProductEditorViewModel`; default `false`.
- La hoja se actualiza en vivo al tipear (bindings `UpdateSourceTrigger=PropertyChanged` ya existentes en los campos del formulario).

## Contenido del preview (calcado de ProductRenderer.cs)

Escala: `PxPerCm ≈ 37.8` (96 DPI), con factor de escala global para que la hoja entre en el ancho del panel.

1. **Título** — segmentos de `DescriptionSegments`: color, negrita (default bold como en Word), cursiva. Fuente Montserrat con fallback Calibri. Tamaño equivalente a 12pt. Sangría izquierda equivalente a 1.9 cm.
2. **Imagen** — si `EditImagePath` existe, miniatura equivalente a 1.6×1.6 cm posicionada en el margen izquierdo del título (detrás/al costado del texto, como `wdWrapBehind`).
3. **Campos personalizados** — por cada `CustomFieldViewModel`: `Label: ` con negrita/subrayado/color del campo, seguido del `Value` parseado con `TagParser.Parse(value, colorHex)` (soporta tags `[red]`, `[b]`, etc. embebidos). Tamaño equivalente a 9pt, justificado, sangrías equivalentes a 0.66 cm izquierda + 1.25 cm primera línea + 0.81 cm derecha.
4. **Tabla resumen 1×4** — fondo azul (mismo color que `TagParserInterop.WD_BLUE` convertido a RGB), texto blanco bold Montserrat. Valores de ejemplo: `Cant.: 1`, `Días: 1`, `Costo U.: {PrecioBase:N0 es-AR}`, `Total: $ {PrecioBase:N0 es-AR}`. Anchos proporcionales 2.0/1.75/3.44/4.81 cm, sangría izquierda equivalente a 5.25 cm. "Total" en tamaño mayor (12pt vs 10pt).

## Arquitectura

- **Solo capa UI.** Sin cambios en Core, Infrastructure ni DB.
- `ProductEditorView.xaml`: tercera columna con el panel; DataTemplates para segmentos de título y campos personalizados en modo "Word".
- `ProductEditorViewModel.cs`: propiedad `IsPreviewVisible` + comando toggle; propiedades derivadas para las celdas de la tabla de ejemplo (formateo es-AR de `EditBasePrice`).
- El valor de campos personalizados con tags embebidos requiere parsear `Value` → lista de segmentos para el preview. Se expone en `CustomFieldViewModel` una propiedad derivada (ej. `PreviewSegments`) recalculada al cambiar `Value`/`ColorHex`, reutilizando `TagParser` de Core.
- Converters existentes (`BoolToFontWeightConverter`, `HexToColorConverter`) se reutilizan; posible converter nuevo para subrayado (`BoolToTextDecorationsConverter`).

## Tiempo real

`DescriptionSegments` y `CustomFields` son `ObservableCollection` con items `ObservableObject`; cada elemento del preview se bindea directo a las propiedades del segmento/campo, así el refresh es por elemento y sin re-render global ni timers.

## Manejo de errores

- Imagen inexistente o ilegible: se omite (mismo comportamiento best-effort que `ProductRenderer`).
- Color hex inválido: fallback negro (igual que `TagParser`).

## Testing

- La lógica nueva es casi toda XAML. En VM: `IsPreviewVisible` y formateo de precio — trivial.
- Si `PreviewSegments` (parsing de tags para preview) queda en `CustomFieldViewModel`, la lógica de parsing ya está cubierta por `TagParserTests` en Core.
- Verificación visual ejecutando la app: editar título/campos/imagen/precio y comprobar actualización en vivo.
