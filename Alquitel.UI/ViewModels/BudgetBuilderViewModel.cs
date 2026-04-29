using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Infrastructure;
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
using Alquitel.Core.Helpers;

namespace Alquitel.UI.ViewModels
{
    public partial class BudgetBuilderViewModel : ObservableObject, IAsyncInitialization, IDisposable
    {
        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
        private readonly IDocumentService _documentService;
        private readonly ICollectionView _productsView;
        private readonly IAppSettings _appSettings;
        private readonly IDialogService _dialogService;

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

        private Dictionary<Guid, ProductCacheEntry> _productSearchCache = new();
        private CancellationTokenSource? _autosaveCts;
        private string _draftsFolder;

        private class ProductCacheEntry
        {
            public HashSet<string> DescriptionTokens { get; set; } = new();
            public HashSet<string> CategoryTokens { get; set; } = new();
            public HashSet<string> DescriptionTrigrams { get; set; } = new();
        }

        public BudgetBuilderViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory, IDocumentService documentService, IAppSettings appSettings, IDialogService dialogService)
        {
            _dbContextFactory = dbContextFactory;
            _documentService = documentService;
            _appSettings = appSettings;
            _dialogService = dialogService;

            _productsView = CollectionViewSource.GetDefaultView(AvailableProducts);
            _productsView.Filter = FilterProduct;

            SelectedItems.CollectionChanged += OnSelectedItemsCollectionChanged;

            _draftsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alquitel", "Drafts");
            if (!Directory.Exists(_draftsFolder))
            {
                Directory.CreateDirectory(_draftsFolder);
            }
        }

        public async Task InitializeAsync()
        {
            AvailableProducts.Clear();
            _productSearchCache.Clear();

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var products = await db.Products.AsNoTracking().ToListAsync();

                var stopWords = new HashSet<string>(_appSettings.SmartSearchStopWords, StringComparer.OrdinalIgnoreCase);

                foreach (var p in products)
                {
                    AvailableProducts.Add(p);
                    _productSearchCache[p.Id] = new ProductCacheEntry
                    {
                        DescriptionTokens = ExtractMeaningfulTokens(NormalizeText(p.Description), stopWords).ToHashSet(StringComparer.OrdinalIgnoreCase),
                        CategoryTokens = ExtractMeaningfulTokens(NormalizeText(p.Category), stopWords).ToHashSet(StringComparer.OrdinalIgnoreCase),
                        DescriptionTrigrams = Trigrams(NormalizeText(p.Description))
                    };
                }
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Failed to load products in BudgetBuilder");
            }

