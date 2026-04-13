# Redesign, Dark Mode, Tabs Funcionales y Bug Fixes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rediseñar la UI de Alquitel con tema claro/oscuro conmutable, tabs funcionales, corrección del cálculo de `Total` y limpieza de código debug.

**Architecture:** ResourceDictionary dual (`LightTheme.xaml` / `DarkTheme.xaml`) intercambiado en runtime via `Application.Current.Resources.MergedDictionaries`. ViewModel expone `IsDarkMode` y `IsTechnicalView`; la preferencia persiste en `settings.json`. El binding de columnas del DataGrid usa un `BindingProxy` Freezable para sortear la limitación de WPF con `DataGridColumn`.

**Tech Stack:** WPF .NET 8, CommunityToolkit.Mvvm, C# 12, XAML ResourceDictionaries.

> **Nota:** Este proyecto no usa tests automatizados (ver CLAUDE.md). Cada tarea se verifica con `dotnet build` y revisión visual en runtime.

---

## Mapa de archivos

| Acción | Archivo |
|--------|---------|
| Crear | `Alquitel.UI/Themes/LightTheme.xaml` |
| Crear | `Alquitel.UI/Themes/DarkTheme.xaml` |
| Crear | `Alquitel.UI/Converters/InverseBooleanToVisibilityConverter.cs` |
| Crear | `Alquitel.UI/Helpers/BindingProxy.cs` |
| Modificar | `Alquitel.UI/App.xaml` |
| Modificar | `Alquitel.UI/MainWindow.xaml` |
| Modificar | `Alquitel.UI/ViewModels/MainViewModel.cs` |
| Modificar | `Alquitel.UI/Converters/ProductCardConverters.cs` |
| Modificar | `Alquitel.Core/Entities/Order.cs` |
| Modificar | `.gitignore` |

---

## Task 1: Actualizar `.gitignore`

**Files:**
- Modify: `.gitignore`

- [ ] **Step 1: Agregar entradas faltantes al .gitignore**

Abrir `.gitignore` y agregar al final:

```gitignore
# Debug outputs generados en runtime
dump.txt

# Node tooling
node_modules/
package.json
package-lock.json

# Archivos de trabajo locales
youtube-screenshot.png

# Configuración de usuario (paths locales, no committear)
settings.json
```

- [ ] **Step 2: Verificar que los archivos desaparecen del status**

```bash
git status
```

Expected: `dump.txt`, `node_modules/`, `package.json`, `package-lock.json`, `youtube-screenshot.png` ya no aparecen como untracked.

- [ ] **Step 3: Commit**

```bash
git add .gitignore
git commit -m "chore: update gitignore - exclude debug outputs, node files and local config"
```

---

## Task 2: Bug fix — `Total` no incluye `Dias`

**Files:**
- Modify: `Alquitel.Core/Entities/Order.cs:83`

- [ ] **Step 1: Corregir la fórmula de `Total`**

En `Alquitel.Core/Entities/Order.cs`, línea 83, reemplazar:

```csharp
public decimal Total => Quantity * UnitPrice;
```

por:

```csharp
public decimal Total => Quantity * UnitPrice * Dias;
```

- [ ] **Step 2: Verificar build**

```bash
dotnet build Alquitel.Core/Alquitel.Core.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add Alquitel.Core/Entities/Order.cs
git commit -m "fix: include Dias in OrderItem.Total calculation (Quantity * UnitPrice * Dias)"
```

---

## Task 3: Bug fix — Eliminar código debug de producción

