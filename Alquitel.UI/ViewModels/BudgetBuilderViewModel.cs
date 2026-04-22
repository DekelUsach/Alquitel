using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Infrastructure.Persistence;
using Alquitel.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Windows;
using System.ComponentModel;
using System.Windows.Data;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;
using System.Collections.Specialized;
using System.Collections.Generic;

namespace Alquitel.UI.ViewModels
{
    public partial class BudgetBuilderViewModel : ObservableObject
    {
        private readonly AlquitelDbContext _dbContext;
        private readonly IDocumentService _documentService;
        private readonly ICollectionView _productsView;
        private readonly SettingsViewModel _settingsVm;

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private bool _isSmartSearchVisible;

        [ObservableProperty]
        private string _smartSearchText = string.Empty;

        [ObservableProperty]
        private string _cuitInput = string.Empty;

        [ObservableProperty]
        private int _eventDays = 1;

        [ObservableProperty]
        private Order _currentOrder = new Order { Client = new Client(), Location = new Location() };

        [ObservableProperty]
        private Client? _selectedClient;

        [ObservableProperty]
        private int _selectionVersion;

        [ObservableProperty]
        private bool _isTechnicalView;

        public ObservableCollection<Product> AvailableProducts { get; } = new();
        public ObservableCollection<OrderItem> SelectedItems { get; } = new();
        public decimal FinalBudget => SelectedItems.Sum(i => i.Total);
        public Visibility CommercialColumnsVisibility =>
            IsTechnicalView ? Visibility.Collapsed : Visibility.Visible;

        public BudgetBuilderViewModel(AlquitelDbContext dbContext, IDocumentService documentService, SettingsViewModel settingsVm)
        {
            _dbContext = dbContext;
            _documentService = documentService;
            _settingsVm = settingsVm;

            try {
                foreach(var p in _dbContext.Products.ToList()) AvailableProducts.Add(p);
            } catch { }

            _productsView = CollectionViewSource.GetDefaultView(AvailableProducts);
            _productsView.Filter = FilterProduct;

            SelectedItems.CollectionChanged += OnSelectedItemsCollectionChanged;
        }

        public int GetSelectedQuantity(Guid productId)
        {
            return SelectedItems.Where(i => i.ProductId == productId).Sum(i => i.Quantity);
        }

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

        partial void OnCuitInputChanged(string value)
        {
            var client = _dbContext.Clients.FirstOrDefault(c => c.Cuit == value);
            if (client != null)
            {
                CurrentOrder.Client = client;
                OnPropertyChanged(nameof(CurrentOrder));
            }
        }

        partial void OnEventDaysChanged(int value)
        {
            if (value < 1) { EventDays = 1; return; }
            foreach (var item in SelectedItems) item.Dias = value;
        }

        partial void OnIsTechnicalViewChanged(bool value)
        {
            OnPropertyChanged(nameof(CommercialColumnsVisibility));
        }

        [RelayCommand]
        private void SetCommercialView() => IsTechnicalView = false;

        [RelayCommand]
        private void SetTechnicalView() => IsTechnicalView = true;

        [RelayCommand]
        private void AddProduct(Product product)
        {
            var existingItem = SelectedItems.FirstOrDefault(i => i.ProductId == product.Id);
            if (existingItem != null)
            {
                existingItem.Quantity += 1;
                existingItem.Dias = EventDays;
                return;
            }
            var item = new OrderItem
            {
                ProductId = product.Id,
                Product = product,
                Quantity = 1,
                Dias = EventDays,
                UnitPrice = product.BasePrice,
                // Copy dynamic fields from Product
                ImagePath = product.ImagePath,
                CustomFieldsJson = product.CustomFieldsJson,
                RequestedMeasure = string.Empty, // Starts off empty, user fills it in for the budget
            };
            SelectedItems.Add(item);
            CurrentOrder.Items.Add(item);
        }

        [RelayCommand]
        private void RemoveItem(OrderItem item)
        {
            SelectedItems.Remove(item);
            CurrentOrder.Items.Remove(item);
        }

