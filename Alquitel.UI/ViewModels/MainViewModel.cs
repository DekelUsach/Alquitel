using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Infrastructure.Persistence;
using Alquitel.Core.Interfaces;
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

namespace Alquitel.UI.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly AlquitelDbContext _dbContext;
        private readonly IDocumentService _documentService;
        private readonly ICollectionView _productsView;

        [ObservableProperty]
        private string _debugLog = "Iniciando sistema Alquitel...";

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
        private bool _isEventCalendarOpen;

        [ObservableProperty]
        private Order _currentOrder = new Order { Client = new Client(), Location = new Location() };

        [ObservableProperty]
        private Client? _selectedClient;

        [ObservableProperty]
        private int _selectionVersion;

        public ObservableCollection<Product> AvailableProducts { get; } = new();
        public ObservableCollection<OrderItem> SelectedItems { get; } = new();
        public decimal FinalBudget => SelectedItems.Sum(i => i.Total);

        public MainViewModel(AlquitelDbContext dbContext, IDocumentService documentService)
        {
            _dbContext = dbContext;
            _documentService = documentService;

            // Cargar productos existentes
            try {
                foreach(var p in _dbContext.Products.ToList()) AvailableProducts.Add(p);
            } catch (Exception ex) {
                Log("Error cargando productos: " + ex.Message);
            }

            _productsView = CollectionViewSource.GetDefaultView(AvailableProducts);
            _productsView.Filter = FilterProduct;

            SelectedItems.CollectionChanged += OnSelectedItemsCollectionChanged;
        }

        private void OnSelectedItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (OrderItem item in e.OldItems)
                {
                    item.PropertyChanged -= OnSelectedItemPropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (OrderItem item in e.NewItems)
                {
                    item.PropertyChanged += OnSelectedItemPropertyChanged;
                }
            }

            SelectionVersion++;
            OnPropertyChanged(nameof(FinalBudget));
        }

        private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(OrderItem.Quantity)
                || e.PropertyName == nameof(OrderItem.Dias)
                || e.PropertyName == nameof(OrderItem.UnitPrice)
                || e.PropertyName == nameof(OrderItem.Total))
            {
                SelectionVersion++;
                OnPropertyChanged(nameof(FinalBudget));
            }
        }

        partial void OnEventDaysChanged(int value)
        {
            if (value < 1)
            {
                EventDays = 1;
                return;
            }

            foreach (var item in SelectedItems)
            {
                item.Dias = value;
            }
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
            if (item is not Product product)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(SearchText))
            {
                return true;
            }

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

        [RelayCommand]
        private void AddProduct(Product product)
        {
            var existingItem = SelectedItems.FirstOrDefault(i => i.ProductId == product.Id);
            if (existingItem != null)
            {
                existingItem.Quantity += 1;
                existingItem.Dias = EventDays;
                Log($"Cantidad actualizada: {product.Description} x{existingItem.Quantity}");
                return;
            }

            var item = new OrderItem
            {
                ProductId = product.Id,
                Product = product,
                Quantity = 1,
                Dias = EventDays,
                UnitPrice = product.BasePrice,
                // Valores default para LED según imagen
                Uso = "IN",
                Forma = "PLANA",
                FactorForma = "MÓDULO"
            };
            SelectedItems.Add(item);
            CurrentOrder.Items.Add(item);
            
            Log($"Producto añadido: {product.Description}");
        }

        [RelayCommand]
        private void RemoveItem(OrderItem item)
        {
            SelectedItems.Remove(item);
            CurrentOrder.Items.Remove(item);
            Log($"Producto eliminado: {item.Product?.Description ?? "Item"}");
        }

        [RelayCommand]
        private void RemoveProduct(Product product)
        {
            var existingItem = SelectedItems.FirstOrDefault(i => i.ProductId == product.Id);
            if (existingItem == null)
            {
                return;
            }

            if (existingItem.Quantity > 1)
            {
                existingItem.Quantity -= 1;
                Log($"Cantidad reducida: {product.Description} x{existingItem.Quantity}");
                return;
            }

            SelectedItems.Remove(existingItem);
            CurrentOrder.Items.Remove(existingItem);
            Log($"Producto eliminado del pedido: {product.Description}");
        }

        [RelayCommand]
        private void SetTodayEventDate()
        {
            CurrentOrder.EventDate = DateTime.Today;
            IsEventCalendarOpen = false;
            OnPropertyChanged(nameof(CurrentOrder));
        }

        [RelayCommand]
        private void CloseEventCalendar()
        {
            IsEventCalendarOpen = false;
        }

        [RelayCommand]
        private void ToggleSmartSearchInput()
        {
            IsSmartSearchVisible = !IsSmartSearchVisible;
        }

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
                Log("No se detectaron productos en el texto.");
                MessageBox.Show("No detecté productos del catálogo en ese texto.", "Búsqueda inteligente", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (SelectedItems.Any())
            {
                var replace = MessageBox.Show(
                    "Ya hay productos en el pedido. ¿Querés reemplazarlos con lo detectado en el texto?",
                    "Confirmar reemplazo",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (replace == MessageBoxResult.Yes)
                {
                    SelectedItems.Clear();
                    CurrentOrder.Items.Clear();
                }
            }

            int addedCount = 0;
            foreach (var result in matches.OrderByDescending(m => m.Score))
            {
                if (SelectedItems.Any(i => i.ProductId == result.Product.Id))
                {
                    continue;
                }

                var item = new OrderItem
                {
                    ProductId = result.Product.Id,
                    Product = result.Product,
                    Quantity = result.Quantity,
                    Dias = EventDays,
                    UnitPrice = result.Product.BasePrice,
                    Uso = "IN",
                    Forma = "PLANA",
                    FactorForma = "MÓDULO"
                };

                SelectedItems.Add(item);
                CurrentOrder.Items.Add(item);
                addedCount++;
            }

            Log($"Búsqueda inteligente: {matches.Count} detectado(s), {addedCount} agregado(s).");
            MessageBox.Show($"Se agregaron {addedCount} producto(s) automáticamente.", "Búsqueda inteligente", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        [RelayCommand]
        private async Task GenerateBudget()
        {
            await GenerateDocument("1_PRESUPUESTOS", @"1_PRESUPUESTOS\template.docx", false);
        }

        [RelayCommand]
        private async Task GenerateOF()
        {
            await GenerateDocument("2_OF", "OF  9054 - 0326 - B + T - FERIA DEL LIBRO 2026 - SG.docx", false);
        }

        [RelayCommand]
        private async Task GenerateOT()
        {
            await GenerateDocument("3_OT", "OT  9054 - 0326 - B + T - FERIA DEL LIBRO 2026 - SG.docx", true);
        }

        private async Task GenerateDocument(string folderName, string templateName, bool isTechnical)
        {
            try
            {
                if (!ValidateOrderForGeneration(out string validationMessage))
                {
                    MessageBox.Show(validationMessage, "Datos incompletos", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Log("Generación bloqueada por validaciones incompletas.");
                    return;
                }

                Log($"Iniciando generación de {folderName}...");
                
                string baseDir = @"C:\Alquitel";
                string targetDir = Path.Combine(baseDir, folderName);
                string templatePath = Path.Combine(baseDir, templateName);
                
                Log($"Carpeta destino: {targetDir}");
                Log($"Plantilla origen: {templatePath}");

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

                if (!Directory.Exists(targetDir))
                {
                    Log("Creando carpeta destino...");
                    Directory.CreateDirectory(targetDir);
                }

                // Generar Nombre de Archivo según política corporativa:
                // [nro]- [MMdd]- [empresa]- [lugar]- [iniciales].docx
                string datePart = CurrentOrder.CreatedDate.ToString("MMdd");
                string empresaPart = string.IsNullOrWhiteSpace(CurrentOrder.Client?.CompanyName) ? "CLIENTE" : CurrentOrder.Client.CompanyName;
                string lugarPart = string.IsNullOrWhiteSpace(CurrentOrder.Location?.Name) ? "LUGAR" : CurrentOrder.Location.Name;
                string inicialesPart = GetInitials(CurrentOrder.AdminName);

                string fileName = $"{CurrentOrder.BudgetNumber}- {datePart}- {empresaPart}- {lugarPart}- {inicialesPart}.docx";
                
                // Limpiar caracteres no válidos para nombres de archivo de Windows
                foreach (char c in Path.GetInvalidFileNameChars()) { fileName = fileName.Replace(c, '_'); }
                
                string outputPath = Path.Combine(targetDir, fileName);

                Log($"Archivo de salida (Poliza Corporativa): {outputPath}");

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

            if (string.IsNullOrWhiteSpace(CurrentOrder.Client?.CompanyName))
            {
                errors.Add("Cliente: completá Empresa / Cliente.");
            }

            if (string.IsNullOrWhiteSpace(CurrentOrder.BudgetNumber))
            {
                errors.Add("N° Presupuesto: ingresá un número de presupuesto.");
            }

            if (!CurrentOrder.EventDate.HasValue)
            {
                errors.Add("Fecha del evento: seleccioná una fecha.");
            }

            if (EventDays < 1)
            {
                errors.Add("Días: debe ser mayor o igual a 1.");
            }

            if (!SelectedItems.Any())
            {
                errors.Add("Productos: agregá al menos un producto al pedido.");
            }

            if (!errors.Any())
            {
                message = string.Empty;
                return true;
            }

            message = "No se puede generar el documento. Revisá estos campos:\n\n- " + string.Join("\n- ", errors);
            return false;
        }

        private void Log(string message)
        {
            DebugLog += $"\n[{DateTime.Now:HH:mm:ss}] {message}";
        }

        private IEnumerable<SmartMatchResult> FindProductsFromParagraph(string paragraph)
        {
            var segments = BuildSmartSegments(paragraph);

            var aggregated = new Dictionary<Guid, SmartMatchResult>();

            foreach (var segment in segments)
            {
                int quantity = ExtractQuantityFromSegment(segment);
                var ranked = AvailableProducts
                    .Select(product => new SmartMatchResult(product, quantity, ScoreProductAgainstSegment(segment, product)))
                    .OrderByDescending(x => x.Score)
                    .ToList();

                if (!ranked.Any())
                {
                    continue;
                }

                var best = ranked[0];
                var second = ranked.Count > 1 ? ranked[1] : null;

                // Umbral minimo para evitar elegir cualquier cosa cuando el texto no describe catalogo.
                if (best.Score < 4.0)
                {
                    continue;
                }

                // Si el primero y el segundo son casi iguales, no forzar seleccion ambigua.
                if (second != null && Math.Abs(best.Score - second.Score) < 0.35)
                {
                    continue;
                }

                if (aggregated.TryGetValue(best.Product.Id, out var existing))
                {
                    aggregated[best.Product.Id] = existing with
                    {
                        Quantity = existing.Quantity + best.Quantity,
                        Score = Math.Max(existing.Score, best.Score)
                    };
                }
                else
                {
                    aggregated[best.Product.Id] = best;
                }
            }

            return aggregated.Values;
        }

        private static List<string> BuildSmartSegments(string paragraph)
        {
            var primarySegments = paragraph
                .Split(new[] { '.', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s));

            var expandedSegments = new List<string>();
            foreach (var segment in primarySegments)
            {
                var parts = Regex.Split(segment, @"\s+y\s+", RegexOptions.IgnoreCase)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s));
                expandedSegments.AddRange(parts);
            }

            return expandedSegments;
        }

        private static int ExtractQuantityFromSegment(string segment)
        {
            string rawSegment = RemoveDiacritics(segment).ToLowerInvariant();

            // Casos: "2x pantalla", "2 unidades pantalla", "pantalla x 2"
            var explicitQtyPatterns = new[]
            {
                @"\b(\d{1,3})(?![\.,]\d)\s*(x|u|ud|uds|unidad|unidades)\b",
                @"\b(?:x|por)\s*(\d{1,3})(?![\.,]\d)\b",
                @"\b(\d{1,3})(?![\.,]\d)\s*(?:pantalla|pantallas|notebook|notebooks|camara|camaras|servicio|servicios|traslado|traslados|touch|equipo|equipos)\b"
            };

            foreach (var pattern in explicitQtyPatterns)
            {
                var qtyMatch = Regex.Match(rawSegment, pattern, RegexOptions.IgnoreCase);
                if (qtyMatch.Success && int.TryParse(qtyMatch.Groups[1].Value, out int explicitQty) && explicitQty > 0)
                {
                    return explicitQty;
                }
            }

            return 1;
        }

        private static double ScoreProductAgainstSegment(string segment, Product product)
        {
            string normalizedSegment = NormalizeText(segment);
            string normalizedDescription = NormalizeText(product.Description);
            string normalizedCategory = NormalizeText(product.Category);

            var segmentTokens = ExtractMeaningfulTokens(normalizedSegment).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var productTokens = ExtractMeaningfulTokens(normalizedDescription).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var categoryTokens = ExtractMeaningfulTokens(normalizedCategory).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!segmentTokens.Any() || !productTokens.Any())
            {
                return 0;
            }

            int overlap = segmentTokens.Intersect(productTokens, StringComparer.OrdinalIgnoreCase).Count();
            int categoryOverlap = segmentTokens.Intersect(categoryTokens, StringComparer.OrdinalIgnoreCase).Count();

            double coverage = (double)overlap / productTokens.Count;
            double precision = (double)overlap / Math.Max(1, segmentTokens.Count);
            double trigramSimilarity = DiceCoefficient(Trigrams(normalizedSegment), Trigrams(normalizedDescription));

            double score = 0;
            score += overlap * 2.7;
            score += categoryOverlap * 0.8;
            score += coverage * 3.5;
            score += precision * 1.5;
            score += trigramSimilarity * 4.0;

            if (normalizedSegment.Contains(normalizedDescription, StringComparison.OrdinalIgnoreCase))
            {
                score += 3.0;
            }

            return score;
        }

        private static IEnumerable<string> ExtractMeaningfulTokens(string text)
        {
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "de", "la", "el", "los", "las", "con", "para", "por", "del", "y", "plus", "pro", "edition", "business", "servicio"
            };

            return Regex.Split(text, @"[^a-z0-9]+")
                .Where(t => t.Length >= 3 && !stopWords.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string formD = RemoveDiacritics(text);
            var sb = new StringBuilder(formD.Length);
            foreach (char c in formD)
            {
                if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append(' ');
                }
            }

            return Regex.Replace(sb.ToString().ToLowerInvariant(), @"\s+", " ").Trim();
        }

        private static bool ContainsWholeWord(string source, string word)
        {
            return Regex.IsMatch(source, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);
        }

        private static HashSet<string> Trigrams(string input)
        {
            string text = $"  {input}  ";
            var grams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (text.Length < 3)
            {
                grams.Add(text);
                return grams;
            }

            for (int i = 0; i <= text.Length - 3; i++)
            {
                grams.Add(text.Substring(i, 3));
            }

            return grams;
        }

        private static double DiceCoefficient(HashSet<string> a, HashSet<string> b)
        {
            if (!a.Any() || !b.Any())
            {
                return 0;
            }

            int intersection = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
            return (2.0 * intersection) / (a.Count + b.Count);
        }

        private static string RemoveDiacritics(string text)
        {
            string normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private sealed record SmartMatchResult(Product Product, int Quantity, double Score);
    }
}