**Files:**
- Modify: `Alquitel.UI/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Eliminar el bloque debug ZIP en `GenerateDocument`**

En `MainViewModel.cs`, dentro del método `GenerateDocument`, eliminar el bloque completo que va desde el comentario hasta el cierre del `catch`. Es el bloque que empieza en la línea ~436:

```csharp
// Eliminar todo esto:
// DEBUG: Extract literal placeholder text from actual zip structure for analysis
try {
    using var zip = System.IO.Compression.ZipFile.OpenRead(templatePath);
    var entry = zip.GetEntry("word/document.xml");
    using var stream = new StreamReader(entry.Open());
    string xml = stream.ReadToEnd();
    var matches = System.Text.RegularExpressions.Regex.Matches(xml, @"<w:t[^>]*>(.*?)</w:t>");
    var sb = new System.Text.StringBuilder();
    foreach (System.Text.RegularExpressions.Match m in matches) sb.Append(m.Groups[1].Value);
    File.WriteAllText(@"C:\Alquitel\dump.txt", sb.ToString());
    Log("Se exportó el texto de la plantilla a dump.txt para depuración.");
} catch (Exception zipEx) {
    Log("Error en debug_zip: " + zipEx.Message);
}
```

Dejar el método así (sin ese bloque, los dos `Log` de arriba se mantienen):

```csharp
private async Task GenerateDocument(string targetDir, string templatePath, bool isTechnical)
{
    try
    {
        if (!ValidateOrderForGeneration(out string validationMessage))
        {
            MessageBox.Show(validationMessage, "Datos incompletos", MessageBoxButton.OK, MessageBoxImage.Warning);
            Log("Generación bloqueada por validaciones incompletas.");
            return;
        }

        Log($"Iniciando generación...");
        Log($"Carpeta destino: {targetDir}");
        Log($"Plantilla origen: {templatePath}");

        if (!Directory.Exists(targetDir))
        {
            Log("Creando carpeta destino...");
            Directory.CreateDirectory(targetDir);
        }

        string datePart = CurrentOrder.CreatedDate.ToString("MMdd");
        string empresaPart = string.IsNullOrWhiteSpace(CurrentOrder.Client?.CompanyName) ? "CLIENTE" : CurrentOrder.Client.CompanyName;
        string lugarPart = string.IsNullOrWhiteSpace(CurrentOrder.Location?.Name) ? "LUGAR" : CurrentOrder.Location.Name;
        string inicialesPart = GetInitials(CurrentOrder.AdminName);

        string fileName = $"{CurrentOrder.BudgetNumber}- {datePart}- {empresaPart}- {lugarPart}- {inicialesPart}.docx";
        foreach (char c in Path.GetInvalidFileNameChars()) { fileName = fileName.Replace(c, '_'); }
        string outputPath = Path.Combine(targetDir, fileName);

        Log($"Archivo de salida: {outputPath}");

        if (!File.Exists(templatePath))
        {
            string msg = $"ERROR: La plantilla no existe en: {templatePath}";
            Log(msg);
            MessageBox.Show(msg, "Error de Plantilla", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Log("Llamando al servicio de Word (esto puede tardar unos segundos)...");
        await _documentService.GenerateDocumentAsync(CurrentOrder, templatePath, outputPath, isTechnical);
        
        Log("¡Documento generado con éxito!");
        MessageBox.Show($"Archivo guardado correctamente en:\n{outputPath}", "Éxito", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    catch (Exception ex)
    {
        string errorMsg = $"ERROR CRÍTICO: {ex.Message}";
        Log(errorMsg);
        Log(ex.StackTrace ?? "");
        MessageBox.Show(errorMsg, "Error de Generación", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
```

- [ ] **Step 2: Verificar build**

```bash
dotnet build Alquitel.UI/Alquitel.UI.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add Alquitel.UI/ViewModels/MainViewModel.cs
git commit -m "fix: remove debug ZIP extraction code from GenerateDocument production path"
```

---

## Task 4: Crear archivos de tema — LightTheme.xaml y DarkTheme.xaml

**Files:**
- Create: `Alquitel.UI/Themes/LightTheme.xaml`
- Create: `Alquitel.UI/Themes/DarkTheme.xaml`

- [ ] **Step 1: Crear carpeta `Themes/` y `LightTheme.xaml`**

Crear `Alquitel.UI/Themes/LightTheme.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- Colores -->
    <Color x:Key="BackgroundColor">#F3F6F9</Color>
    <Color x:Key="SurfaceColor">#FFFFFF</Color>
    <Color x:Key="SurfaceAltColor">#F8FAFC</Color>
    <Color x:Key="TextColor">#0D1117</Color>
    <Color x:Key="MutedTextColor">#6B7280</Color>
    <Color x:Key="BorderColor">#E2E8F0</Color>
    <Color x:Key="PrimaryColor">#1B2E58</Color>
    <Color x:Key="SecondaryColor">#0D84E7</Color>
    <Color x:Key="HoverColor">#EBF4FF</Color>
    <Color x:Key="DataRowHoverColor">#F0F7FF</Color>
    <Color x:Key="SelectionColor">#DBEAFE</Color>

    <!-- Brushes -->
    <SolidColorBrush x:Key="BackgroundBrush" Color="{StaticResource BackgroundColor}"/>
    <SolidColorBrush x:Key="SurfaceBrush" Color="{StaticResource SurfaceColor}"/>
    <SolidColorBrush x:Key="SurfaceAltBrush" Color="{StaticResource SurfaceAltColor}"/>
    <SolidColorBrush x:Key="TextBrush" Color="{StaticResource TextColor}"/>
    <SolidColorBrush x:Key="MutedTextBrush" Color="{StaticResource MutedTextColor}"/>
    <SolidColorBrush x:Key="BorderBrush" Color="{StaticResource BorderColor}"/>
    <SolidColorBrush x:Key="PrimaryBrush" Color="{StaticResource PrimaryColor}"/>
    <SolidColorBrush x:Key="SecondaryColorBrush" Color="{StaticResource SecondaryColor}"/>
    <SolidColorBrush x:Key="HoverBrush" Color="{StaticResource HoverColor}"/>
    <SolidColorBrush x:Key="DataRowHoverBrush" Color="{StaticResource DataRowHoverColor}"/>
    <SolidColorBrush x:Key="SelectionBrush" Color="{StaticResource SelectionColor}"/>

</ResourceDictionary>
```

- [ ] **Step 2: Crear `DarkTheme.xaml`**

Crear `Alquitel.UI/Themes/DarkTheme.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- Colores -->
    <Color x:Key="BackgroundColor">#0D1117</Color>
    <Color x:Key="SurfaceColor">#161B22</Color>
    <Color x:Key="SurfaceAltColor">#21262D</Color>
    <Color x:Key="TextColor">#E6EDF3</Color>
    <Color x:Key="MutedTextColor">#7D8590</Color>
    <Color x:Key="BorderColor">#30363D</Color>
    <Color x:Key="PrimaryColor">#1F6FEB</Color>
    <Color x:Key="SecondaryColor">#0D84E7</Color>
    <Color x:Key="HoverColor">#1C2A3A</Color>
    <Color x:Key="DataRowHoverColor">#1A2332</Color>
    <Color x:Key="SelectionColor">#163A5F</Color>

    <!-- Brushes -->
    <SolidColorBrush x:Key="BackgroundBrush" Color="{StaticResource BackgroundColor}"/>
    <SolidColorBrush x:Key="SurfaceBrush" Color="{StaticResource SurfaceColor}"/>
    <SolidColorBrush x:Key="SurfaceAltBrush" Color="{StaticResource SurfaceAltColor}"/>
    <SolidColorBrush x:Key="TextBrush" Color="{StaticResource TextColor}"/>
    <SolidColorBrush x:Key="MutedTextBrush" Color="{StaticResource MutedTextColor}"/>
    <SolidColorBrush x:Key="BorderBrush" Color="{StaticResource BorderColor}"/>
    <SolidColorBrush x:Key="PrimaryBrush" Color="{StaticResource PrimaryColor}"/>
    <SolidColorBrush x:Key="SecondaryColorBrush" Color="{StaticResource SecondaryColor}"/>
    <SolidColorBrush x:Key="HoverBrush" Color="{StaticResource HoverColor}"/>
    <SolidColorBrush x:Key="DataRowHoverBrush" Color="{StaticResource DataRowHoverColor}"/>
    <SolidColorBrush x:Key="SelectionBrush" Color="{StaticResource SelectionColor}"/>

</ResourceDictionary>
```

- [ ] **Step 3: Commit**

```bash
git add Alquitel.UI/Themes/
git commit -m "feat: add LightTheme and DarkTheme ResourceDictionaries"
```

---

## Task 5: Reescribir `App.xaml` — cargar tema y mejorar estilos de controles

**Files:**
- Modify: `Alquitel.UI/App.xaml`

- [ ] **Step 1: Reemplazar `App.xaml` completo**

Reemplazar el contenido de `Alquitel.UI/App.xaml` con:

```xml
<Application x:Class="Alquitel.UI.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <!-- Tema inicial: claro. Se reemplaza en runtime por MainViewModel.ApplyTheme() -->
                <ResourceDictionary Source="Themes/LightTheme.xaml"/>
            </ResourceDictionary.MergedDictionaries>

            <!-- Sombra estándar -->
            <DropShadowEffect x:Key="StandardShadow" Color="#000000" Direction="270"
                              ShadowDepth="2" BlurRadius="10" Opacity="0.06"/>

            <!-- Botón Pill oscuro con hover/pressed -->
            <Style x:Key="DarkPillButton" TargetType="Button">
                <Setter Property="Background" Value="{StaticResource PrimaryBrush}"/>
                <Setter Property="Foreground" Value="White"/>
                <Setter Property="Padding" Value="15,10"/>
                <Setter Property="Margin" Value="5"/>
                <Setter Property="FontSize" Value="13"/>
                <Setter Property="FontWeight" Value="SemiBold"/>
                <Setter Property="BorderThickness" Value="0"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="Button">
                            <Border x:Name="border"
                                    Background="{TemplateBinding Background}"
                                    CornerRadius="20"
                                    Padding="{TemplateBinding Padding}"
                                    RenderTransformOrigin="0.5,0.5">
                                <Border.RenderTransform>
                                    <ScaleTransform x:Name="scale" ScaleX="1" ScaleY="1"/>
                                </Border.RenderTransform>
                                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                            </Border>
                            <ControlTemplate.Triggers>
                                <Trigger Property="IsMouseOver" Value="True">
                                    <Setter TargetName="border" Property="Opacity" Value="0.85"/>
                                </Trigger>
                                <Trigger Property="IsPressed" Value="True">
                                    <Setter TargetName="border" Property="Opacity" Value="0.70"/>
                                    <Setter TargetName="scale" Property="ScaleX" Value="0.97"/>
                                    <Setter TargetName="scale" Property="ScaleY" Value="0.97"/>
                                </Trigger>
                                <Trigger Property="IsEnabled" Value="False">
                                    <Setter Property="Opacity" Value="0.4"/>
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>

            <!-- TextBox Pill con focus highlight -->
            <Style TargetType="TextBox">
                <Setter Property="Padding" Value="15,10"/>
                <Setter Property="FontSize" Value="14"/>
                <Setter Property="Background" Value="{StaticResource SurfaceBrush}"/>
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="BorderBrush" Value="{StaticResource BorderBrush}"/>
                <Setter Property="CaretBrush" Value="{StaticResource TextBrush}"/>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="TextBox">
                            <Border x:Name="border"
                                    Background="{TemplateBinding Background}"
                                    BorderBrush="{TemplateBinding BorderBrush}"
                                    BorderThickness="{TemplateBinding BorderThickness}"
                                    CornerRadius="18"
                                    Effect="{StaticResource StandardShadow}">
                                <ScrollViewer x:Name="PART_ContentHost" Margin="0" VerticalAlignment="Center"/>
                            </Border>
                            <ControlTemplate.Triggers>
                                <Trigger Property="IsFocused" Value="True">
                                    <Setter TargetName="border" Property="BorderBrush"
                                            Value="{StaticResource SecondaryColorBrush}"/>
                                    <Setter TargetName="border" Property="BorderThickness" Value="1.5"/>
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>

            <!-- DatePicker -->
            <Style TargetType="DatePicker">
                <Setter Property="Margin" Value="0"/>
                <Setter Property="Height" Value="42"/>
                <Setter Property="Background" Value="{StaticResource SurfaceBrush}"/>
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="BorderBrush" Value="{StaticResource BorderBrush}"/>
            </Style>

            <!-- DataGrid base -->
            <Style TargetType="DataGrid">
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="BorderBrush" Value="Transparent"/>
                <Setter Property="BorderThickness" Value="0"/>
                <Setter Property="RowBackground" Value="Transparent"/>
                <Setter Property="AlternatingRowBackground" Value="Transparent"/>
                <Setter Property="GridLinesVisibility" Value="Horizontal"/>
                <Setter Property="HorizontalGridLinesBrush" Value="{StaticResource BorderBrush}"/>
                <Setter Property="VerticalGridLinesBrush" Value="Transparent"/>
                <Setter Property="HeadersVisibility" Value="Column"/>
                <Setter Property="RowHeight" Value="50"/>
                <Setter Property="FontSize" Value="14"/>
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="SelectionMode" Value="Single"/>
            </Style>

            <!-- DataGridColumnHeader -->
            <Style TargetType="DataGridColumnHeader">
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="FontWeight" Value="Bold"/>
                <Setter Property="Padding" Value="10,15"/>
                <Setter Property="BorderThickness" Value="0,0,0,1"/>
                <Setter Property="BorderBrush" Value="{StaticResource BorderBrush}"/>
            </Style>

            <!-- DataGridRow con hover y selección temáticos -->
            <Style TargetType="DataGridRow">
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Style.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter Property="Background" Value="{StaticResource DataRowHoverBrush}"/>
                    </Trigger>
                    <Trigger Property="IsSelected" Value="True">
                        <Setter Property="Background" Value="{StaticResource SelectionBrush}"/>
                    </Trigger>
                </Style.Triggers>
            </Style>

            <!-- DataGridCell sin borde de foco por defecto -->
            <Style TargetType="DataGridCell">
                <Setter Property="BorderThickness" Value="0"/>
                <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Style.Triggers>
                    <Trigger Property="IsSelected" Value="True">
                        <Setter Property="Background" Value="Transparent"/>
                        <Setter Property="BorderThickness" Value="0"/>
                        <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                    </Trigger>
                </Style.Triggers>
            </Style>

        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 2: Verificar build**

```bash
dotnet build Alquitel.UI/Alquitel.UI.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add Alquitel.UI/App.xaml
git commit -m "feat: refactor App.xaml - load theme dict, add hover/pressed/focus states to controls"
```

---

## Task 6: Crear `InverseBooleanToVisibilityConverter` y `BindingProxy`

**Files:**
- Create: `Alquitel.UI/Converters/InverseBooleanToVisibilityConverter.cs`
- Create: `Alquitel.UI/Helpers/BindingProxy.cs`

- [ ] **Step 1: Crear `InverseBooleanToVisibilityConverter.cs`**

Crear `Alquitel.UI/Converters/InverseBooleanToVisibilityConverter.cs`:

```csharp
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Alquitel.UI.Converters
{
    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
```

- [ ] **Step 2: Crear `Helpers/BindingProxy.cs`**

Crear `Alquitel.UI/Helpers/BindingProxy.cs`:

```csharp
using System.Windows;

namespace Alquitel.UI.Helpers
{
    /// <summary>
    /// Freezable que actúa como proxy para bindings en contextos sin DataContext
    /// (e.g. DataGridColumn.Visibility).
    /// </summary>
    public class BindingProxy : Freezable
    {
        protected override Freezable CreateInstanceCore() => new BindingProxy();

        public object Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        public static readonly DependencyProperty DataProperty =
            DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy),
                new UIPropertyMetadata(null));
    }
}
```

- [ ] **Step 3: Verificar build**

```bash
dotnet build Alquitel.UI/Alquitel.UI.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add Alquitel.UI/Converters/InverseBooleanToVisibilityConverter.cs Alquitel.UI/Helpers/BindingProxy.cs
git commit -m "feat: add InverseBooleanToVisibilityConverter and BindingProxy helper"
```

---

## Task 7: Agregar `IsDarkMode`, `ToggleThemeCommand` e `IsTechnicalView` a `MainViewModel`

**Files:**
- Modify: `Alquitel.UI/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Agregar propiedades e importar namespace necesario**

Al inicio del archivo, la sección de `using` ya tiene lo necesario. Agregar `using System.Linq;` si no está (ya está). Agregar `using System.Windows;` si no está (ya está).

Dentro de la clase `MainViewModel`, agregar las nuevas propiedades junto a las existentes `[ObservableProperty]`:

```csharp
[ObservableProperty]
private bool _isDarkMode;

[ObservableProperty]
private bool _isTechnicalView;
```

Y agregar la propiedad calculada para visibilidad de columnas comerciales (después de `FinalBudget`):

```csharp
public Visibility CommercialColumnsVisibility =>
    IsTechnicalView ? Visibility.Collapsed : Visibility.Visible;
```

- [ ] **Step 2: Agregar `ApplyTheme` y `ToggleThemeCommand`**

Agregar después de `ToggleSettings()`:

```csharp
[RelayCommand]
private void ToggleTheme()
{
    IsDarkMode = !IsDarkMode;
    ApplyTheme(IsDarkMode);
    SaveSettings();
}

private void ApplyTheme(bool isDark)
{
    var themeFile = isDark ? "DarkTheme.xaml" : "LightTheme.xaml";
    var uri = new Uri($"pack://application:,,,/Themes/{themeFile}");
    var mergedDicts = Application.Current.Resources.MergedDictionaries;

    var toRemove = mergedDicts
        .Where(d => d.Source?.OriginalString.Contains("Theme.xaml") == true)
        .ToList();
    foreach (var d in toRemove) mergedDicts.Remove(d);

    mergedDicts.Add(new ResourceDictionary { Source = uri });
}
```

- [ ] **Step 3: Agregar comandos de tab**

Agregar después de `ToggleTheme`:

```csharp
[RelayCommand]
private void SetCommercialView()
{
    IsTechnicalView = false;
    OnPropertyChanged(nameof(CommercialColumnsVisibility));
}

[RelayCommand]
private void SetTechnicalView()
{
    IsTechnicalView = true;
    OnPropertyChanged(nameof(CommercialColumnsVisibility));
}
```

Y en el partial generado por el source generator, `IsTechnicalView` también debe notificar `CommercialColumnsVisibility`. Agregar el partial override al final de la clase:

```csharp
partial void OnIsTechnicalViewChanged(bool value)
{
    OnPropertyChanged(nameof(CommercialColumnsVisibility));
}
```

- [ ] **Step 4: Actualizar `LoadSettings` para cargar `IsDarkMode`**

En el método `LoadSettings()`, después de la última línea `if (settings.TryGetValue(...)` y antes de `Log(...)`:

```csharp
if (settings.TryGetValue("IsDarkMode", out var dm) && bool.TryParse(dm, out var isDark))
    IsDarkMode = isDark;
```

- [ ] **Step 5: Actualizar `SaveSettings` para guardar `IsDarkMode`**

En el método `SaveSettings()`, agregar al diccionario `settings`:

```csharp
["IsDarkMode"] = IsDarkMode.ToString(),
```

- [ ] **Step 6: Aplicar tema al inicio en el constructor**

En el constructor `MainViewModel(...)`, al final (después de `SelectedItems.CollectionChanged += ...`):

```csharp
ApplyTheme(IsDarkMode);
```

- [ ] **Step 7: Verificar build**

```bash
dotnet build Alquitel.UI/Alquitel.UI.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 8: Commit**

```bash
git add Alquitel.UI/ViewModels/MainViewModel.cs
git commit -m "feat: add IsDarkMode/ToggleThemeCommand and IsTechnicalView/tab commands to MainViewModel"
```

---

## Task 8: Corregir `ProductButtonBackgroundConverter` para usar colores del tema

**Files:**
- Modify: `Alquitel.UI/Converters/ProductCardConverters.cs`

El converter actual hardcodea `#1B2E58` y `#5A9EEA` como colores fijos. En modo oscuro el azul claro no cambia. Reemplazar los campos estáticos por brushes que leen del tema actual.

- [ ] **Step 1: Actualizar `ProductButtonBackgroundConverter`**

Reemplazar la clase `ProductButtonBackgroundConverter` completa:

```csharp
public class ProductButtonBackgroundConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        Brush defaultBrush = Application.Current.TryFindResource("PrimaryBrush") as Brush
            ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B2E58"));
        Brush addedBrush = Application.Current.TryFindResource("SecondaryColorBrush") as Brush
            ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0D84E7"));

        if (values.Length < 2 || values[0] is not Product product || values[1] is not MainViewModel vm)
            return defaultBrush;

        int quantity = vm.GetSelectedQuantity(product.Id);
        return quantity > 0 ? addedBrush : defaultBrush;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

Agregar `using System.Windows;` al inicio si no está (ya está via `System.Windows.Data`).

- [ ] **Step 2: Verificar build**

```bash
dotnet build Alquitel.UI/Alquitel.UI.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add Alquitel.UI/Converters/ProductCardConverters.cs
git commit -m "fix: ProductButtonBackgroundConverter reads theme brushes dynamically instead of hardcoded colors"
```

---

## Task 9: Reescribir `MainWindow.xaml` — toggle, tabs funcionales, columnas y mejoras visuales

**Files:**
- Modify: `Alquitel.UI/MainWindow.xaml`

- [ ] **Step 1: Actualizar `Window.Resources` y agregar namespaces**

En la apertura del elemento `<Window>`, agregar los namespaces:

```xml
xmlns:helpers="clr-namespace:Alquitel.UI.Helpers"
```

(El namespace `conv` ya existe.) Reemplazar `Window.Resources`:

```xml
<Window.Resources>
    <BooleanToVisibilityConverter x:Key="BoolToVisibilityConverter"/>
    <conv:ProductButtonTextConverter x:Key="ProductButtonTextConverter"/>
    <conv:ProductButtonBackgroundConverter x:Key="ProductButtonBackgroundConverter"/>
    <conv:ProductRemoveButtonVisibilityConverter x:Key="ProductRemoveButtonVisibilityConverter"/>
    <conv:InverseBooleanToVisibilityConverter x:Key="InverseBoolToVisibilityConverter"/>
    <!-- Proxy para bindings en DataGridColumn (no tienen DataContext propio) -->
    <helpers:BindingProxy x:Key="Proxy" Data="{Binding}"/>
</Window.Resources>
```

- [ ] **Step 2: Actualizar el header del sidebar — agregar toggle de tema**

Reemplazar el `TextBlock` de título "Grupo Alquitel":

```xml
<!-- ANTES -->
<TextBlock Text="Grupo Alquitel" FontSize="36" FontWeight="Black" FontStyle="Italic"
           Foreground="{StaticResource SecondaryColorBrush}"
           Margin="0,10,0,30" HorizontalAlignment="Center"/>

<!-- DESPUÉS -->
<Grid Margin="0,10,0,30">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="Auto"/>
    </Grid.ColumnDefinitions>
    <TextBlock Grid.Column="0" Text="Grupo Alquitel"
               FontSize="32" FontWeight="Black" FontStyle="Italic"
               Foreground="{StaticResource SecondaryColorBrush}"
               VerticalAlignment="Center" HorizontalAlignment="Center"/>
    <Button Grid.Column="1" Style="{StaticResource DarkPillButton}"
            Height="38" Width="38" Padding="0"
            VerticalAlignment="Center"
            ToolTip="Cambiar tema claro/oscuro"
            Command="{Binding ToggleThemeCommand}">
        <TextBlock FontFamily="Segoe MDL2 Assets" FontSize="16"
                   VerticalAlignment="Center" HorizontalAlignment="Center">
            <TextBlock.Style>
                <Style TargetType="TextBlock">
                    <Setter Property="Text" Value="&#xE708;"/><!-- Luna (modo oscuro disponible) -->
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding IsDarkMode}" Value="True">
                            <Setter Property="Text" Value="&#xE706;"/><!-- Sol (modo claro disponible) -->
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </TextBlock.Style>
        </TextBlock>
    </Button>
</Grid>
```

- [ ] **Step 3: Actualizar placeholder de imagen de producto para usar color del tema**

En el `DataTemplate` de los productos del ListBox, reemplazar el `Border` del placeholder de imagen:

```xml
<!-- ANTES -->
<Border Grid.Row="0" Grid.Column="0" Grid.RowSpan="3" Width="70" Height="70"
        Background="#E5E7EB" CornerRadius="8" Margin="0,0,10,0"/>

<!-- DESPUÉS -->
<Border Grid.Row="0" Grid.Column="0" Grid.RowSpan="3" Width="70" Height="70"
        Background="{StaticResource SurfaceAltBrush}" CornerRadius="8" Margin="0,0,10,0"/>
```

- [ ] **Step 4: Reemplazar tabs estáticos por botones funcionales**

Reemplazar el `StackPanel` con los dos `Border` de tabs:

```xml
<!-- ANTES -->
<StackPanel Grid.Row="0" Orientation="Horizontal" Margin="0,0,0,20">
    <Border Background="{StaticResource SecondaryColorBrush}" CornerRadius="20" Padding="30,12" Margin="0,0,10,0" Effect="{StaticResource StandardShadow}">
        <TextBlock Text="PRESUPUESTO COMERCIAL" Foreground="White" FontWeight="Bold" FontSize="14"/>
    </Border>
    <Border Background="Transparent" CornerRadius="20" Padding="30,12">
        <TextBlock Text="ORDEN DE TRABAJO (TÉCNICA)" Foreground="{StaticResource TextBrush}" FontWeight="SemiBold" FontSize="14"/>
    </Border>
</StackPanel>

<!-- DESPUÉS -->
<StackPanel Grid.Row="0" Orientation="Horizontal" Margin="0,0,0,20">
    <Button Height="44" Padding="24,0" Margin="0,0,10,0"
            Cursor="Hand" BorderThickness="0"
            Command="{Binding SetCommercialViewCommand}"
            Effect="{StaticResource StandardShadow}">
        <Button.Style>
            <Style TargetType="Button">
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="FontWeight" Value="SemiBold"/>
                <Setter Property="FontSize" Value="14"/>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="Button">
                            <Border Background="{TemplateBinding Background}"
                                    CornerRadius="20" Padding="{TemplateBinding Padding}">
                                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                            </Border>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
                <Style.Triggers>
                    <DataTrigger Binding="{Binding IsTechnicalView}" Value="False">
                        <Setter Property="Background" Value="{StaticResource SecondaryColorBrush}"/>
                        <Setter Property="Foreground" Value="White"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </Button.Style>
        <TextBlock Text="PRESUPUESTO COMERCIAL" FontWeight="Bold" FontSize="14"
                   Foreground="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"/>
    </Button>

    <Button Height="44" Padding="24,0"
            Cursor="Hand" BorderThickness="0"
            Command="{Binding SetTechnicalViewCommand}">
        <Button.Style>
            <Style TargetType="Button">
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="FontWeight" Value="SemiBold"/>
                <Setter Property="FontSize" Value="14"/>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="Button">
                            <Border Background="{TemplateBinding Background}"
                                    CornerRadius="20" Padding="{TemplateBinding Padding}">
                                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                            </Border>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
                <Style.Triggers>
                    <DataTrigger Binding="{Binding IsTechnicalView}" Value="True">
                        <Setter Property="Background" Value="{StaticResource SecondaryColorBrush}"/>
                        <Setter Property="Foreground" Value="White"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </Button.Style>
        <TextBlock Text="ORDEN DE TRABAJO (TÉCNICA)" FontWeight="SemiBold" FontSize="14"
                   Foreground="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"/>
    </Button>
</StackPanel>
```

- [ ] **Step 5: Agregar visibilidad a columnas comerciales del DataGrid**

En las columnas "PRECIO UNIT." y "SUBTOTAL" del DataGrid, agregar la propiedad `Visibility`:

```xml
<!-- Columna PRECIO UNIT. -->
<DataGridTextColumn Header="PRECIO UNIT."
                    Binding="{Binding UnitPrice, StringFormat='{}{0:C}'}"
                    Width="140"
                    Visibility="{Binding Data.CommercialColumnsVisibility,
                                         Source={StaticResource Proxy}}">
    ...
</DataGridTextColumn>

<!-- Columna SUBTOTAL -->
<DataGridTextColumn Header="SUBTOTAL"
                    Binding="{Binding Total, Mode=OneWay, StringFormat='{}{0:C}'}"
                    Width="150"
                    IsReadOnly="True"
                    Visibility="{Binding Data.CommercialColumnsVisibility,
                                         Source={StaticResource Proxy}}">
    ...
</DataGridTextColumn>
```

- [ ] **Step 6: Agregar visibilidad al panel PRESUPUESTO FINAL y botones comerciales**

En el `StackPanel` de la botonera inferior (Row 3), agregar visibilidad al panel total:

```xml
<Border ... Visibility="{Binding CommercialColumnsVisibility}">
    <Grid>
        ...
        <TextBlock Grid.Column="0" Text="PRESUPUESTO FINAL:" .../>
        <TextBlock Grid.Column="1" Text="{Binding FinalBudget, ...}" .../>
    </Grid>
</Border>
```

Y agregar visibilidad individual a los botones comerciales:

```xml
<Button Content="GENERAR PRESUPUESTO" ...
        Visibility="{Binding CommercialColumnsVisibility}"
        Command="{Binding GenerateBudgetCommand}"/>

<Button Content="GENERAR O. FACTURACIÓN" ...
        Visibility="{Binding CommercialColumnsVisibility}"
        Command="{Binding GenerateOFCommand}"/>
```

El botón "GENERAR O. TRABAJO" permanece siempre visible.

- [ ] **Step 7: Agregar íconos Segoe MDL2 a los botones de generación**

Reemplazar el `Content` de texto plano por StackPanel con ícono + texto:

```xml
<!-- GENERAR PRESUPUESTO -->
<Button Height="50" Width="220" Style="{StaticResource DarkPillButton}"
        Command="{Binding GenerateBudgetCommand}"
        Visibility="{Binding CommercialColumnsVisibility}">
    <StackPanel Orientation="Horizontal">
        <TextBlock Text="&#xE8A5;" FontFamily="Segoe MDL2 Assets" FontSize="15"
                   VerticalAlignment="Center" Margin="0,0,8,0"/>
        <TextBlock Text="GENERAR PRESUPUESTO" VerticalAlignment="Center"/>
    </StackPanel>
</Button>

<!-- GENERAR O. FACTURACIÓN -->
<Button Height="50" Width="220" Style="{StaticResource DarkPillButton}"
        Margin="15,0" Command="{Binding GenerateOFCommand}"
        Visibility="{Binding CommercialColumnsVisibility}">
    <StackPanel Orientation="Horizontal">
        <TextBlock Text="&#xE8A5;" FontFamily="Segoe MDL2 Assets" FontSize="15"
                   VerticalAlignment="Center" Margin="0,0,8,0"/>
        <TextBlock Text="GENERAR O. FACTURACIÓN" VerticalAlignment="Center"/>
    </StackPanel>
</Button>

<!-- GENERAR O. TRABAJO (siempre visible) -->
<Button Height="50" Width="220" Style="{StaticResource DarkPillButton}"
        Command="{Binding GenerateOTCommand}">
    <StackPanel Orientation="Horizontal">
        <TextBlock Text="&#xE9F5;" FontFamily="Segoe MDL2 Assets" FontSize="15"
                   VerticalAlignment="Center" Margin="0,0,8,0"/>
        <TextBlock Text="GENERAR O. TRABAJO" VerticalAlignment="Center"/>
    </StackPanel>
</Button>
```

- [ ] **Step 8: Mover el botón GUARDAR al footer del panel de configuración**

En el `Border` del panel de configuración de rutas (`Grid.Row="1"`), el botón GUARDAR está actualmente al final de la columna OT (confuso). Mover al final del `Grid` de 3 columnas, en una fila nueva centrada.

En el `Grid` de 3 columnas del settings panel, cambiar `Grid.RowDefinitions` para agregar una fila de footer y mover el botón:

En la columna OT (`Border Grid.Column="4"`), eliminar el botón GUARDAR:
```xml
<!-- Eliminar esta parte de la columna OT: -->
<Button Content="GUARDAR" Height="38" Style="{StaticResource DarkPillButton}"
        HorizontalAlignment="Right" Width="120"
        Command="{Binding SaveSettingsCommand}"/>
```

Cambiar la `Grid` de 3 columnas a que tenga también filas:
```xml
<Grid Grid.Row="1">
    <Grid.RowDefinitions>
        <RowDefinition Height="*"/>
        <RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="16"/>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="16"/>
        <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>

    <!-- Las 3 columnas de cards en Row 0, sin el botón GUARDAR en OT -->
    <!-- ... mismo contenido pero OT sin el Button GUARDAR ... -->

    <!-- Footer con GUARDAR centrado -->
    <Button Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="5"
            Content="GUARDAR CONFIGURACIÓN"
            Height="40" Width="200"
            HorizontalAlignment="Center"
            Margin="0,16,0,0"
            Style="{StaticResource DarkPillButton}"
            Command="{Binding SaveSettingsCommand}"/>
</Grid>
```

- [ ] **Step 9: Verificar build**

```bash
dotnet build Alquitel.UI/Alquitel.UI.csproj
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 10: Commit**

```bash
git add Alquitel.UI/MainWindow.xaml
git commit -m "feat: functional tabs, dark mode toggle, themed columns visibility and button icons in MainWindow"
```

---

## Task 10: Smoke test visual — ejecutar la app y verificar

- [ ] **Step 1: Ejecutar la app**

```bash
dotnet run --project Alquitel.UI/Alquitel.UI.csproj
```

- [ ] **Step 2: Verificar checklist visual**

Recorrer esta lista en la app:

| Check | Qué hacer | Qué esperar |
|---|---|---|
| Toggle tema | Click en botón luna/sol del sidebar | La app cambia a oscuro/claro completamente |
| Hover botones | Pasar el mouse por cualquier botón | El botón se oscurece levemente |
| Press botón | Click y mantener | El botón escala a 0.97 |
| Focus TextBox | Click en cualquier campo de texto | El borde cambia a azul `#0D84E7` |
| Tab OT | Click en "ORDEN DE TRABAJO" | La tab se activa en azul, columnas de precio y botones comerciales desaparecen |
| Tab Comercial | Click en "PRESUPUESTO COMERCIAL" | Todo vuelve a aparecer |
| Hover fila DataGrid | Pasar el mouse sobre una fila | El fondo de la fila cambia a azul tenue |
| Persistencia tema | Cerrar y reabrir la app | El tema elegido se mantiene |
| Cálculo Total | Agregar producto, cambiar Días a 3 | Subtotal = Cantidad × Precio × 3 |

- [ ] **Step 3: Commit final si todo pasó**

```bash
git add -A
git commit -m "feat: complete redesign, dark mode, functional tabs, Total fix and debug cleanup"
```

---

## Resumen de cambios

| Archivo | Tipo | Cambio |
|---|---|---|
| `.gitignore` | Infra | +6 entradas para archivos locales y debug |
| `Alquitel.Core/Entities/Order.cs` | Bug fix | `Total = Qty × Price × Dias` |
| `Alquitel.UI/ViewModels/MainViewModel.cs` | Bug fix + Feature | Eliminado debug code; `IsDarkMode`, `IsTechnicalView`, tema |
| `Alquitel.UI/App.xaml` | Feature | Carga tema dinámico, hover/pressed/focus en controles |
| `Alquitel.UI/Themes/LightTheme.xaml` | Nuevo | Paleta clara |
| `Alquitel.UI/Themes/DarkTheme.xaml` | Nuevo | Paleta oscura |
| `Alquitel.UI/Converters/InverseBooleanToVisibilityConverter.cs` | Nuevo | Converter inverso |
| `Alquitel.UI/Helpers/BindingProxy.cs` | Nuevo | Proxy para DataGridColumn bindings |
| `Alquitel.UI/Converters/ProductCardConverters.cs` | Fix | Colores del botón leen del tema activo |
| `Alquitel.UI/MainWindow.xaml` | Feature | Toggle, tabs, columnas, íconos |
