# Spec: Rediseño UI, Modo Oscuro, Bug Fixes y .gitignore
**Fecha:** 2026-04-13  
**Proyecto:** Alquitel — Sistema Administrativo (WPF .NET 8)

---

## 1. Alcance

Este spec cubre cuatro áreas independientes que se implementan en un único ciclo:

| # | Área | Tipo |
|---|---|---|
| 1 | Rediseño visual completo + modo oscuro toggle | Feature |
| 2 | Tabs funcionales (Presupuesto Comercial / Orden de Trabajo) | Feature |
| 3 | Bug fix: `Total` no incluye `Dias` + eliminación de debug code | Bug |
| 4 | `.gitignore` actualizado | Infra |

---

## 2. Arquitectura del Sistema de Temas

### 2.1 Estructura de archivos

```
Alquitel.UI/
  Themes/
    LightTheme.xaml     ← paleta clara (todos los color tokens)
    DarkTheme.xaml      ← paleta oscura (mismos keys, distintos valores)
  App.xaml              ← estilos de controles compartidos, carga LightTheme por defecto
```

`App.xaml` elimina la definición inline de colores actuales y en su lugar hace merge de uno de los dos diccionarios según el tema activo. Los estilos de controles (`Button`, `TextBox`, `DataGrid`, etc.) permanecen en `App.xaml` y referencian los tokens por key.

### 2.2 Cambio de tema en runtime

En `MainViewModel`:
- Propiedad `[ObservableProperty] private bool _isDarkMode`
- Comando `[RelayCommand] private void ToggleTheme()`
- El comando reemplaza el `MergedDictionary` de tema en `Application.Current.Resources`
- La preferencia `IsDarkMode` se persiste en `settings.json` (nueva clave `"IsDarkMode"`)
- Se carga en `LoadSettings()` y se aplica antes de que la ventana sea visible

### 2.3 Paleta de colores

Todos los tokens deben existir en ambos archivos con el mismo `x:Key`.

| Key | Light | Dark |
|---|---|---|
| `BackgroundColor` | `#F3F6F9` | `#0D1117` |
| `SurfaceColor` | `#FFFFFF` | `#161B22` |
| `SurfaceAltColor` | `#F8FAFC` | `#21262D` |
| `TextColor` | `#0D1117` | `#E6EDF3` |
| `MutedTextColor` | `#6B7280` | `#7D8590` |
| `BorderColor` | `#E2E8F0` | `#30363D` |
| `PrimaryColor` | `#1B2E58` | `#1F6FEB` |
| `SecondaryColor` | `#0D84E7` | `#0D84E7` |
| `HoverColor` | `#EBF4FF` | `#1C2A3A` |
| `DataRowHoverColor` | `#F0F7FF` | `#1A2332` |
| `SelectionColor` | `#DBEAFE` | `#163A5F` |

Los brushes (`BackgroundBrush`, `SurfaceBrush`, etc.) se definen en cada archivo referenciando sus colores locales.

---

## 3. Cambios Visuales

### 3.1 Estilos de controles mejorados

**`DarkPillButton`** — agregar triggers de hover y pressed (actualmente ausentes):
```
IsMouseOver=true  → Background oscurece/aclara 15%
IsPressed=true    → Background aún más oscuro, scale 0.97
IsEnabled=false   → Opacity 0.4
```
Implementar con `ControlTemplate.Triggers` usando `ColorAnimation` o reemplazar el `Background` via trigger.

**`TextBox`** — agregar trigger de focus:
```
IsFocused=true → BorderBrush = SecondaryColorBrush, BorderThickness = 1.5
```

**`DataGrid`** — agregar estilos de fila:
```xml
<Style TargetType="DataGridRow">
  hover  → Background = DataRowHoverColor
  selected → Background = SelectionColor, Foreground = TextColor
</Style>
<Style TargetType="DataGridCell">
  focused → sin borde azul por defecto de WPF (override)
</Style>
```

### 3.2 Toggle de tema

Botón en el sidebar, a la derecha del título "Grupo Alquitel":
- Modo claro activo → muestra ícono luna `&#xE708;` (Segoe MDL2)
- Modo oscuro activo → muestra ícono sol `&#xE706;`
- Usa `Style="{StaticResource DarkPillButton}"`, `Height="38"`, `Width="38"`
- `Command="{Binding ToggleThemeCommand}"`

### 3.3 Header del sidebar

Reemplazar el `TextBlock` simple por un `Grid` de dos columnas:
- Col 0 (`*`): TextBlock "Grupo Alquitel" (estilo actual)
- Col 1 (`Auto`): botón toggle de tema

