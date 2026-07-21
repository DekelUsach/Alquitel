using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Infrastructure;
using Alquitel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Data;
using System.ComponentModel;
using Alquitel.Core.Parsing;

namespace Alquitel.UI.ViewModels
{
    // ── Description segment: one colored/styled chunk of the product title ──
    public partial class DescriptionSegmentViewModel : ObservableObject
    {
        [ObservableProperty] private string _text = string.Empty;
        [ObservableProperty] private string _colorHex = "#000000";
        [ObservableProperty] private bool _isBold = false;
        [ObservableProperty] private bool _isItalic = false;

        // Named color entries shown in the ComboBox
        public IReadOnlyList<NamedColor> AvailableColors { get; } = new[]
        {
            new NamedColor("#000000", "Negro"),
            new NamedColor("#FF0000", "Rojo"),
            new NamedColor("#006600", "Verde"),
            new NamedColor("#C00000", "Rojo Oscuro"),
            new NamedColor("#1F68C7", "Azul"),
        };

        // Live preview brush used by the XAML preview TextBlock
        public Brush PreviewBrush => ColorHex == "#000000"
            ? (Brush)new SolidColorBrush(Colors.Black)
            : TryParseBrush(ColorHex) ?? (Brush)new SolidColorBrush(Colors.Black);

        partial void OnColorHexChanged(string value) => OnPropertyChanged(nameof(PreviewBrush));

