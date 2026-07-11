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
    /// <summary>Opción de estado de presupuesto con etiqueta en español para la UI.</summary>
    public sealed record StatusOption(OrderStatus Value, string Label);

    public partial class BudgetBuilderViewModel : ObservableObject, IAsyncInitialization, IDisposable
    {
        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
        private readonly IDocumentService _documentService;
        private readonly ICollectionView _productsView;
        private readonly IAppSettings _appSettings;
        private readonly IDialogService _dialogService;
        private readonly ICurrentUserService _currentUserService;
        private readonly Alquitel.Core.Interfaces.Repositories.IOrderRepository _orderRepository;
        private readonly ITemplateStorageService _templateStorage;

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

        /// <summary>Directorio de clientes para el buscador por nombre del encabezado.</summary>
        public ObservableCollection<Client> KnownClients { get; } = new();

        /// <summary>Estados disponibles del presupuesto (combo del encabezado).</summary>
        public IReadOnlyList<StatusOption> StatusOptions { get; } = new[]
        {
            new StatusOption(OrderStatus.Draft, "Borrador"),
            new StatusOption(OrderStatus.Approved, "Aprobado"),
            new StatusOption(OrderStatus.SentToOF, "Enviado a OF"),
            new StatusOption(OrderStatus.SentToOT, "Enviado a OT"),
            new StatusOption(OrderStatus.Rejected, "Rechazado"),
            new StatusOption(OrderStatus.Archived, "Archivado"),
        };
        public decimal FinalBudget => SelectedItems.Sum(i => i.Total);
        public Visibility CommercialColumnsVisibility =>
            IsTechnicalView ? Visibility.Collapsed : Visibility.Visible;

        private Dictionary<Guid, ProductCacheEntry> _productSearchCache = new();
        private CancellationTokenSource? _autosaveCts;
        private string _draftsFolder;

        // ── Undo del carrito: snapshots de items antes de cada mutación ──
        private readonly List<List<OrderItem>> _undoSnapshots = new();
        private const int MaxUndoSnapshots = 20;
        private bool _isRestoringUndo;

        private class ProductCacheEntry
        {
            public HashSet<string> DescriptionTokens { get; set; } = new();
            public HashSet<string> CategoryTokens { get; set; } = new();
            public HashSet<string> DescriptionTrigrams { get; set; } = new();
            public string NormalizedDescription { get; set; } = string.Empty;
        }

        public BudgetBuilderViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory, IDocumentService documentService, IAppSettings appSettings, IDialogService dialogService, ICurrentUserService currentUserService, Alquitel.Core.Interfaces.Repositories.IOrderRepository orderRepository, ITemplateStorageService templateStorage)
        {
            _dbContextFactory = dbContextFactory;
            _documentService = documentService;
            _appSettings = appSettings;
            _dialogService = dialogService;
            _currentUserService = currentUserService;
            _orderRepository = orderRepository;
            _templateStorage = templateStorage;

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
            // Multi-usuario: el presupuesto nuevo queda firmado por quien está logueado.
            if (string.IsNullOrWhiteSpace(CurrentOrder.AdminName) && _currentUserService.Current != null)
            {
                CurrentOrder.AdminName = _currentUserService.Current.Name;
                OnPropertyChanged(nameof(CurrentOrder));
            }

            // Numeración en serie: el presupuesto nuevo toma el número siguiente al
            // mayor existente en la base (compartida por todo el equipo).
            await AssignNextSerialIfEmptyAsync();

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
                    string nd = NormalizeText(p.Description);
                    _productSearchCache[p.Id] = new ProductCacheEntry
                    {
                        DescriptionTokens = ExtractMeaningfulTokens(nd, stopWords).ToHashSet(StringComparer.OrdinalIgnoreCase),
                        CategoryTokens = ExtractMeaningfulTokens(NormalizeText(p.Category), stopWords).ToHashSet(StringComparer.OrdinalIgnoreCase),
                        DescriptionTrigrams = Trigrams(nd),
                        NormalizedDescription = nd
                    };
                }

                // Directorio de clientes para el buscador por nombre.
                var clients = await db.Clients.AsNoTracking().OrderBy(c => c.CompanyName).ToListAsync();
                KnownClients.Clear();
                foreach (var c in clients) KnownClients.Add(c);
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
                        // Convert to DTO to avoid circular references during serialization.
                        // Must mirror every user-editable field of Order/OrderItem or the draft loses data.
                        var dto = new
                        {
                            CurrentOrder.Id,
                            CurrentOrder.BudgetNumber,
                            CurrentOrder.AdminName,
                            CurrentOrder.CreatedByUserId,
                            ClientName = CurrentOrder.Client?.CompanyName,
                            ClientCuit = CurrentOrder.Client?.Cuit,
                            LocationName = CurrentOrder.Location?.Name,
                            CurrentOrder.EventDate,
                            CurrentOrder.EventEndDate,
                            CurrentOrder.CreatedDate,
                            Status = CurrentOrder.Status.ToString(),
                            Items = SelectedItems.Select(i => new
                            {
                                i.ProductId, i.Quantity, i.Dias, i.UnitPrice, i.Total,
                                i.TechnicalNotes, i.ImagePath, i.CustomFieldsJson,
                                i.DescriptionSnapshot, i.RequestedMeasure
                            }).ToList()
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

        /// <summary>
        /// Removes the autosave draft once the order was persisted to the database,
        /// so stale drafts don't accumulate in %AppData%\Alquitel\Drafts.
        /// </summary>
        private void TryDeleteDraft()
        {
            try
            {
                var draftName = CurrentOrder.Id == Guid.Empty ? "new_draft.json" : $"draft_{CurrentOrder.Id}.json";
                var path = Path.Combine(_draftsFolder, draftName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Could not delete draft for order {OrderId}", CurrentOrder.Id);
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
            if (item is not Product product) return false;
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            return product.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || product.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        }

        partial void OnCuitInputChanged(string value)
        {
            // Only hit the DB once the input is a complete, checksum-valid CUIT.
            // A synchronous query per keystroke blocked the UI thread while typing.
            if (!CuitValidator.IsValid(value)) return;
            _ = LookupClientByCuitAsync(value);
        }

        private async Task LookupClientByCuitAsync(string cuit)
        {
            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Cuit == cuit);
                // Guard against stale results if the user kept typing meanwhile
                if (client != null && CuitInput == cuit)
                {
                    CurrentOrder.Client = client;
                    OnPropertyChanged(nameof(CurrentOrder));
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "CUIT lookup failed for {Cuit}", cuit);
            }
        }

        partial void OnSelectedClientChanged(Client? value)
        {
            if (value == null) return;

            // Clon: el TextBox de "Empresa / Cliente" es editable y no debe mutar la
            // instancia compartida de la lista KnownClients.
            CurrentOrder.Client = new Client
            {
                Id = value.Id,
                CompanyName = value.CompanyName,
                Cuit = value.Cuit,
                ContactName = value.ContactName,
                Phone = value.Phone,
                Email = value.Email,
                InternalNotes = value.InternalNotes,
            };
            OnPropertyChanged(nameof(CurrentOrder));
            CuitInput = value.Cuit ?? string.Empty;
        }

        // Al cargar una orden existente EventDays se setea desde el primer ítem; sin esta
        // bandera ese seteo pisaba los Dias individuales del resto de los ítems.
        private bool _suppressEventDaysPropagation;

        partial void OnEventDaysChanged(int value)
        {
            if (value < 1) { EventDays = 1; return; }
            if (_suppressEventDaysPropagation) return;
            foreach (var item in SelectedItems) item.Dias = value;
        }

        private void SetEventDaysWithoutPropagation(int value)
        {
            _suppressEventDaysPropagation = true;
            try { EventDays = Math.Max(1, value); }
            finally { _suppressEventDaysPropagation = false; }
        }

        partial void OnIsTechnicalViewChanged(bool value)
        {
            OnPropertyChanged(nameof(CommercialColumnsVisibility));
        }

        [RelayCommand]
        private void SetCommercialView() => IsTechnicalView = false;

        [RelayCommand]
        private void SetTechnicalView() => IsTechnicalView = true;

        // ── Undo del carrito ─────────────────────────────────────────

        private static OrderItem CloneItem(OrderItem i) => new OrderItem
        {
            Id = i.Id,
            OrderId = i.OrderId,
            ProductId = i.ProductId,
            Product = i.Product,
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice,
            Dias = i.Dias,
            TechnicalNotes = i.TechnicalNotes,
            ImagePath = i.ImagePath,
            CustomFieldsJson = i.CustomFieldsJson,
            DescriptionSnapshot = i.DescriptionSnapshot,
            RequestedMeasure = i.RequestedMeasure,
        };

        /// <summary>Guarda el estado actual del carrito antes de una mutación (agregar/quitar/reemplazar).</summary>
        private void PushUndoSnapshot()
        {
            if (_isRestoringUndo) return;
            _undoSnapshots.Add(SelectedItems.Select(CloneItem).ToList());
            if (_undoSnapshots.Count > MaxUndoSnapshots) _undoSnapshots.RemoveAt(0);
            UndoCartCommand.NotifyCanExecuteChanged();
        }

        private bool CanUndoCart() => _undoSnapshots.Count > 0;

        [RelayCommand(CanExecute = nameof(CanUndoCart))]
        private void UndoCart()
        {
            if (_undoSnapshots.Count == 0) return;
            var snapshot = _undoSnapshots[^1];
            _undoSnapshots.RemoveAt(_undoSnapshots.Count - 1);

            _isRestoringUndo = true;
            try
            {
                ClearCart();
                foreach (var item in snapshot)
                    AddItemToCart(item);
            }
            finally
            {
                _isRestoringUndo = false;
            }
            UndoCartCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand]
        private void AddProduct(Product product)
        {
            PushUndoSnapshot();
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
            AddItemToCart(item);
        }

        [RelayCommand]
        private void RemoveItem(OrderItem item)
        {
            PushUndoSnapshot();
            RemoveItemFromCart(item);
        }

        [RelayCommand]
        private void RemoveProduct(Product product)
        {
            var existingItem = SelectedItems.FirstOrDefault(i => i.ProductId == product.Id);
            if (existingItem == null) return;
            PushUndoSnapshot();
            if (existingItem.Quantity > 1) { existingItem.Quantity -= 1; return; }
            RemoveItemFromCart(existingItem);
        }

        // SelectedItems (vista) y CurrentOrder.Items (modelo) deben permanecer sincronizados.
        // Estos helpers evitan que una colección se actualice sin la otra.
        private void AddItemToCart(OrderItem item)
        {
            SelectedItems.Add(item);
            CurrentOrder.Items.Add(item);
        }

        private void RemoveItemFromCart(OrderItem item)
        {
            SelectedItems.Remove(item);
            CurrentOrder.Items.Remove(item);
        }

        private void ClearCart()
        {
            SelectedItems.Clear();
            CurrentOrder.Items.Clear();
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

            PushUndoSnapshot();
            if (SelectedItems.Any())
            {
                var replace = _dialogService.ShowConfirm(
                    "Confirmar reemplazo",
                    "Ya hay productos en el pedido. ¿Querés reemplazarlos con lo detectado en el texto?");
                if (replace) ClearCart();
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
                AddItemToCart(item);
                addedCount++;
            }
            _dialogService.ShowInfo("Búsqueda inteligente", $"Se agregaron {addedCount} producto(s) automáticamente.");
        }

        [RelayCommand]
        private async Task GenerateBudget()
        {
            await GenerateDocument(_appSettings.PresupuestosFolder, _appSettings.PresupuestosTemplate, false, TemplateKind.Presupuesto);
        }

        [RelayCommand]
        private async Task GenerateOF()
        {
            await GenerateDocument(_appSettings.OfFolder, _appSettings.OfTemplate, false, TemplateKind.OF);
        }

        [RelayCommand]
        private async Task GenerateOT()
        {
            await GenerateDocument(_appSettings.OtFolder, _appSettings.OtTemplate, true, TemplateKind.OT);
        }

        private async Task GenerateDocument(string targetDir, string templatePath, bool isTechnical, TemplateKind templateKind)
        {
            try
            {
                if (!ValidateOrderForGeneration(out string validationMessage))
                {
                    _dialogService.ShowWarning("Datos incompletos", validationMessage);
                    return;
                }

                // §2 Disponibilidad: advierte si el pedido supera el stock ya comprometido
                // en otras órdenes activas para la misma fecha. Advierte, no bloquea.
                var stockWarnings = await RefreshStockConflictsAsync();
                if (stockWarnings.Count > 0)
                {
                    var proceed = _dialogService.ShowConfirm(
                        "Posible conflicto de stock",
                        "Estos productos superan el stock disponible para la fecha del evento:\n\n- " +
                        string.Join("\n- ", stockWarnings) +
                        "\n\n¿Generar el documento de todos modos?");
                    if (!proceed) return;
                }

                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                // El documento lo firma quien está logueado (recuadro celeste del final),
                // aunque el presupuesto cargado sea histórico de otro empleado.
                if (_currentUserService.Current != null)
                    CurrentOrder.AdminName = _currentUserService.Current.Name;

                // Los campos Contacto/CUIT de los documentos salen SIEMPRE de la ficha
                // del cliente: si se tipeó a mano y el cliente existe en la base,
                // completar los datos de contacto que falten.
                await HydrateClientContactAsync();

                // CreatedDate is UTC; use local time so evening budgets don't get tomorrow's date
                string datePart = CurrentOrder.CreatedDate.ToLocalTime().ToString("MMdd");
                string empresaPart = string.IsNullOrWhiteSpace(CurrentOrder.Client?.CompanyName) ? "CLIENTE" : CurrentOrder.Client.CompanyName;
                string lugarPart = string.IsNullOrWhiteSpace(CurrentOrder.Location?.Name) ? "LUGAR" : CurrentOrder.Location.Name;
                string inicialesPart = GetInitials(CurrentOrder.AdminName);
                string numeroPart = BudgetNumberHelper.ToFileNameForm(CurrentOrder.BudgetNumber); // "31294/2" → "31294(2)"
                string fileName = $"{numeroPart}- {datePart}- {empresaPart}- {lugarPart}- {inicialesPart}.docx";
                foreach (char c in Path.GetInvalidFileNameChars()) { fileName = fileName.Replace(c, '_'); }
                string outputPath = Path.Combine(targetDir, fileName);

                // Plantilla centralizada: la versión publicada en Supabase Storage tiene
                // prioridad (descarga con cache offline). Si no hay plantilla en la nube,
                // se usa la ruta local configurada en Configuración.
                string effectiveTemplate = templatePath;
                var cloudTemplate = await _templateStorage.ResolveTemplateAsync(templateKind);
                if (!string.IsNullOrEmpty(cloudTemplate))
                    effectiveTemplate = cloudTemplate;

                if (!File.Exists(effectiveTemplate))
                {
                    _dialogService.ShowError("Error de Plantilla",
                        "No hay plantilla disponible.\n\n" +
                        $"No se encontró plantilla publicada en el servidor ni en la ruta local: {templatePath}\n" +
                        "Un Admin puede publicarla desde Configuración → Plantillas en la nube.");
                    return;
                }

                await _documentService.GenerateDocumentAsync(CurrentOrder, effectiveTemplate, outputPath, isTechnical, _appSettings.ExportPdf);
                bool persisted = await PersistOrderAsync();

                if (persisted)
                {
                    TryDeleteDraft();
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

        /// <summary>
        /// Completa ContactName/Email/Phone/Cuit del cliente del pedido con los datos de
        /// su ficha en la base (buscada por Id, CUIT o razón social). Garantiza que la OT
        /// imprima el contacto y CUIT del CLIENTE aunque se lo haya tipeado a mano.
        /// </summary>
        private async Task HydrateClientContactAsync()
        {
            var client = CurrentOrder.Client;
            if (client == null) return;
            if (!string.IsNullOrWhiteSpace(client.ContactName) &&
                !string.IsNullOrWhiteSpace(client.Email) &&
                !string.IsNullOrWhiteSpace(client.Phone) &&
                !string.IsNullOrWhiteSpace(client.Cuit)) return;

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                Client? stored = null;
                if (client.Id != Guid.Empty)
                    stored = await db.Clients.AsNoTracking().IgnoreQueryFilters()
                        .FirstOrDefaultAsync(c => c.Id == client.Id);
                if (stored == null && !string.IsNullOrWhiteSpace(client.Cuit))
                {
                    var cuit = client.Cuit.Trim();
                    stored = await db.Clients.AsNoTracking().IgnoreQueryFilters()
                        .FirstOrDefaultAsync(c => c.Cuit == cuit);
                }
                if (stored == null && !string.IsNullOrWhiteSpace(client.CompanyName))
                {
                    var name = client.CompanyName.Trim();
                    stored = await db.Clients.AsNoTracking().IgnoreQueryFilters()
                        .FirstOrDefaultAsync(c => c.CompanyName == name);
                }
                if (stored == null) return;

                if (string.IsNullOrWhiteSpace(client.ContactName)) client.ContactName = stored.ContactName;
                if (string.IsNullOrWhiteSpace(client.Email)) client.Email = stored.Email;
                if (string.IsNullOrWhiteSpace(client.Phone)) client.Phone = stored.Phone;
                if (string.IsNullOrWhiteSpace(client.Cuit)) client.Cuit = stored.Cuit;
                OnPropertyChanged(nameof(CurrentOrder));
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "HydrateClientContactAsync failed");
            }
        }

        /// <summary>
        /// Crea una nueva VERSIÓN (rama) del presupuesto actual: misma serie con sufijo
        /// incremental ("31294" → "31294/2" → "31294/3"). La rama es una orden nueva e
        /// independiente: se puede modificar completa sin tocar la versión anterior.
        /// </summary>
        private async Task AssignNextSerialIfEmptyAsync()
        {
            if (!string.IsNullOrWhiteSpace(CurrentOrder.BudgetNumber)) return;
            try
            {
                var numbers = await _orderRepository.GetAllBudgetNumbersAsync();
                CurrentOrder.BudgetNumber = BudgetNumberHelper.NextSerial(numbers);
                OnPropertyChanged(nameof(CurrentOrder));
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "No se pudo calcular el próximo número de presupuesto");
            }
        }

        [RelayCommand]
        private async Task CreateNewVersionAsync()
        {
            if (string.IsNullOrWhiteSpace(CurrentOrder.BudgetNumber))
            {
                _dialogService.ShowWarning("Nueva versión",
                    "El presupuesto actual no tiene número: asignale un número antes de crear una versión.");
                return;
            }

            try
            {
                var numbers = await _orderRepository.GetAllBudgetNumbersAsync();
                string newNumber = BudgetNumberHelper.NextVersion(CurrentOrder.BudgetNumber, numbers);

                if (!_dialogService.ShowConfirm("Nueva versión",
                    $"Se creará la versión {newNumber} como copia editable del presupuesto " +
                    $"{CurrentOrder.BudgetNumber}. La versión anterior no se modifica. ¿Continuar?"))
                    return;

                PushUndoSnapshot();

                // Identidad nueva, contenido idéntico: misma mecánica que "Repetir pedido"
                // pero conservando cliente, lugar, fecha de evento y la serie del número.
                CurrentOrder.Id = Guid.NewGuid();
                CurrentOrder.BudgetNumber = newNumber;
                CurrentOrder.CreatedDate = DateTime.UtcNow;
                CurrentOrder.Status = OrderStatus.Draft;
                if (_currentUserService.Current != null)
                {
                    CurrentOrder.AdminName = _currentUserService.Current.Name;
                    CurrentOrder.CreatedByUserId = _currentUserService.Current.Id;
                }

                foreach (var item in SelectedItems)
                {
                    item.Id = Guid.NewGuid();
                    item.OrderId = CurrentOrder.Id;
                }

                OnPropertyChanged(nameof(CurrentOrder));
                _dialogService.ShowInfo("Nueva versión",
                    $"Estás editando la versión {newNumber}. Los cambios se guardan como presupuesto aparte al generar.");
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "CreateNewVersionAsync failed");
                _dialogService.ShowError("Error al crear versión", ex.Message);
            }
        }

        /// <summary>
        /// Carga una orden existente como COPIA: mismos productos, cantidades y cliente,
        /// pero con identidad nueva (Id/N° presupuesto/fechas en blanco). Es la base del
        /// botón "Repetir pedido" del dashboard.
        /// </summary>
        public async Task LoadOrderCopyByIdAsync(Guid orderId)
        {
            var full = await FetchOrderAsync(orderId);
            if (full == null) return; // la orden original no existe

            ApplyLoadedOrder(full);

            CurrentOrder.Id = Guid.NewGuid();
            CurrentOrder.BudgetNumber = string.Empty;
            CurrentOrder.CreatedDate = DateTime.UtcNow;
            CurrentOrder.EventDate = null; // el evento nuevo tendrá otra fecha; forzar selección
            CurrentOrder.EventEndDate = null;
            CurrentOrder.Status = OrderStatus.Draft;

            // La copia es un pedido nuevo: lo firma quien está logueado, no el autor original.
            if (_currentUserService.Current != null)
            {
                CurrentOrder.AdminName = _currentUserService.Current.Name;
                CurrentOrder.CreatedByUserId = _currentUserService.Current.Id;
            }

            foreach (var item in SelectedItems)
            {
                item.Id = Guid.NewGuid();
                item.OrderId = CurrentOrder.Id;
            }

            // Order no implementa INotifyPropertyChanged: re-notificar la raíz refresca los bindings.
            OnPropertyChanged(nameof(CurrentOrder));

            // La copia arranca con el próximo número de la serie.
            await AssignNextSerialIfEmptyAsync();
        }

        /// <summary>
        /// Carga una orden existente para EDITARLA: conserva Id, número, fechas y estado.
        /// Es lo que usa la "Actividad reciente" del dashboard al tocar un presupuesto.
        /// </summary>
        public async Task<bool> LoadOrderForEditAsync(Guid orderId)
        {
            var full = await FetchOrderAsync(orderId);
            if (full == null) return false;

            ApplyLoadedOrder(full);
            OnPropertyChanged(nameof(CurrentOrder));
            return true;
        }

        /// <summary>
        /// Carga una orden ya armada en memoria (la rama creada por el editor de
        /// versiones de la sección Presupuestos). A diferencia de LoadOrder, no lee
        /// de la base: la orden viene lista con su número de versión e items editados.
        /// </summary>
        public void LoadBranchOrder(Order branch)
        {
            _undoSnapshots.Clear();
            UndoCartCommand.NotifyCanExecuteChanged();

            var items = branch.Items.ToList();
            branch.Items = new List<OrderItem>();
            CurrentOrder = branch;

            ClearCart();
            foreach (var item in items)
                AddItemToCart(item);

            SetEventDaysWithoutPropagation(items.FirstOrDefault()?.Dias ?? 1);
            CuitInput = branch.Client?.Cuit ?? string.Empty;
            OnPropertyChanged(nameof(CurrentOrder));
        }

        /// <summary>
        /// Lee la orden completa de la base (incluyendo entidades archivadas) sin
        /// bloquear el hilo de UI — importante en modo servidor (PostgreSQL remoto).
        /// </summary>
        private async Task<Order?> FetchOrderAsync(Guid orderId)
        {
            using var db = await _dbContextFactory.CreateDbContextAsync();
            return await db.Orders
                .AsNoTracking()
                .IgnoreQueryFilters() // Include archived clients/products in historical orders
                .Include(o => o.Client)
                .Include(o => o.Location)
                .Include(o => o.Items).ThenInclude(i => i.Product)
                .FirstOrDefaultAsync(o => o.Id == orderId);
        }

        /// <summary>Vuelca una orden ya leída de la base al estado del armador (carrito, cliente, días).</summary>
        private void ApplyLoadedOrder(Order full)
        {
            // El historial que se carga reemplaza al carrito actual: los snapshots viejos ya no aplican.
            _undoSnapshots.Clear();
            UndoCartCommand.NotifyCanExecuteChanged();

            CurrentOrder = new Order
            {
                Id = full.Id,
                BudgetNumber = full.BudgetNumber,
                AdminName = full.AdminName,
                CreatedByUserId = full.CreatedByUserId,
                CreatedDate = full.CreatedDate,
                EventDate = full.EventDate,
                EventEndDate = full.EventEndDate,
                Status = full.Status,
                Client = full.Client ?? new Client(),
                Location = full.Location ?? new Location(),
            };

            ClearCart();

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
                AddItemToCart(oi);
            }

            SetEventDaysWithoutPropagation(full.Items.FirstOrDefault()?.Dias ?? 1);
            CuitInput = full.Client?.Cuit ?? string.Empty;
        }

        /// <summary>
        /// Recalcula el flag <see cref="OrderItem.HasStockConflict"/> de cada ítem del
        /// carrito contra el stock comprometido en otras órdenes activas y devuelve la
        /// lista de advertencias legibles (vacía si no hay conflictos).
        /// </summary>
        private async Task<List<string>> RefreshStockConflictsAsync()
        {
            var warnings = new List<string>();

            if (!CurrentOrder.EventDate.HasValue)
            {
                foreach (var i in SelectedItems) i.HasStockConflict = false;
                return warnings;
            }

            var start = CurrentOrder.EventDate.Value.Date;

            foreach (var group in SelectedItems.GroupBy(i => i.ProductId).ToList())
            {
                var product = group.First().Product;
                if (product?.StockQuantity is not int stock)
                {
                    foreach (var i in group) i.HasStockConflict = false;
                    continue;
                }

                var end = start.AddDays(Math.Max(1, group.Max(i => i.Dias)));
                int committed;
                try
                {
                    committed = await _orderRepository.GetCommittedQuantityAsync(
                        group.Key, start, end, CurrentOrder.Id);
                }
                catch (Exception ex)
                {
                    AppLog.Warning(ex, "Chequeo de stock falló para producto {ProductId}", group.Key);
                    continue;
                }

                var requested = group.Sum(i => i.Quantity);
                bool conflict = requested + committed > stock;
                foreach (var i in group) i.HasStockConflict = conflict;

                if (conflict)
                {
                    warnings.Add(
                        $"{product.Description}: pedidos {requested} + comprometidos {committed} " +
                        $"en otras órdenes > stock total {stock}.");
                }
            }

            return warnings;
        }

        private async Task<bool> PersistOrderAsync()
        {
            try
            {
                // Firma multi-usuario: la primera persistencia registra quién creó la orden.
                CurrentOrder.CreatedByUserId ??= _currentUserService.Current?.Id;

                using var db = await _dbContextFactory.CreateDbContextAsync();
                await using var tx = await db.Database.BeginTransactionAsync();

                // ── Location: find-or-create so Order.LocationId always references a real row.
                // A Guid.Empty FK violated the constraint and silently failed the whole persist. ──
                var locName = (CurrentOrder.Location?.Name ?? string.Empty).Trim();
                var location = await db.Locations.FirstOrDefaultAsync(l => l.Name == locName);
                if (location == null)
                {
                    location = new Location { Name = locName };
                    db.Locations.Add(location);
                    await db.SaveChangesAsync();
                }

                // ── Client: reuse existing (by Id, then by CUIT) or create it.
                // A client typed manually never existed in the DB and broke the FK. ──
                var clientId = await ResolveClientIdAsync(db);
                var locationId = location.Id;

                var orderExists = await db.Orders.AnyAsync(o => o.Id == CurrentOrder.Id);

                if (!orderExists)
                {
                    var orderToSave = new Order
                    {
                        Id = CurrentOrder.Id,
                        BudgetNumber = CurrentOrder.BudgetNumber,
                        AdminName = CurrentOrder.AdminName,
                        CreatedByUserId = CurrentOrder.CreatedByUserId,
                        ClientId = clientId,
                        LocationId = locationId,
                        CreatedDate = CurrentOrder.CreatedDate,
                        EventDate = CurrentOrder.EventDate,
                        EventEndDate = CurrentOrder.EventEndDate,
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
                    var tracked = await db.Orders.FindAsync(CurrentOrder.Id);
                    if (tracked != null)
                    {
                        tracked.BudgetNumber = CurrentOrder.BudgetNumber;
                        tracked.AdminName = CurrentOrder.AdminName;
                        // No pisar al creador original al editar una orden ajena.
                        tracked.CreatedByUserId ??= CurrentOrder.CreatedByUserId;
                        tracked.ClientId = clientId;
                        tracked.LocationId = locationId;
                        tracked.EventDate = CurrentOrder.EventDate;
                        tracked.EventEndDate = CurrentOrder.EventEndDate;
                        tracked.Status = CurrentOrder.Status;
                    }

                    var oldItems = await db.OrderItems.Where(i => i.OrderId == CurrentOrder.Id).ToListAsync();
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

                await tx.CommitAsync();
                AppLog.Information("Order persisted: {OrderId} ({Budget})", CurrentOrder.Id, CurrentOrder.BudgetNumber);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "PersistOrderAsync failed for order {OrderId}", CurrentOrder.Id);
                return false;
            }
        }

        /// <summary>
        /// Returns the Id of a Client row guaranteed to exist in the DB for the current order:
        /// the tracked client if already persisted, an existing client with the same CUIT,
        /// or a newly inserted row built from the manually typed data.
        /// </summary>
        private async Task<Guid> ResolveClientIdAsync(AlquitelDbContext db)
        {
            var client = CurrentOrder.Client ?? new Client();

            if (client.Id != Guid.Empty &&
                await db.Clients.IgnoreQueryFilters().AnyAsync(c => c.Id == client.Id))
                return client.Id;

            if (!string.IsNullOrWhiteSpace(client.Cuit))
            {
                var cuit = client.Cuit.Trim();
                var byCuit = await db.Clients.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Cuit == cuit);
                if (byCuit != null) return byCuit.Id;
            }

            var newClient = new Client
            {
                Id = client.Id == Guid.Empty ? Guid.NewGuid() : client.Id,
                CompanyName = client.CompanyName?.Trim() ?? string.Empty,
                Cuit = client.Cuit?.Trim() ?? string.Empty,
                ContactName = client.ContactName,
                Phone = client.Phone,
                Email = client.Email,
            };
            db.Clients.Add(newClient);
            await db.SaveChangesAsync();
            AppLog.Information("Client auto-created from budget: {Company} ({Cuit})", newClient.CompanyName, newClient.Cuit);
            return newClient.Id;
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
            if (CurrentOrder.EventDate.HasValue && CurrentOrder.EventEndDate.HasValue &&
                CurrentOrder.EventEndDate.Value.Date < CurrentOrder.EventDate.Value.Date)
                errors.Add("Fecha del evento: la fecha de fin no puede ser anterior a la de inicio.");
            if (EventDays < 1) errors.Add("Días: debe ser mayor o igual a 1.");
            if (!SelectedItems.Any()) errors.Add("Productos: agregá al menos un producto.");
            if (!errors.Any()) { message = string.Empty; return true; }
            message = "No se puede generar el documento. Revisá estos campos:\n\n- " + string.Join("\n- ", errors);
            return false;
        }

        // Items actualmente suscriptos a PropertyChanged. Clear() dispara Reset SIN
        // OldItems, así que confiar solo en e.OldItems dejaba handlers colgados (leak
        // y recálculos fantasma). Se resincroniza la suscripción completa en cada cambio.
        private readonly List<OrderItem> _subscribedItems = new();

        private void OnSelectedItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            foreach (var item in _subscribedItems) item.PropertyChanged -= OnSelectedItemPropertyChanged;
            _subscribedItems.Clear();
            foreach (var item in SelectedItems)
            {
                item.PropertyChanged += OnSelectedItemPropertyChanged;
                _subscribedItems.Add(item);
            }
            SelectionVersion++;
            OnPropertyChanged(nameof(FinalBudget));
            _ = RefreshStockConflictsAsync();
        }

        private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(OrderItem.Quantity) or nameof(OrderItem.Dias) or nameof(OrderItem.UnitPrice) or nameof(OrderItem.Total))
            {
                SelectionVersion++;
                OnPropertyChanged(nameof(FinalBudget));
                if (e.PropertyName is nameof(OrderItem.Quantity) or nameof(OrderItem.Dias))
                    _ = RefreshStockConflictsAsync();
            }
        }

        #region Smart Search Engine

        private IEnumerable<SmartMatchResult> FindProductsFromParagraph(string paragraph)
        {
            var segments = BuildSmartSegments(paragraph);
            var aggregated = new Dictionary<Guid, SmartMatchResult>();
            var threshold = _appSettings.SmartSearchThreshold;
            var margin = _appSettings.SmartSearchMargin;
            var stopWords = new HashSet<string>(_appSettings.SmartSearchStopWords, StringComparer.OrdinalIgnoreCase);

            foreach (var segment in segments)
            {
                int quantity = ExtractQuantityFromSegment(segment);

                // Normalize the segment once — previously tokens, trigrams and the
                // stop-word set were rebuilt for every product on every segment.
                string ns = NormalizeText(segment);
                var st = ExtractMeaningfulTokens(ns, stopWords).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!st.Any()) continue;
                var sTri = Trigrams(ns);

                var ranked = AvailableProducts
                    .Select(product => new SmartMatchResult(product, quantity, ScoreProductAgainstSegment(ns, st, sTri, product)))
                    .OrderByDescending(x => x.Score).ToList();
                if (!ranked.Any()) continue;
                var best = ranked[0];
                var second = ranked.Count > 1 ? ranked[1] : null;
                if (best.Score < threshold) continue;
                if (second != null && Math.Abs(best.Score - second.Score) < margin) continue;
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

        private double ScoreProductAgainstSegment(string normalizedSegment, HashSet<string> segmentTokens, HashSet<string> segmentTrigrams, Product product)
        {
            if (!_productSearchCache.TryGetValue(product.Id, out var cache)) return 0;

            var pt = cache.DescriptionTokens;
            var ct = cache.CategoryTokens;

            if (!pt.Any()) return 0;

            int overlap = segmentTokens.Intersect(pt, StringComparer.OrdinalIgnoreCase).Count();
            int catOverlap = segmentTokens.Intersect(ct, StringComparer.OrdinalIgnoreCase).Count();
            double coverage = (double)overlap / pt.Count;
            double precision = (double)overlap / Math.Max(1, segmentTokens.Count);
            double tri = DiceCoefficient(segmentTrigrams, cache.DescriptionTrigrams);

            double score = overlap * 2.7 + catOverlap * 0.8 + coverage * 3.5 + precision * 1.5 + tri * 4.0;

            if (normalizedSegment.Contains(cache.NormalizedDescription, StringComparison.OrdinalIgnoreCase)) score += 3.0;

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