### 3.4 Mejoras menores de layout

- **Sidebar producto cards**: reemplazar el placeholder gris `#E5E7EB` de imagen por un `Border` que use `SurfaceAltColor` para que sea temático
- **Settings panel**: el botón "GUARDAR" se mueve al footer del panel en una fila separada, centrado — actualmente está en la columna OT lo cual es confuso
- **Botonera inferior**: los tres botones de generación reciben íconos Segoe MDL2 (`&#xE8A5;` documento) antes del texto para mejorar legibilidad visual

---

## 4. Tabs Funcionales

### 4.1 Comportamiento

Las tabs "PRESUPUESTO COMERCIAL" y "ORDEN DE TRABAJO" pasan a ser interactivas:

- `[ObservableProperty] private bool _isTechnicalView` en `MainViewModel`
- Tab activa: `Background = SecondaryColorBrush`, `Foreground = White`
- Tab inactiva: `Background = Transparent`, `Foreground = TextBrush`, cursor hand, click cambia `IsTechnicalView`
- Cuando `IsTechnicalView = true`:
  - La columna "PRECIO UNIT." del DataGrid se oculta (`Visibility = Collapsed`)
  - La columna "SUBTOTAL" del DataGrid se oculta
  - El panel "PRESUPUESTO FINAL" se oculta
  - Los botones "GENERAR PRESUPUESTO" y "GENERAR O. FACTURACIÓN" se deshabilitan / ocultan

### 4.2 Implementación

En `MainWindow.xaml`, reemplazar los `Border` estáticos de tabs por dos `Button` con estilos condicionales usando `DataTrigger` bindeando a `IsTechnicalView`.

Las columnas del DataGrid que dependen del modo se controlan así:
- Agregar `InverseBooleanToVisibilityConverter` en `Alquitel.UI/Converters/` (WPF no lo incluye por defecto): devuelve `Collapsed` cuando el bool es `true` y `Visible` cuando es `false`.
- Registrarlo en `Window.Resources` como `x:Key="InverseBoolToVisibilityConverter"`.
- Las columnas "PRECIO UNIT." y "SUBTOTAL" usan `InverseBoolToVisibilityConverter` bindeando a `DataContext.IsTechnicalView`.
- El panel "PRESUPUESTO FINAL" y los botones comerciales usan el mismo converter.

---

## 5. Bug Fixes

### 5.1 `OrderItem.Total` no incluye `Dias`

**Archivo:** `Alquitel.Core/Entities/Order.cs`

```csharp
// ANTES (incorrecto):
public decimal Total => Quantity * UnitPrice;

// DESPUÉS (correcto para empresa de alquiler):
public decimal Total => Quantity * UnitPrice * Dias;
```

`Dias` ya dispara `OnPropertyChanged(nameof(Total))`, por lo que el binding en la UI se actualiza automáticamente. No requiere cambios adicionales en la vista.

### 5.2 Eliminar código de debug en `GenerateDocument`

**Archivo:** `Alquitel.UI/ViewModels/MainViewModel.cs`, método `GenerateDocument`

Eliminar el bloque completo delimitado por el comentario `// DEBUG: Extract literal placeholder text...` hasta el cierre de su `catch`. Este bloque escribe `C:\Alquitel\dump.txt` en cada generación de documento.

---

## 6. Actualización de `.gitignore`

Agregar al `.gitignore` existente:

```gitignore
# Debug outputs generados en runtime
dump.txt

# Node tooling (si existe)
node_modules/
package.json
package-lock.json

# Archivos de trabajo locales
youtube-screenshot.png

# Configuración de usuario (paths locales, no committear)
settings.json
```

---

## 7. Criterios de aceptación

- [ ] El toggle cambia el tema en tiempo real sin reiniciar la app
- [ ] La preferencia de tema persiste entre sesiones (settings.json)
- [ ] Todos los controles (TextBox, Button, DataGrid, Border, DatePicker) respetan el tema activo
- [ ] `FinalBudget` muestra el total correcto incluyendo días (verificar con qty=2, precio=100, días=3 → $600)
- [ ] La pestaña OT oculta columnas monetarias al activarse
- [ ] Los botones tienen feedback visual en hover y pressed
- [ ] No se genera `dump.txt` al generar documentos
- [ ] Los archivos listados en §6 no aparecen en `git status` como untracked

---

## 8. Fuera de alcance

- Cambio de tipografía (se mantiene Segoe UI)
- Cambio de layout general (dos columnas se mantienen)
- Logo de imagen (se mantiene texto)
- Cambio del color de acento (se mantiene `#0D84E7`)
- Persistencia de órdenes en base de datos