        private static Brush? TryParseBrush(string hex)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { return null; }
        }
    }

    public record NamedColor(string Hex, string Name);

    public partial class CustomFieldViewModel : ObservableObject
    {
        [ObservableProperty] private string _label = string.Empty;
        [ObservableProperty] private string _value = string.Empty;
        [ObservableProperty] private bool _isBold = false;
        [ObservableProperty] private bool _isUnderline = false;
        [ObservableProperty] private string _colorHex = "#000000";

        public IReadOnlyList<NamedColor> AvailableColors { get; } = new[]
        {
            new NamedColor("#000000", "Negro"),
            new NamedColor("#006600", "Verde"),
            new NamedColor("#E53E3E", "Rojo"),
            new NamedColor("#F68787", "Rojo Suave"),
            new NamedColor("#1F6FEB", "Azul"),
        };

        /// <summary>
        /// Segmentos que la vista previa Word renderiza para este campo, replicando
        /// ProductRenderer: la etiqueta con su negrita/subrayado/color, seguida del
        /// valor parseado con TagParser (soporta tags [red], [b], etc. embebidos).
        /// </summary>
        public IReadOnlyList<TextSegment> PreviewSegments
        {
            get
            {
                string colorHex = string.IsNullOrEmpty(ColorHex) ? "#000000" : ColorHex;
                var segments = new List<TextSegment>();
                if (!string.IsNullOrEmpty(Label))
                {
                    segments.Add(new TextSegment
                    {
                        Text = string.IsNullOrEmpty(Value) ? Label : Label + ": ",
                        ColorHex = colorHex,
                        Bold = IsBold,
                        Underline = IsUnderline
                    });
                }
                if (!string.IsNullOrEmpty(Value))
                    segments.AddRange(TagParser.Parse(Value, colorHex));
                return segments;
            }
        }

        partial void OnLabelChanged(string value) => OnPropertyChanged(nameof(PreviewSegments));
        partial void OnValueChanged(string value) => OnPropertyChanged(nameof(PreviewSegments));
        partial void OnIsBoldChanged(bool value) => OnPropertyChanged(nameof(PreviewSegments));
        partial void OnIsUnderlineChanged(bool value) => OnPropertyChanged(nameof(PreviewSegments));
        partial void OnColorHexChanged(string value) => OnPropertyChanged(nameof(PreviewSegments));
    }

    /// <summary>Celda de un día del calendario de disponibilidad de stock.</summary>
    public sealed record StockDayCell(string DayLabel, string CountLabel, string Tone);

    /// <summary>Fila de orden activa que compromete stock del producto seleccionado.</summary>
    public sealed record CommitmentRow(string BudgetNumber, string ClientName, string RangeLabel, int Quantity);

    public partial class ProductEditorViewModel : ObservableObject, Alquitel.Core.Interfaces.IAsyncInitialization
    {
        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
        private readonly Alquitel.Core.Interfaces.Repositories.IOrderRepository _orderRepository;

        public ObservableCollection<Product> Products { get; } = new();

        [ObservableProperty]
        private Product? _selectedProduct;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private bool _isEditing;

        [ObservableProperty]
        private string _searchText = string.Empty;

        private readonly ICollectionView _productsView;

        // ── Editable fields (bound to the form) ─────────────────
        [ObservableProperty] private string _editCategory = "General";
        [ObservableProperty] private decimal _editBasePrice;
        [ObservableProperty] private string? _editImagePath;

        // §2/§3: stock físico total y costo interno. Vacío = sin control / sin costo.
        [ObservableProperty] private string _editStockQuantity = string.Empty;
        [ObservableProperty] private string _editCost = string.Empty;

        // Stepper del stock: +1 / −1 sobre el texto numérico. Bajar de 0 vuelve a
        // "vacío" (= sin control de stock), el mismo significado que el campo en blanco.
        [RelayCommand]
        private void IncrementStock()
        {
            int current = int.TryParse(EditStockQuantity?.Trim(), out var v) && v >= 0 ? v : 0;
            EditStockQuantity = (current + 1).ToString();
        }

        [RelayCommand]
        private void DecrementStock()
        {
            if (!int.TryParse(EditStockQuantity?.Trim(), out var v) || v <= 0)
            {
                EditStockQuantity = string.Empty;
                return;
            }
            EditStockQuantity = v == 1 ? "0" : (v - 1).ToString();
        }

        // Segments that compose the product title (serialized to tagged string)
        public ObservableCollection<DescriptionSegmentViewModel> DescriptionSegments { get; } = new();

        public ObservableCollection<CustomFieldViewModel> CustomFields { get; } = new();

        // ── Vista previa Word (panel lateral) ────────────────────

        [ObservableProperty] private bool _isPreviewVisible;
        [ObservableProperty] private bool _isZoomedPreviewVisible;

        [RelayCommand]
        private void ToggleWordPreview() => IsPreviewVisible = !IsPreviewVisible;

        [RelayCommand]
        private void OpenZoomedPreview() => IsZoomedPreviewVisible = true;

        [RelayCommand]
        private void CloseZoomedPreview() => IsZoomedPreviewVisible = false;

        /// <summary>
        /// Segmentos del título tal como los renderiza ProductRenderer en Word: el
        /// título completo va siempre en negrita (defaultBold: true al parsear), la
        /// cursiva y el color se respetan por segmento.
        /// </summary>
        public IReadOnlyList<TextSegment> TitlePreviewSegments =>
            DescriptionSegments
                .Where(s => !string.IsNullOrEmpty(s.Text))
                .Select(s => new TextSegment { Text = s.Text, ColorHex = s.ColorHex, Bold = true, Italic = s.IsItalic })
                .ToList();

        // Celdas de la tabla resumen 1×4 con valores de ejemplo (Cant=1, Días=1,
        // precio = Precio Base). Mismo formato que ProductRenderer (es-AR, N0).
        public string PreviewCostCell => $"Costo U.:  {FormatPreviewMoney(EditBasePrice)}";
        public string PreviewTotalCell => $"Total: $   {EditBasePrice.ToString("N0", new System.Globalization.CultureInfo("es-AR"))}";

        private static string FormatPreviewMoney(decimal value) =>
            value == 0 ? "   -----" : value.ToString("N0", new System.Globalization.CultureInfo("es-AR"));

        partial void OnEditBasePriceChanged(decimal value)
        {
            OnPropertyChanged(nameof(PreviewCostCell));
            OnPropertyChanged(nameof(PreviewTotalCell));
        }

        private void NotifyTitlePreviewChanged() => OnPropertyChanged(nameof(TitlePreviewSegments));

        private void OnDescriptionSegmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
            => NotifyTitlePreviewChanged();

        private void OnDescriptionSegmentsCollectionChanged(object? sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (DescriptionSegmentViewModel item in e.OldItems)
                    item.PropertyChanged -= OnDescriptionSegmentPropertyChanged;
            if (e.NewItems != null)
                foreach (DescriptionSegmentViewModel item in e.NewItems)
                    item.PropertyChanged += OnDescriptionSegmentPropertyChanged;
            NotifyTitlePreviewChanged();
        }

        // ── Serialization helpers ────────────────────────────────

        // Maps hex → tag name used by WordDocumentService parser
        // Debe cubrir todos los colores que TagParser.Parse puede devolver: un color
        // sin entrada acá se perdería al re-serializar (round-trip editar → guardar).
        private static readonly Dictionary<string, string> _colorToTag = new(StringComparer.OrdinalIgnoreCase)
        {
            ["#FF0000"] = "red",
            ["#006600"] = "green",
            ["#C00000"] = "darkred",
            ["#1F68C7"] = "blue",
            ["#FFFFFF"] = "white",
        };

        private static string SerializeDescriptionSegments(IEnumerable<DescriptionSegmentViewModel> segs)
        {
            var sb = new StringBuilder();
            foreach (var s in segs)
            {
                string text = s.Text;
                if (string.IsNullOrEmpty(text)) continue;
                if (s.IsItalic)  text = $"[i]{text}[/i]";
                if (s.IsBold)    text = $"[b]{text}[/b]";
                if (_colorToTag.TryGetValue(s.ColorHex, out string? tag))
                    text = $"[{tag}]{text}[/{tag}]";
                sb.Append(text);
            }
            return sb.ToString();
        }

        private static List<DescriptionSegmentViewModel> ParseDescriptionSegments(string raw)
        {
            var parsed = TagParser.Parse(raw, "#000000");
            var result = new List<DescriptionSegmentViewModel>();
            
            foreach (var seg in parsed)
            {
                result.Add(new DescriptionSegmentViewModel 
                { 
                    Text = seg.Text, 
                    ColorHex = seg.ColorHex, 
                    IsBold = seg.Bold, 
                    IsItalic = seg.Italic 
                });
            }

            if (result.Count == 0)
                result.Add(new DescriptionSegmentViewModel());

            return result;
        }

        public ProductEditorViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory,
            Alquitel.Core.Interfaces.Repositories.IOrderRepository orderRepository)
        {
            _dbContextFactory = dbContextFactory;
            _orderRepository = orderRepository;
            _productsView = CollectionViewSource.GetDefaultView(Products);
            _productsView.Filter = FilterProduct;
            DescriptionSegments.CollectionChanged += OnDescriptionSegmentsCollectionChanged;
        }

        // Loading happens here (invoked by NavigationService) instead of the constructor,
        // so the DB hit no longer blocks the UI thread during navigation.
        public async System.Threading.Tasks.Task InitializeAsync() => await LoadProductsAsync();

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private async System.Threading.Tasks.Task RefreshAsync() => await LoadProductsAsync();

        partial void OnSearchTextChanged(string value)
        {
            _productsView.Refresh();
        }

        private bool FilterProduct(object item)
        {
            if (item is not Product product) return false;
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            return product.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || product.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        }

        private async System.Threading.Tasks.Task LoadProductsAsync()
        {
            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var products = await db.Products.AsNoTracking()
                    .OrderBy(p => p.Category).ThenBy(p => p.Description)
                    .ToListAsync();
                Products.Clear();
                foreach (var p in products) Products.Add(p);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "LoadProductsAsync failed");
                StatusMessage = $"✗ Error al cargar productos: {ex.Message}";
            }
        }

        partial void OnSelectedProductChanged(Product? value)
        {
            if (value == null)
            {
                IsEditing = false;
                HasAvailability = false;
                return;
            }
            PopulateForm(value);
            IsEditing = true;
            _ = LoadAvailabilityAsync(value);
        }

        // ── Calendario de disponibilidad de stock (§ próximos 30 días) ──

        /// <summary>Celdas por día: fecha + unidades libres, coloreadas por severidad.</summary>
        public ObservableCollection<StockDayCell> AvailabilityDays { get; } = new();

        /// <summary>Órdenes activas que comprometen stock en la ventana.</summary>
        public ObservableCollection<CommitmentRow> AvailabilityOrders { get; } = new();

        [ObservableProperty]
        private bool _hasAvailability;

        [ObservableProperty]
        private string _availabilitySummary = string.Empty;

        private async System.Threading.Tasks.Task LoadAvailabilityAsync(Product product)
        {
            AvailabilityDays.Clear();
            AvailabilityOrders.Clear();
            HasAvailability = false;

            // Sin control de stock (servicios, ítems sin cantidad) no hay calendario.
            if (product.StockQuantity is not int stock) return;

            var from = DateTime.Today;
            var to = from.AddDays(30);

            List<Alquitel.Core.Interfaces.Repositories.ProductCommitment> commitments;
            try
            {
                commitments = await _orderRepository.GetCommitmentsAsync(product.Id, from, to);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "LoadAvailabilityAsync failed for product {ProductId}", product.Id);
                return;
            }

            // El usuario pudo cambiar de producto durante el await.
            if (SelectedProduct?.Id != product.Id) return;

            var culture = System.Globalization.CultureInfo.GetCultureInfo("es-AR");
            for (int d = 0; d < 30; d++)
            {
                var day = from.AddDays(d);
                int committed = commitments
                    .Where(c => c.Start <= day && day < c.End)
                    .Sum(c => c.Quantity);
                int free = stock - committed;

                string tone = committed == 0 ? "none"
                    : free > 0 ? "ok"
                    : free == 0 ? "tight"
                    : "over";

                AvailabilityDays.Add(new StockDayCell(
                    day.ToString("ddd d/M", culture),
                    free < 0 ? $"−{-free}" : free.ToString(),
                    tone));
            }

            foreach (var group in commitments.GroupBy(c => c.OrderId))
            {
                var first = group.First();
                var endInclusive = first.End.AddDays(-1);
                string range = first.Start == endInclusive
                    ? first.Start.ToString("dd/MM")
                    : $"{first.Start:dd/MM} → {endInclusive:dd/MM}";
                AvailabilityOrders.Add(new CommitmentRow(
                    first.BudgetNumber,
                    string.IsNullOrWhiteSpace(first.ClientName) ? "(sin cliente)" : first.ClientName!,
                    range,
                    group.Sum(c => c.Quantity)));
            }

            AvailabilitySummary = commitments.Count == 0
                ? $"Stock total: {stock} u. Sin compromisos en los próximos 30 días."
                : $"Stock total: {stock} u. — unidades libres por día (próximos 30 días):";
            HasAvailability = true;
        }

        private void PopulateForm(Product p)
        {
            DescriptionSegments.Clear();
            foreach (var seg in ParseDescriptionSegments(p.Description))
                DescriptionSegments.Add(seg);
            EditCategory = p.Category;
            EditBasePrice = p.BasePrice;
            EditImagePath = p.ImagePath;
            EditStockQuantity = p.StockQuantity?.ToString() ?? string.Empty;
            EditCost = p.Cost?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;

            CustomFields.Clear();
            if (!string.IsNullOrWhiteSpace(p.CustomFieldsJson))
            {
                try
                {
                    var fields = JsonSerializer.Deserialize<List<CustomFieldDefinition>>(p.CustomFieldsJson);
                    if (fields != null)
                    {
                        foreach (var f in fields)
                        {
                            CustomFields.Add(new CustomFieldViewModel
                            {
                                Label = f.Label,
                                Value = f.Value,
                                IsBold = f.IsBold,
                                IsUnderline = f.IsUnderline,
                                ColorHex = f.ColorHex
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warning(ex, "Failed to parse CustomFieldsJson for product {ProductId}", p.Id);
                }
            }
        }

        [RelayCommand]
        private void AddDescriptionSegment()
        {
            DescriptionSegments.Add(new DescriptionSegmentViewModel());
        }

        [RelayCommand]
        private void RemoveDescriptionSegment(DescriptionSegmentViewModel seg)
        {
            if (seg != null) DescriptionSegments.Remove(seg);
        }

        [RelayCommand]
        private void NewProduct()
        {
            SelectedProduct = null;
            DescriptionSegments.Clear();
            DescriptionSegments.Add(new DescriptionSegmentViewModel());
            EditCategory = "General";
            EditBasePrice = 0;
            EditImagePath = null;
            EditStockQuantity = string.Empty;
            EditCost = string.Empty;
            CustomFields.Clear();

            IsEditing = true;
            StatusMessage = string.Empty;
        }

        [RelayCommand]
        private void AddCustomField()
        {
            CustomFields.Add(new CustomFieldViewModel());
        }

        [RelayCommand]
        private void RemoveCustomField(CustomFieldViewModel field)
        {
            if (field != null)
                CustomFields.Remove(field);
        }

        [RelayCommand]
        private void SelectImage()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Seleccionar Imagen del Producto",
                Filter = "Archivos de imagen (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png|Todos los archivos (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                EditImagePath = dialog.FileName;
            }
        }

        [RelayCommand]
        private void RemoveImage()
        {
            EditImagePath = null;
        }

        [RelayCommand]
        private async System.Threading.Tasks.Task SaveProductAsync()
        {
            if (!DescriptionSegments.Any(s => !string.IsNullOrWhiteSpace(s.Text)))
            {
                StatusMessage = "✗ La descripción no puede estar vacía.";
                return;
            }

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                Product target;
                if (SelectedProduct != null)
                {
                    target = await db.Products.FindAsync(SelectedProduct.Id) ?? new Product { Id = SelectedProduct.Id };
                    if (db.Entry(target).State == EntityState.Detached) db.Products.Add(target);
                }
                else
                {
                    target = new Product();
                    db.Products.Add(target);
                }

                ApplyFormToProduct(target);
                await db.SaveChangesAsync();

                Guid savedId = target.Id;
                await LoadProductsAsync();
                SelectedProduct = Products.FirstOrDefault(p => p.Id == savedId);
                StatusMessage = "✓ Producto guardado correctamente.";
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "SaveProduct failed");
                StatusMessage = $"✗ Error al guardar: {ex.Message}";
            }
        }

        [RelayCommand]
        private async System.Threading.Tasks.Task DeleteProductAsync()
        {
            if (SelectedProduct == null) return;

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var tracked = await db.Products.FindAsync(SelectedProduct.Id);
                if (tracked != null)
                {
                    // Soft delete: mark as archived instead of physical removal.
                    // Global query filter ensures archived products are hidden from UI.
                    tracked.IsArchived = true;
                    await db.SaveChangesAsync();
                }
                SelectedProduct = null;
                IsEditing = false;
                await LoadProductsAsync();
                StatusMessage = "✓ Producto archivado.";
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "DeleteProduct (archive) failed");
                StatusMessage = $"✗ Error al archivar: {ex.Message}";
            }
        }

        [RelayCommand]
        private void CancelEdit()
        {
            // "Volver al catálogo": descarta cambios sin guardar y cierra el taller.
            // Deseleccionar dispara OnSelectedProductChanged(null) → IsEditing = false.
            SelectedProduct = null;
            IsEditing = false;
            StatusMessage = string.Empty;
        }

        /// <summary>
        /// Crea una copia del producto seleccionado (descripción + " (copia)") y la
        /// guarda directamente. Útil para variantes de un mismo equipo.
        /// </summary>
        [RelayCommand]
        private async System.Threading.Tasks.Task DuplicateProductAsync()
        {
            if (SelectedProduct == null) return;

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var copy = new Product
                {
                    Description = SelectedProduct.Description + " (copia)",
                    Category = SelectedProduct.Category,
                    BasePrice = SelectedProduct.BasePrice,
                    ImagePath = SelectedProduct.ImagePath,
                    CustomFieldsJson = SelectedProduct.CustomFieldsJson
                };
                db.Products.Add(copy);
                await db.SaveChangesAsync();

                await LoadProductsAsync();
                SelectedProduct = Products.FirstOrDefault(p => p.Id == copy.Id);
                StatusMessage = "✓ Producto duplicado. Editá la copia y guardala.";
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "DuplicateProduct failed");
                StatusMessage = $"✗ Error al duplicar: {ex.Message}";
            }
        }

        /// <summary>
        /// Exporta el catálogo completo a CSV (separador ';' + BOM UTF-8 para Excel).
        /// La descripción se exporta sin tags de estilo.
        /// </summary>
        [RelayCommand]
        private void ExportCsv()
        {
            if (Products.Count == 0)
            {
                StatusMessage = "No hay productos para exportar.";
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "Archivo CSV (*.csv)|*.csv",
                FileName = $"Catalogo_Alquitel_{DateTime.Now:yyyyMMdd}.csv"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Descripción;Categoría;Precio Base");
                foreach (var p in Products)
                {
                    var plainDescription = TagParser.StripTags(p.Description);
                    sb.AppendLine(string.Join(";",
                        CsvField(plainDescription), CsvField(p.Category),
                        p.BasePrice.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }

                System.IO.File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
                StatusMessage = $"✓ Se exportaron {Products.Count} productos.";
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "ExportCsv (products) failed");
                StatusMessage = $"✗ Error al exportar: {ex.Message}";
            }
        }

        private static string CsvField(string? value)
        {
            var v = value ?? string.Empty;
            if (v.Contains(';') || v.Contains('"') || v.Contains('\n'))
                v = $"\"{v.Replace("\"", "\"\"")}\"";
            return v;
        }

        private void ApplyFormToProduct(Product p)
        {
            p.Description = SerializeDescriptionSegments(DescriptionSegments);
            p.Category = EditCategory.Trim();
            p.BasePrice = EditBasePrice;
            p.ImagePath = EditImagePath;
            p.StockQuantity = int.TryParse(EditStockQuantity.Trim(), out var stock) && stock >= 0
                ? stock : null;
            p.Cost = decimal.TryParse(EditCost.Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.CurrentCulture, out var cost) && cost >= 0
                ? cost : null;

            var definitions = CustomFields.Select(cf => new CustomFieldDefinition
            {
                Label = cf.Label.Trim(),
                Value = cf.Value.Trim(),
                IsBold = cf.IsBold,
                IsUnderline = cf.IsUnderline,
                ColorHex = cf.ColorHex
            }).ToList();

            p.CustomFieldsJson = JsonSerializer.Serialize(definitions);
        }
    }
}