        [RelayCommand]
        private void RemoveProduct(Product product)
        {
            var existingItem = SelectedItems.FirstOrDefault(i => i.ProductId == product.Id);
            if (existingItem == null) return;
            if (existingItem.Quantity > 1) { existingItem.Quantity -= 1; return; }
            SelectedItems.Remove(existingItem);
            CurrentOrder.Items.Remove(existingItem);
        }

        [RelayCommand]
        private void ToggleSmartSearchInput() => IsSmartSearchVisible = !IsSmartSearchVisible;

        [RelayCommand]
        private void ParseSmartSearch()
        {
            if (string.IsNullOrWhiteSpace(SmartSearchText))
            {
                MessageBox.Show("Pegá un texto para analizar primero.", "Búsqueda inteligente", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var matches = FindProductsFromParagraph(SmartSearchText).ToList();
            if (!matches.Any())
            {
                MessageBox.Show("No detecté productos del catálogo en ese texto.", "Búsqueda inteligente", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (SelectedItems.Any())
            {
                var replace = MessageBox.Show(
                    "Ya hay productos en el pedido. ¿Querés reemplazarlos con lo detectado en el texto?",
                    "Confirmar reemplazo", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (replace == MessageBoxResult.Yes) { SelectedItems.Clear(); CurrentOrder.Items.Clear(); }
            }

            int addedCount = 0;
            foreach (var result in matches.OrderByDescending(m => m.Score))
            {
                if (SelectedItems.Any(i => i.ProductId == result.Product.Id)) continue;
                var item = new OrderItem
                {
                    ProductId = result.Product.Id,
                    Product = result.Product,
                    Quantity = result.Quantity,
                    Dias = EventDays,
                    UnitPrice = result.Product.BasePrice,
                    ImagePath = result.Product.ImagePath,
                    CustomFieldsJson = result.Product.CustomFieldsJson,
                    RequestedMeasure = string.Empty
                };
                SelectedItems.Add(item);
                CurrentOrder.Items.Add(item);
                addedCount++;
            }
            MessageBox.Show($"Se agregaron {addedCount} producto(s) automáticamente.", "Búsqueda inteligente", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        [RelayCommand]
        private async Task GenerateBudget()
        {
            var paths = _settingsVm.GetCurrentPaths();
            await GenerateDocument(paths["PresupuestosFolder"], paths["PresupuestosTemplate"], false);
        }

        [RelayCommand]
        private async Task GenerateOF()
        {
            var paths = _settingsVm.GetCurrentPaths();
            await GenerateDocument(paths["OfFolder"], paths["OfTemplate"], false);
        }

        [RelayCommand]
        private async Task GenerateOT()
        {
            var paths = _settingsVm.GetCurrentPaths();
            await GenerateDocument(paths["OtFolder"], paths["OtTemplate"], true);
        }

        private async Task GenerateDocument(string targetDir, string templatePath, bool isTechnical)
        {
            try
            {
                if (!ValidateOrderForGeneration(out string validationMessage))
                {
                    MessageBox.Show(validationMessage, "Datos incompletos", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                string datePart = CurrentOrder.CreatedDate.ToString("MMdd");
                string empresaPart = string.IsNullOrWhiteSpace(CurrentOrder.Client?.CompanyName) ? "CLIENTE" : CurrentOrder.Client.CompanyName;
                string lugarPart = string.IsNullOrWhiteSpace(CurrentOrder.Location?.Name) ? "LUGAR" : CurrentOrder.Location.Name;
                string inicialesPart = GetInitials(CurrentOrder.AdminName);
                string fileName = $"{CurrentOrder.BudgetNumber}- {datePart}- {empresaPart}- {lugarPart}- {inicialesPart}.docx";
                foreach (char c in Path.GetInvalidFileNameChars()) { fileName = fileName.Replace(c, '_'); }
                string outputPath = Path.Combine(targetDir, fileName);

                if (!File.Exists(templatePath))
                {
                    MessageBox.Show($"La plantilla no existe en: {templatePath}", "Error de Plantilla", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                await _documentService.GenerateDocumentAsync(CurrentOrder, templatePath, outputPath, isTechnical);
                await PersistOrderAsync();
                MessageBox.Show($"Archivo guardado correctamente en:\n{outputPath}", "Éxito", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error de Generación", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void LoadOrder(Order order)
        {
            var full = _dbContext.Orders
                .Include(o => o.Client)
                .Include(o => o.Location)
                .Include(o => o.Items).ThenInclude(i => i.Product)
                .FirstOrDefault(o => o.Id == order.Id);

            if (full == null) return;

            CurrentOrder = new Order
            {
                Id = full.Id,
                BudgetNumber = full.BudgetNumber,
                AdminName = full.AdminName,
                CreatedDate = full.CreatedDate,
                EventDate = full.EventDate,
                Status = full.Status,
                Client = full.Client ?? new Client(),
                Location = full.Location ?? new Location(),
            };

            SelectedItems.Clear();
            CurrentOrder.Items.Clear();

            foreach (var item in full.Items)
            {
                var oi = new OrderItem
                {
                    Id = item.Id,
                    OrderId = item.OrderId,
                    ProductId = item.ProductId,
                    Product = item.Product,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    Dias = item.Dias,
                    TechnicalNotes = item.TechnicalNotes,
                    ImagePath = item.ImagePath,
                    CustomFieldsJson = item.CustomFieldsJson,
                    RequestedMeasure = item.RequestedMeasure,
                };
                SelectedItems.Add(oi);
                CurrentOrder.Items.Add(oi);
            }

            EventDays = full.Items.FirstOrDefault()?.Dias ?? 1;
            CuitInput = full.Client?.Cuit ?? string.Empty;
        }

        private async Task PersistOrderAsync()
        {
            try
            {
                var locName = CurrentOrder.Location?.Name ?? string.Empty;
                var location = _dbContext.Locations.FirstOrDefault(l => l.Name == locName);
                if (location == null && !string.IsNullOrWhiteSpace(locName))
                {
                    location = new Location { Name = locName };
                    _dbContext.Locations.Add(location);
                    await _dbContext.SaveChangesAsync();
                }
                else if (location == null)
                {
                    location = new Location { Id = Guid.Empty, Name = string.Empty };
                }

                var clientId = CurrentOrder.Client?.Id ?? Guid.Empty;
                var locationId = location.Id;

                var trackedOrder = _dbContext.ChangeTracker.Entries<Order>()
                    .FirstOrDefault(e => e.Entity.Id == CurrentOrder.Id);
                if (trackedOrder != null) trackedOrder.State = EntityState.Detached;

                var orderExists = _dbContext.Orders.Any(o => o.Id == CurrentOrder.Id);

                if (!orderExists)
                {
                    var orderToSave = new Order
                    {
                        Id = CurrentOrder.Id,
                        BudgetNumber = CurrentOrder.BudgetNumber,
                        AdminName = CurrentOrder.AdminName,
                        ClientId = clientId,
                        LocationId = locationId,
                        CreatedDate = CurrentOrder.CreatedDate,
                        EventDate = CurrentOrder.EventDate,
                        Status = CurrentOrder.Status,
                    };
                    _dbContext.Orders.Add(orderToSave);
                    await _dbContext.SaveChangesAsync();

                    foreach (var item in CurrentOrder.Items)
                    {
                        _dbContext.OrderItems.Add(new OrderItem
                        {
                            Id = item.Id,
                            OrderId = orderToSave.Id,
                            ProductId = item.ProductId,
                            Quantity = item.Quantity,
                            UnitPrice = item.UnitPrice,
                            Dias = item.Dias,
                            TechnicalNotes = item.TechnicalNotes,
                            ImagePath = item.ImagePath,
                            CustomFieldsJson = item.CustomFieldsJson,
                            RequestedMeasure = item.RequestedMeasure,
                        });
                    }
                    await _dbContext.SaveChangesAsync();
                }
                else
                {
                    var tracked = _dbContext.Orders.Find(CurrentOrder.Id);
                    if (tracked != null)
                    {
                        tracked.BudgetNumber = CurrentOrder.BudgetNumber;
                        tracked.AdminName = CurrentOrder.AdminName;
                        tracked.ClientId = clientId;
                        tracked.LocationId = locationId;
                        tracked.EventDate = CurrentOrder.EventDate;
                        tracked.Status = CurrentOrder.Status;
                    }

                    var oldItems = _dbContext.OrderItems.Where(i => i.OrderId == CurrentOrder.Id).ToList();
                    _dbContext.OrderItems.RemoveRange(oldItems);
                    await _dbContext.SaveChangesAsync();

                    foreach (var item in CurrentOrder.Items)
                    {
                        _dbContext.OrderItems.Add(new OrderItem
                        {
                            Id = Guid.NewGuid(),
                            OrderId = CurrentOrder.Id,
                            ProductId = item.ProductId,
                            Quantity = item.Quantity,
                            UnitPrice = item.UnitPrice,
                            Dias = item.Dias,
                            TechnicalNotes = item.TechnicalNotes,
                            ImagePath = item.ImagePath,
                            CustomFieldsJson = item.CustomFieldsJson,
                            RequestedMeasure = item.RequestedMeasure,
                        });
                    }
                    await _dbContext.SaveChangesAsync();
                }
            }
            catch
            {
                // Persistence is non-critical — document was already generated
            }
        }

        private string GetInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "NA";
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return name.Substring(0, Math.Min(2, name.Length)).ToUpper();
            return string.Join("", parts.Select(p => p[0])).ToUpper();
        }

        private bool ValidateOrderForGeneration(out string message)
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(CurrentOrder.Client?.CompanyName)) errors.Add("Cliente: completá Empresa / Cliente.");
            if (string.IsNullOrWhiteSpace(CurrentOrder.BudgetNumber)) errors.Add("N° Presupuesto: ingresá un número.");
            if (!CurrentOrder.EventDate.HasValue) errors.Add("Fecha del evento: seleccioná una fecha.");
            if (EventDays < 1) errors.Add("Días: debe ser mayor o igual a 1.");
            if (!SelectedItems.Any()) errors.Add("Productos: agregá al menos un producto.");
            if (!errors.Any()) { message = string.Empty; return true; }
            message = "No se puede generar el documento. Revisá estos campos:\n\n- " + string.Join("\n- ", errors);
            return false;
        }

        private void OnSelectedItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (OrderItem item in e.OldItems) item.PropertyChanged -= OnSelectedItemPropertyChanged;
            if (e.NewItems != null) foreach (OrderItem item in e.NewItems) item.PropertyChanged += OnSelectedItemPropertyChanged;
            SelectionVersion++;
            OnPropertyChanged(nameof(FinalBudget));
        }

        private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(OrderItem.Quantity) or nameof(OrderItem.Dias) or nameof(OrderItem.UnitPrice) or nameof(OrderItem.Total))
            {
                SelectionVersion++;
                OnPropertyChanged(nameof(FinalBudget));
            }
        }

        #region Smart Search Engine

        private IEnumerable<SmartMatchResult> FindProductsFromParagraph(string paragraph)
        {
            var segments = BuildSmartSegments(paragraph);
            var aggregated = new Dictionary<Guid, SmartMatchResult>();
            foreach (var segment in segments)
            {
                int quantity = ExtractQuantityFromSegment(segment);
                var ranked = AvailableProducts
                    .Select(product => new SmartMatchResult(product, quantity, ScoreProductAgainstSegment(segment, product)))
                    .OrderByDescending(x => x.Score).ToList();
                if (!ranked.Any()) continue;
                var best = ranked[0];
                var second = ranked.Count > 1 ? ranked[1] : null;
                if (best.Score < 4.0) continue;
                if (second != null && Math.Abs(best.Score - second.Score) < 0.35) continue;
                if (aggregated.TryGetValue(best.Product.Id, out var existing))
                    aggregated[best.Product.Id] = existing with { Quantity = existing.Quantity + best.Quantity, Score = Math.Max(existing.Score, best.Score) };
                else
                    aggregated[best.Product.Id] = best;
            }
            return aggregated.Values;
        }

        private static List<string> BuildSmartSegments(string paragraph)
        {
            var primarySegments = paragraph.Split(new[] { '.', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s));
            var expanded = new List<string>();
            foreach (var segment in primarySegments)
            {
                expanded.AddRange(Regex.Split(segment, @"\s+y\s+", RegexOptions.IgnoreCase).Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            return expanded;
        }

        private static int ExtractQuantityFromSegment(string segment)
        {
            string raw = RemoveDiacritics(segment).ToLowerInvariant();
            var patterns = new[]
            {
                @"\b(\d{1,3})(?![\.,]\d)\s*(x|u|ud|uds|unidad|unidades)\b",
                @"\b(?:x|por)\s*(\d{1,3})(?![\.,]\d)\b",
                @"\b(\d{1,3})(?![\.,]\d)\s*(?:pantalla|pantallas|notebook|notebooks|camara|camaras|servicio|servicios|traslado|traslados|touch|equipo|equipos)\b"
            };
            foreach (var pattern in patterns)
            {
                var m = Regex.Match(raw, pattern, RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int q) && q > 0) return q;
            }
            return 1;
        }

        private static double ScoreProductAgainstSegment(string segment, Product product)
        {
            string ns = NormalizeText(segment);
            string nd = NormalizeText(product.Description);
            string nc = NormalizeText(product.Category);
            var st = ExtractMeaningfulTokens(ns).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pt = ExtractMeaningfulTokens(nd).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ct = ExtractMeaningfulTokens(nc).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!st.Any() || !pt.Any()) return 0;
            int overlap = st.Intersect(pt, StringComparer.OrdinalIgnoreCase).Count();
            int catOverlap = st.Intersect(ct, StringComparer.OrdinalIgnoreCase).Count();
            double coverage = (double)overlap / pt.Count;
            double precision = (double)overlap / Math.Max(1, st.Count);
            double tri = DiceCoefficient(Trigrams(ns), Trigrams(nd));
            double score = overlap * 2.7 + catOverlap * 0.8 + coverage * 3.5 + precision * 1.5 + tri * 4.0;
            if (ns.Contains(nd, StringComparison.OrdinalIgnoreCase)) score += 3.0;
            return score;
        }

        private static IEnumerable<string> ExtractMeaningfulTokens(string text)
        {
            var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "de", "la", "el", "los", "las", "con", "para", "por", "del", "y", "plus", "pro", "edition", "business", "servicio" };
            return Regex.Split(text, @"[^a-z0-9]+").Where(t => t.Length >= 3 && !stop.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string formD = RemoveDiacritics(text);
            var sb = new StringBuilder(formD.Length);
            foreach (char c in formD) sb.Append(char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ? c : ' ');
            return Regex.Replace(sb.ToString().ToLowerInvariant(), @"\s+", " ").Trim();
        }

        private static HashSet<string> Trigrams(string input)
        {
            string text = $"  {input}  ";
            var grams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (text.Length < 3) { grams.Add(text); return grams; }
            for (int i = 0; i <= text.Length - 3; i++) grams.Add(text.Substring(i, 3));
            return grams;
        }

        private static double DiceCoefficient(HashSet<string> a, HashSet<string> b)
        {
            if (!a.Any() || !b.Any()) return 0;
            int intersection = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
            return (2.0 * intersection) / (a.Count + b.Count);
        }

        private static string RemoveDiacritics(string text)
        {
            string normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private sealed record SmartMatchResult(Product Product, int Quantity, double Score);

        #endregion
    }
}
