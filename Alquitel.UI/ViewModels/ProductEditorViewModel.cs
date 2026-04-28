using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Infrastructure.Persistence;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;

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
    }

    public partial class ProductEditorViewModel : ObservableObject
    {
        private readonly AlquitelDbContext _dbContext;

        public ObservableCollection<Product> Products { get; } = new();

        [ObservableProperty]
        private Product? _selectedProduct;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private bool _isEditing;

        // ── Editable fields (bound to the form) ─────────────────
        [ObservableProperty] private string _editCategory = "General";
        [ObservableProperty] private decimal _editBasePrice;
        [ObservableProperty] private string? _editImagePath;

        // Segments that compose the product title (serialized to tagged string)
        public ObservableCollection<DescriptionSegmentViewModel> DescriptionSegments { get; } = new();

        public ObservableCollection<CustomFieldViewModel> CustomFields { get; } = new();

        // ── Serialization helpers ────────────────────────────────

        // Maps hex → tag name used by WordDocumentService parser
        private static readonly Dictionary<string, string> _colorToTag = new(StringComparer.OrdinalIgnoreCase)
        {
            ["#FF0000"] = "red",
            ["#006600"] = "green",
            ["#C00000"] = "darkred",
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
            // Strip tags entirely to get plain-text segments between tags,
            // but also track which color/bold/italic each plain span had.
            var result = new List<DescriptionSegmentViewModel>();
            if (string.IsNullOrEmpty(raw))
            {
                result.Add(new DescriptionSegmentViewModel());
                return result;
            }

            // Simple tokenizer
            var tagPattern = new Regex(@"\[(/?)([a-zA-Z]+)\]");
            int pos = 0;
            string color = "#000000";
            bool bold = false, italic = false;
            var stack = new Stack<(string color, bool bold, bool italic)>();

            foreach (Match m in tagPattern.Matches(raw))
            {
                // Text before this tag
                if (m.Index > pos)
                {
                    string span = raw.Substring(pos, m.Index - pos);
                    if (!string.IsNullOrEmpty(span))
                        result.Add(new DescriptionSegmentViewModel { Text = span, ColorHex = color, IsBold = bold, IsItalic = italic });
                }

                bool closing = m.Groups[1].Value == "/";
                string name = m.Groups[2].Value.ToLowerInvariant();

                if (!closing)
                {
                    stack.Push((color, bold, italic));
                    color = name switch
                    {
                        "red"     => "#FF0000",
                        "green"   => "#006600",
                        "darkred" => "#C00000",
                        _ => color
                    };
                    if (name == "b") bold = true;
                    if (name == "i") italic = true;
                }
                else if (stack.Count > 0)
                {
                    var prev = stack.Pop();
                    color = prev.color; bold = prev.bold; italic = prev.italic;
                }

                pos = m.Index + m.Length;
            }

            // Remaining text after last tag
            if (pos < raw.Length)
            {
                string span = raw.Substring(pos);
                if (!string.IsNullOrEmpty(span))
                    result.Add(new DescriptionSegmentViewModel { Text = span, ColorHex = color, IsBold = bold, IsItalic = italic });
            }

            if (result.Count == 0)
                result.Add(new DescriptionSegmentViewModel());

            return result;
        }

        public ProductEditorViewModel(AlquitelDbContext dbContext)
        {
            _dbContext = dbContext;
            LoadProducts();
        }

        private void LoadProducts()
        {
            Products.Clear();
            foreach (var p in _dbContext.Products.OrderBy(p => p.Category).ThenBy(p => p.Description).ToList())
                Products.Add(p);
        }

        partial void OnSelectedProductChanged(Product? value)
        {
            if (value == null)
            {
                IsEditing = false;
                return;
            }
            PopulateForm(value);
            IsEditing = true;
        }

        private void PopulateForm(Product p)
        {
            DescriptionSegments.Clear();
            foreach (var seg in ParseDescriptionSegments(p.Description))
                DescriptionSegments.Add(seg);
            EditCategory = p.Category;
            EditBasePrice = p.BasePrice;
            EditImagePath = p.ImagePath;

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
                catch { } // Ignore JSON parsing errors for bad data
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
        private void SaveProduct()
        {
            if (!DescriptionSegments.Any(s => !string.IsNullOrWhiteSpace(s.Text)))
            {
                StatusMessage = "✗ La descripción no puede estar vacía.";
                return;
            }

            try
            {
                Product target;
                if (SelectedProduct != null)
                {
                    target = SelectedProduct;
                }
                else
                {
                    target = new Product();
                    _dbContext.Products.Add(target);
                }

                ApplyFormToProduct(target);
                _dbContext.SaveChanges();

                LoadProducts();
                SelectedProduct = Products.FirstOrDefault(p => p.Id == target.Id);
                StatusMessage = "✓ Producto guardado correctamente.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Error al guardar: {ex.Message}";
            }
        }

        [RelayCommand]
        private void DeleteProduct()
        {
            if (SelectedProduct == null) return;

            try
            {
                _dbContext.Products.Remove(SelectedProduct);
                _dbContext.SaveChanges();
                SelectedProduct = null;
                IsEditing = false;
                LoadProducts();
                StatusMessage = "✓ Producto eliminado.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Error al eliminar: {ex.Message}";
            }
        }

        [RelayCommand]
        private void CancelEdit()
        {
            if (SelectedProduct != null)
                PopulateForm(SelectedProduct);
            else
                IsEditing = false;
            StatusMessage = string.Empty;
        }

        private void ApplyFormToProduct(Product p)
        {
            p.Description = SerializeDescriptionSegments(DescriptionSegments);
            p.Category = EditCategory.Trim();
            p.BasePrice = EditBasePrice;
            p.ImagePath = EditImagePath;

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