            _autosaveCts?.Cancel();
            _autosaveCts = new CancellationTokenSource();
            _ = AutosaveLoopAsync(_autosaveCts.Token);
        }

        private async Task AutosaveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                    if (SelectedItems.Any() && CurrentOrder != null)
                    {
                        var draftName = CurrentOrder.Id == Guid.Empty ? "new_draft.json" : $"draft_{CurrentOrder.Id}.json";
                        var path = Path.Combine(_draftsFolder, draftName);
                        // Convert to DTO to avoid circular references during serialization
                        var dto = new
                        {
                            BudgetNumber = CurrentOrder.BudgetNumber,
                            ClientName = CurrentOrder.Client?.CompanyName,
                            EventDate = CurrentOrder.EventDate,
                            Items = SelectedItems.Select(i => new { i.ProductId, i.Quantity, i.Total, i.RequestedMeasure }).ToList()
                        };
                        var json = System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        await File.WriteAllTextAsync(path, json, token);
                    }
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    AppLog.Warning(ex, "Autosave failed");
                }
            }
        }

        public void Dispose()
        {
            _autosaveCts?.Cancel();
            _autosaveCts?.Dispose();
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
            if (!string.IsNullOrWhiteSpace(value) && !CuitValidator.IsValid(value))
            {
                // We could show a visual cue, but for now we just log or ignore
                // We can't block typing, but we can prevent finding a client if invalid
            }

            try
            {
                using var db = _dbContextFactory.CreateDbContext();
                var client = db.Clients.AsNoTracking().FirstOrDefault(c => c.Cuit == value);
                if (client != null)
                {
                    CurrentOrder.Client = client;
                    OnPropertyChanged(nameof(CurrentOrder));
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "CUIT lookup failed for {Cuit}", value);
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
                DescriptionSnapshot = product.Description,
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
                _dialogService.ShowInfo("Búsqueda inteligente", "Pegá un texto para analizar primero.");
                return;
            }

            var matches = FindProductsFromParagraph(SmartSearchText).ToList();
            if (!matches.Any())
            {
                _dialogService.ShowWarning("Búsqueda inteligente", "No detecté productos del catálogo en ese texto.");
                return;
            }

            if (SelectedItems.Any())
            {
                var replace = _dialogService.ShowConfirm(
                    "Confirmar reemplazo",
                    "Ya hay productos en el pedido. ¿Querés reemplazarlos con lo detectado en el texto?");
                if (replace) { SelectedItems.Clear(); CurrentOrder.Items.Clear(); }
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
                    DescriptionSnapshot = result.Product.Description,
                    RequestedMeasure = string.Empty
                };
                SelectedItems.Add(item);
                CurrentOrder.Items.Add(item);
                addedCount++;
            }
            _dialogService.ShowInfo("Búsqueda inteligente", $"Se agregaron {addedCount} producto(s) automáticamente.");
        }

        [RelayCommand]
        private async Task GenerateBudget()
        {
            await GenerateDocument(_appSettings.PresupuestosFolder, _appSettings.PresupuestosTemplate, false);
        }

        [RelayCommand]
        private async Task GenerateOF()
        {
            await GenerateDocument(_appSettings.OfFolder, _appSettings.OfTemplate, false);
        }

        [RelayCommand]
        private async Task GenerateOT()
        {
            await GenerateDocument(_appSettings.OtFolder, _appSettings.OtTemplate, true);
        }

        private async Task GenerateDocument(string targetDir, string templatePath, bool isTechnical)
        {
            try
            {
                if (!ValidateOrderForGeneration(out string validationMessage))
                {
                    _dialogService.ShowWarning("Datos incompletos", validationMessage);
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
                    _dialogService.ShowError("Error de Plantilla", $"La plantilla no existe en: {templatePath}");
                    return;
                }

                await _documentService.GenerateDocumentAsync(CurrentOrder, templatePath, outputPath, isTechnical, _appSettings.ExportPdf);
                bool persisted = await PersistOrderAsync();

                if (persisted)
                {
                    _dialogService.ShowInfo("Éxito", $"Archivo guardado correctamente en:\n{outputPath}");
                }
                else
                {
                    _dialogService.ShowWarning(
                        "Documento generado, persistencia falló",
                        $"El documento se generó correctamente:\n{outputPath}\n\n" +
                        "ATENCIÓN: la orden no pudo guardarse en la base de datos. " +
                        "Revisá el archivo de log para más detalles.");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "GenerateDocument failed (template={Template}, target={Target})", templatePath, targetDir);
                _dialogService.ShowError("Error de Generación", $"Error: {ex.Message}");
            }
        }

        public void LoadOrder(Order order)
        {
            using var db = _dbContextFactory.CreateDbContext();
            var full = db.Orders
                .AsNoTracking()
                .IgnoreQueryFilters() // Include archived clients/products in historical orders
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
                    DescriptionSnapshot = item.DescriptionSnapshot,
                    RequestedMeasure = item.RequestedMeasure,
                };
                SelectedItems.Add(oi);
                CurrentOrder.Items.Add(oi);
            }

            EventDays = full.Items.FirstOrDefault()?.Dias ?? 1;
            CuitInput = full.Client?.Cuit ?? string.Empty;
        }

        private async Task<bool> PersistOrderAsync()
        {
            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();

                var locName = CurrentOrder.Location?.Name ?? string.Empty;
                var location = db.Locations.FirstOrDefault(l => l.Name == locName);
                if (location == null && !string.IsNullOrWhiteSpace(locName))
                {
                    location = new Location { Name = locName };
                    db.Locations.Add(location);
                    await db.SaveChangesAsync();
                }
                else if (location == null)
                {
                    location = new Location { Id = Guid.Empty, Name = string.Empty };
                }

                var clientId = CurrentOrder.Client?.Id ?? Guid.Empty;
                var locationId = location.Id;

                var orderExists = db.Orders.Any(o => o.Id == CurrentOrder.Id);

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
                    db.Orders.Add(orderToSave);
                    await db.SaveChangesAsync();

                    foreach (var item in CurrentOrder.Items)
                    {
                        db.OrderItems.Add(new OrderItem
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
                            DescriptionSnapshot = item.DescriptionSnapshot,
                            RequestedMeasure = item.RequestedMeasure,
                        });
                    }
                    await db.SaveChangesAsync();
                }
                else
                {
                    var tracked = db.Orders.Find(CurrentOrder.Id);
                    if (tracked != null)
                    {
                        tracked.BudgetNumber = CurrentOrder.BudgetNumber;
                        tracked.AdminName = CurrentOrder.AdminName;
                        tracked.ClientId = clientId;
                        tracked.LocationId = locationId;
                        tracked.EventDate = CurrentOrder.EventDate;
                        tracked.Status = CurrentOrder.Status;
                    }

                    var oldItems = db.OrderItems.Where(i => i.OrderId == CurrentOrder.Id).ToList();
                    db.OrderItems.RemoveRange(oldItems);
                    await db.SaveChangesAsync();

                    foreach (var item in CurrentOrder.Items)
                    {
                        db.OrderItems.Add(new OrderItem
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
                            DescriptionSnapshot = item.DescriptionSnapshot,
                            RequestedMeasure = item.RequestedMeasure,
                        });
                    }
                    await db.SaveChangesAsync();
                }

                AppLog.Information("Order persisted: {OrderId} ({Budget})", CurrentOrder.Id, CurrentOrder.BudgetNumber);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "PersistOrderAsync failed for order {OrderId}", CurrentOrder.Id);
                return false;
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
            
            var cuit = CurrentOrder.Client?.Cuit ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(cuit) && !CuitValidator.IsValid(cuit))
            {
                errors.Add("CUIT: el número ingresado no es válido (falló verificación AFIP).");
            }
            
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
            var threshold = _appSettings.SmartSearchThreshold;

            foreach (var segment in segments)
            {
                int quantity = ExtractQuantityFromSegment(segment);
                var ranked = AvailableProducts
                    .Select(product => new SmartMatchResult(product, quantity, ScoreProductAgainstSegment(segment, product)))
                    .OrderByDescending(x => x.Score).ToList();
                if (!ranked.Any()) continue;
                var best = ranked[0];
                var second = ranked.Count > 1 ? ranked[1] : null;
                if (best.Score < threshold) continue;
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

        private double ScoreProductAgainstSegment(string segment, Product product)
        {
            if (!_productSearchCache.TryGetValue(product.Id, out var cache)) return 0;

            string ns = NormalizeText(segment);
            var stopWords = new HashSet<string>(_appSettings.SmartSearchStopWords, StringComparer.OrdinalIgnoreCase);
            var st = ExtractMeaningfulTokens(ns, stopWords).ToHashSet(StringComparer.OrdinalIgnoreCase);
            
            var pt = cache.DescriptionTokens;
            var ct = cache.CategoryTokens;

            if (!st.Any() || !pt.Any()) return 0;

            int overlap = st.Intersect(pt, StringComparer.OrdinalIgnoreCase).Count();
            int catOverlap = st.Intersect(ct, StringComparer.OrdinalIgnoreCase).Count();
            double coverage = (double)overlap / pt.Count;
            double precision = (double)overlap / Math.Max(1, st.Count);
            double tri = DiceCoefficient(Trigrams(ns), cache.DescriptionTrigrams);

            double score = overlap * 2.7 + catOverlap * 0.8 + coverage * 3.5 + precision * 1.5 + tri * 4.0;
            
            string nd = NormalizeText(product.Description);
            if (ns.Contains(nd, StringComparison.OrdinalIgnoreCase)) score += 3.0;
            
            return score;
        }

        private static IEnumerable<string> ExtractMeaningfulTokens(string text, HashSet<string> stopWords)
        {
            return Regex.Split(text, @"[^a-z0-9]+").Where(t => t.Length >= 3 && !stopWords.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase);
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
