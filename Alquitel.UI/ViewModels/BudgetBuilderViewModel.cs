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
using Alquitel.Core.Search;

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
        private readonly IAiOrderParser _aiOrderParser;
        private readonly IOrderPersistenceService _orderPersistence;
        private readonly IDraftService _draftService;
        private readonly IToastService _toastService;
        private readonly IEmailService _emailService;
        private readonly IOrderAuditService _auditService;
        private readonly IAiTextAssistant _aiAssistant;
        private readonly IOrderOutboxService _orderOutbox;
        private readonly IApprovalLinkService _approvalLinkService;

        [ObservableProperty]
        private string _searchText = string.Empty;

        // Término y cantidad efectivos del buscador: "3*proyector" filtra por "proyector"
        // y Enter agrega 3 unidades (ver SearchQueryParser en Core).
        private string _searchTerm = string.Empty;
        private int _searchQuantity = 1;

        /// <summary>Estado del autosave para la barra superior ("Borrador guardado 14:32").</summary>
        [ObservableProperty]
        private string _autosaveStatus = string.Empty;

        [ObservableProperty]
        private bool _isSmartSearchVisible;

        [ObservableProperty]
        private string _smartSearchText = string.Empty;

        [ObservableProperty]
        private string _cuitInput = string.Empty;

        // ── Validación inline (feedback al pie del campo, no modal) ──
        /// <summary>"✓ CUIT válido" o el motivo del error; vacío mientras se tipea.</summary>
        [ObservableProperty]
        private string _cuitFeedback = string.Empty;

        /// <summary>True cuando el feedback del CUIT es un error (pinta en rojo).</summary>
        [ObservableProperty]
        private bool _cuitFeedbackIsError;

        /// <summary>Error de coherencia de fechas (fin &lt; inicio); vacío si está todo bien.</summary>
        [ObservableProperty]
        private string _dateRangeError = string.Empty;

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

        /// <summary>True mientras la IA analiza el texto del pedido automático.</summary>
        [ObservableProperty]
        private bool _isAiParsing;

        /// <summary>
        /// Rail derecho colapsado a un resumen mínimo (total + generar). Útil en
        /// ventanas angostas (~1000 px) donde el armador queda apretado.
        /// </summary>
        [ObservableProperty]
        private bool _isRailCollapsed;

        [RelayCommand]
        private void ToggleRail() => IsRailCollapsed = !IsRailCollapsed;

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

        // ── Motor comercial: proxies con notificación (Order no implementa INPC) ──
        /// <summary>Descuento global % (0-100) del presupuesto actual.</summary>
        public decimal DiscountPercent
        {
            get => CurrentOrder.DiscountPercent;
            set
            {
                CurrentOrder.DiscountPercent = Math.Clamp(value, 0m, 100m);
                OnPropertyChanged();
                NotifyCommercialTotals();
            }
        }

        /// <summary>Descuento global en monto fijo del presupuesto actual.</summary>
        public decimal DiscountAmount
        {
            get => CurrentOrder.DiscountAmount;
            set
            {
                CurrentOrder.DiscountAmount = Math.Max(0m, value);
                OnPropertyChanged();
                NotifyCommercialTotals();
            }
        }

        /// <summary>Toggle IVA: true = precios netos + IVA 21% discriminado.</summary>
        public bool AddVat
        {
            get => CurrentOrder.AddVat;
            set
            {
                CurrentOrder.AddVat = value;
                OnPropertyChanged();
                NotifyCommercialTotals();
            }
        }

        public decimal DiscountValue => CurrentOrder.DiscountValue;
        public decimal VatValue => CurrentOrder.VatValue;
        public decimal GrandTotal => CurrentOrder.GrandTotal;
        public bool HasDiscount => CurrentOrder.DiscountValue > 0;

        private void NotifyCommercialTotals()
        {
            OnPropertyChanged(nameof(DiscountValue));
            OnPropertyChanged(nameof(VatValue));
            OnPropertyChanged(nameof(GrandTotal));
            OnPropertyChanged(nameof(HasDiscount));
        }

        /// <summary>Al cambiar la orden raíz (cargar/copiar/recuperar) refrescar los proxies comerciales.</summary>
        partial void OnCurrentOrderChanged(Order value)
        {
            OnPropertyChanged(nameof(DiscountPercent));
            OnPropertyChanged(nameof(DiscountAmount));
            OnPropertyChanged(nameof(AddVat));
            NotifyCommercialTotals();
            RefreshGenerationWarnings();
        }
        public Visibility CommercialColumnsVisibility =>
            IsTechnicalView ? Visibility.Collapsed : Visibility.Visible;

        // Motor de coincidencia del Smart Search (Core, testeable). Se reconstruye en
        // cada InitializeAsync con el snapshot del catálogo y la config vigente.
        private ProductMatcher? _matcher;
        private CancellationTokenSource? _autosaveCts;

        // ── Undo del carrito: snapshots de items antes de cada mutación ──
        private readonly List<List<OrderItem>> _undoSnapshots = new();
        private const int MaxUndoSnapshots = 20;
        private bool _isRestoringUndo;

        // La oferta de recuperar un borrador se hace una sola vez por sesión de la app:
        // repetirla en cada navegación al armador sería intrusivo.
        private static bool _draftRecoveryOffered;

        public BudgetBuilderViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory, IDocumentService documentService, IAppSettings appSettings, IDialogService dialogService, ICurrentUserService currentUserService, Alquitel.Core.Interfaces.Repositories.IOrderRepository orderRepository, ITemplateStorageService templateStorage, IAiOrderParser aiOrderParser, IOrderPersistenceService orderPersistence, IDraftService draftService, IToastService toastService, IEmailService emailService, IOrderAuditService auditService, IAiTextAssistant aiAssistant, IOrderOutboxService orderOutbox, IApprovalLinkService approvalLinkService)
        {
            _approvalLinkService = approvalLinkService;
            _orderOutbox = orderOutbox;
            _toastService = toastService;
            _emailService = emailService;
            _auditService = auditService;
            _aiAssistant = aiAssistant;
            _dbContextFactory = dbContextFactory;
            _documentService = documentService;
            _appSettings = appSettings;
            _dialogService = dialogService;
            _currentUserService = currentUserService;
            _orderRepository = orderRepository;
            _templateStorage = templateStorage;
            _aiOrderParser = aiOrderParser;
            _orderPersistence = orderPersistence;
            _draftService = draftService;

            _productsView = CollectionViewSource.GetDefaultView(AvailableProducts);
            _productsView.Filter = FilterProduct;

            SelectedItems.CollectionChanged += OnSelectedItemsCollectionChanged;
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

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var products = await db.Products.AsNoTracking().ToListAsync();

                foreach (var p in products) AvailableProducts.Add(p);

                _matcher = new ProductMatcher(products, _appSettings.SmartSearchStopWords,
                    _appSettings.SmartSearchThreshold, _appSettings.SmartSearchMargin);

                // Directorio de clientes para el buscador por nombre.
                var clients = await db.Clients.AsNoTracking().OrderBy(c => c.CompanyName).ToListAsync();
                KnownClients.Clear();
                foreach (var c in clients) KnownClients.Add(c);

                // Combos de evento: carritos predefinidos ("Casamiento clásico", etc.).
                var combos = await db.EventTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
                EventCombos.Clear();
                foreach (var t in combos) EventCombos.Add(t);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Failed to load products in BudgetBuilder");
            }

            // Recuperación de borradores: si la app se cerró con un pedido a medias,
            // el autosave quedó en disco y acá se ofrece retomarlo.
            await OfferDraftRecoveryAsync();

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
                        await _draftService.SaveDraftAsync(CurrentOrder, SelectedItems.ToList(), token);
                        // Feedback visible: el operador sabe que su pedido a medias está a salvo.
                        AutosaveStatus = $"Borrador guardado {DateTime.Now:HH:mm}";
                    }
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    AppLog.Warning(ex, "Autosave failed");
                }
            }
        }

        /// <summary>
        /// Ofrece recuperar el borrador más reciente (menos de 3 días) si el armador
        /// está vacío. Solo una vez por sesión de la aplicación.
        /// </summary>
        private async Task OfferDraftRecoveryAsync()
        {
            if (_draftRecoveryOffered || SelectedItems.Any()) return;
            _draftRecoveryOffered = true;

            var recent = _draftService.GetRecentDrafts(TimeSpan.FromDays(3));
            if (recent.Count == 0) return;

            var newest = recent[0];
            var draft = await _draftService.LoadDraftAsync(newest.FilePath);
            if (draft == null || draft.Items.Count == 0) return;

            var age = DateTime.Now - newest.LastWriteLocal;
            string ageText = age.TotalMinutes < 60
                ? $"hace {Math.Max(1, (int)age.TotalMinutes)} min"
                : age.TotalHours < 24
                    ? $"hace {(int)age.TotalHours} h"
                    : $"hace {(int)age.TotalDays} día(s)";

            bool recover = _dialogService.ShowConfirm("Pedido sin guardar",
                $"Hay un pedido sin guardar ({ageText}): " +
                $"{draft.Items.Count} producto(s) para \"{draft.ClientName ?? "cliente sin nombre"}\".\n\n" +
                "¿Querés recuperarlo?");

            if (!recover)
            {
                // Rechazado: se descarta para no volver a ofrecerlo en cada arranque.
                _draftService.DeleteDraftFile(newest.FilePath);
                return;
            }

            ApplyDraft(draft);
        }

        /// <summary>Vuelca un borrador recuperado al estado del armador.</summary>
        private void ApplyDraft(OrderDraft draft)
        {
            _undoSnapshots.Clear();
            UndoCartCommand.NotifyCanExecuteChanged();

            CurrentOrder = new Order
            {
                Id = draft.Id,
                BudgetNumber = draft.BudgetNumber ?? string.Empty,
                AdminName = draft.AdminName ?? string.Empty,
                CreatedByUserId = draft.CreatedByUserId,
                CreatedDate = draft.CreatedDate,
                EventDate = draft.EventDate,
                EventEndDate = draft.EventEndDate,
                Comments = draft.Comments,
                Status = Enum.TryParse<OrderStatus>(draft.Status, out var st) ? st : OrderStatus.Draft,
                DiscountPercent = draft.DiscountPercent,
                DiscountAmount = draft.DiscountAmount,
                AddVat = draft.AddVat,
                // Drafts viejos sin RowVersion deserializan Guid.Empty: el chequeo de
                // concurrencia se saltea para ellos (comportamiento legado).
                RowVersion = draft.RowVersion,
                Client = new Client { CompanyName = draft.ClientName ?? string.Empty, Cuit = draft.ClientCuit ?? string.Empty },
                Location = new Location { Name = draft.LocationName ?? string.Empty },
            };

            ClearCart();
            foreach (var di in draft.Items)
            {
                // El borrador guarda solo el ProductId: la instancia viva sale del catálogo.
                // Si el producto ya no existe se recupera igual con el snapshot de descripción.
                var product = AvailableProducts.FirstOrDefault(p => p.Id == di.ProductId);
                AddItemToCart(new OrderItem
                {
                    ProductId = di.ProductId,
                    Product = product,
                    Quantity = di.Quantity,
                    Dias = di.Dias,
                    UnitPrice = di.UnitPrice,
                    TechnicalNotes = di.TechnicalNotes,
                    ImagePath = di.ImagePath,
                    CustomFieldsJson = di.CustomFieldsJson,
                    DescriptionSnapshot = di.DescriptionSnapshot,
                    RequestedMeasure = di.RequestedMeasure,
                });
            }

            SetEventDaysWithoutPropagation(draft.Items.FirstOrDefault()?.Dias ?? 1);
            CuitInput = draft.ClientCuit ?? string.Empty;
            OnPropertyChanged(nameof(CurrentOrder));
        }

        public void Dispose()
        {
            _autosaveCts?.Cancel();
            _autosaveCts?.Dispose();
            _stockRefreshCts?.Cancel();
            _stockRefreshCts?.Dispose();
            _aiParseCts?.Cancel();
            _aiParseCts?.Dispose();
        }

        public int GetSelectedQuantity(Guid productId)
        {
            return SelectedItems.Where(i => i.ProductId == productId).Sum(i => i.Quantity);
        }

        partial void OnSearchTextChanged(string value)
        {
            (_searchQuantity, _searchTerm) = SearchQueryParser.Parse(value);
            _productsView.Refresh();
        }

        private bool FilterProduct(object item)
        {
            if (item is not Product product) return false;
            if (string.IsNullOrWhiteSpace(_searchTerm)) return true;
            return product.Description.Contains(_searchTerm, StringComparison.OrdinalIgnoreCase)
                || product.Category.Contains(_searchTerm, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Enter en el buscador: agrega al pedido el primer producto visible del filtro,
        /// con la cantidad del prefijo ("3*proyector" = 3 unidades). Flujo 100% teclado.
        /// </summary>
        [RelayCommand]
        private void AddTopSearchMatch()
        {
            if (string.IsNullOrWhiteSpace(_searchTerm)) return;

            var product = _productsView.Cast<Product>().FirstOrDefault();
            if (product == null)
            {
                _toastService.ShowInfo($"Ningún equipo coincide con \"{_searchTerm}\".");
                return;
            }

            PushUndoSnapshot();
            var existing = SelectedItems.FirstOrDefault(i => i.ProductId == product.Id);
            if (existing != null)
            {
                existing.Quantity += _searchQuantity;
                existing.Dias = EventDays;
            }
            else
            {
                AddItemToCart(new OrderItem
                {
                    ProductId = product.Id,
                    Product = product,
                    Quantity = _searchQuantity,
                    Dias = EventDays,
                    UnitPrice = product.BasePrice,
                    ImagePath = product.ImagePath,
                    CustomFieldsJson = product.CustomFieldsJson,
                    DescriptionSnapshot = product.Description,
                    RequestedMeasure = string.Empty,
                });
            }

            var name = Alquitel.Core.Parsing.TagParser.StripTags(product.Description) ?? product.Description;
            _toastService.ShowSuccess($"{_searchQuantity} × {Truncate(name, 40)} agregado al pedido.");
            SearchText = string.Empty; // listo para el siguiente equipo, sin tocar el mouse
        }

        private static string Truncate(string text, int max) =>
            text.Length <= max ? text : text[..max] + "…";

        partial void OnCuitInputChanged(string value)
        {
            RefreshCuitFeedback(value);
            RefreshGenerationWarnings();

            // Only hit the DB once the input is a complete, checksum-valid CUIT.
            // A synchronous query per keystroke blocked the UI thread while typing.
            if (!CuitValidator.IsValid(value)) return;
            _ = LookupClientByCuitAsync(value);
        }

        /// <summary>
        /// Feedback inline del CUIT: ✓ apenas es válido, ✗ solo cuando el número está
        /// completo (11 dígitos) y falla Módulo 11 — no se marca error mientras se tipea.
        /// </summary>
        private void RefreshCuitFeedback(string value)
        {
            var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digits.Length == 0)
            {
                CuitFeedback = string.Empty;
                CuitFeedbackIsError = false;
            }
            else if (CuitValidator.IsValid(value))
            {
                CuitFeedback = "✓ CUIT válido (verificación AFIP)";
                CuitFeedbackIsError = false;
            }
            else if (digits.Length >= 11)
            {
                CuitFeedback = "✗ El CUIT no supera la verificación de AFIP (Módulo 11).";
                CuitFeedbackIsError = true;
            }
            else
            {
                CuitFeedback = string.Empty;
                CuitFeedbackIsError = false;
            }
        }

        /// <summary>
        /// Recalcula el error de rango de fechas. La vista lo invoca al cambiar cualquiera
        /// de los DatePickers (Order no implementa INotifyPropertyChanged).
        /// </summary>
        public void RefreshDateValidation()
        {
            DateRangeError = CurrentOrder.EventDate is DateTime s &&
                             CurrentOrder.EventEndDate is DateTime e &&
                             e.Date < s.Date
                ? "✗ La fecha de fin es anterior a la de inicio."
                : string.Empty;
            RefreshGenerationWarnings();
        }

        // ── Advertencias pre-generación (no bloqueantes) ─────────────

        /// <summary>
        /// Avisos suaves visibles sobre los botones Generar: cosas que NO bloquean la
        /// generación pero que suelen terminar en un documento rehecho. Vacío = todo bien.
        /// </summary>
        [ObservableProperty]
        private string _generationWarnings = string.Empty;

        /// <summary>Recalcula los avisos. La vista también lo invoca al editar el lugar.</summary>
        public void RefreshGenerationWarnings()
        {
            var warnings = new List<string>();

            if (CurrentOrder.EventDate is DateTime ev && ev.Date < DateTime.Today)
                warnings.Add("La fecha del evento ya pasó.");
            if (SelectedItems.Any() && string.IsNullOrWhiteSpace(CurrentOrder.Location?.Name))
                warnings.Add("Falta el lugar del evento.");
            if (SelectedItems.Any() && string.IsNullOrWhiteSpace(CurrentOrder.Client?.Cuit))
                warnings.Add("El cliente no tiene CUIT cargado.");

            int stockConflicts = SelectedItems.Count(i => i.HasStockConflict);
            if (stockConflicts > 0)
                warnings.Add($"{stockConflicts} producto(s) con posible conflicto de stock.");

            GenerationWarnings = warnings.Count == 0
                ? string.Empty
                : string.Join("\n", warnings.Select(w => "⚠ " + w));
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

            // Precio especial por cliente: si la ficha tiene un % acordado y el
            // presupuesto todavía no tiene descuento, se aplica como default editable.
            if (value.SpecialDiscountPercent is decimal special && special > 0 && DiscountPercent == 0)
            {
                DiscountPercent = special;
                _toastService.ShowInfo($"Descuento acordado con {value.CompanyName}: {special:0.##}% aplicado (editable en Totales).");
            }

            _ = LoadClientInsightAsync(value);
        }

        // ── Ficha rápida del cliente (memoria comercial) ─────────────

        /// <summary>True cuando hay ficha para mostrar (cliente registrado seleccionado).</summary>
        [ObservableProperty]
        private bool _isClientInsightVisible;

        /// <summary>Últimos pedidos del cliente, una línea por orden.</summary>
        [ObservableProperty]
        private string _clientRecentOrders = string.Empty;

        /// <summary>Badge "cliente frecuente": 3+ órdenes en los últimos 12 meses.</summary>
        [ObservableProperty]
        private bool _isClientFrequent;

        /// <summary>Notas internas de la ficha (nunca salen en documentos).</summary>
        [ObservableProperty]
        private string _clientNotes = string.Empty;

        /// <summary>
        /// Carga la memoria comercial del cliente seleccionado: últimas 3 órdenes con
        /// total, y marca de frecuencia. Es informativa: cualquier error solo la oculta.
        /// </summary>
        private async Task LoadClientInsightAsync(Client client)
        {
            IsClientInsightVisible = false;
            if (client.Id == Guid.Empty) return;

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var recent = await db.Orders.AsNoTracking().IgnoreQueryFilters()
                    .Where(o => o.ClientId == client.Id)
                    .OrderByDescending(o => o.CreatedDate)
                    .Take(3)
                    .Select(o => new { o.BudgetNumber, o.CreatedDate, o.Status })
                    .ToListAsync();

                var yearAgo = DateTime.UtcNow.AddMonths(-12);
                var yearCount = await db.Orders.AsNoTracking().IgnoreQueryFilters()
                    .CountAsync(o => o.ClientId == client.Id && o.CreatedDate >= yearAgo);

                // Si mientras tanto se eligió otro cliente, este resultado ya no aplica.
                if (SelectedClient?.Id != client.Id) return;

                IsClientFrequent = yearCount >= 3;
                ClientNotes = client.InternalNotes ?? string.Empty;
                ClientRecentOrders = recent.Count == 0
                    ? "Primer pedido de este cliente."
                    : string.Join("\n", recent.Select(o =>
                        $"N° {(string.IsNullOrWhiteSpace(o.BudgetNumber) ? "s/n" : o.BudgetNumber)} · " +
                        $"{o.CreatedDate.ToLocalTime():dd/MM/yyyy} · {StatusLabel(o.Status)}"));
                IsClientInsightVisible = true;
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "LoadClientInsightAsync failed for {Client}", client.CompanyName);
            }
        }

        private static string StatusLabel(OrderStatus status) => status switch
        {
            OrderStatus.Draft => "Borrador",
            OrderStatus.Approved => "Aprobado",
            OrderStatus.SentToOF => "Enviado a OF",
            OrderStatus.SentToOT => "Enviado a OT",
            OrderStatus.Rejected => "Rechazado",
            OrderStatus.Archived => "Archivado",
            _ => status.ToString(),
        };

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

            // Deshacer a un clic (además de Ctrl+Z): borrar por error no cuesta rearmar.
            var name = Alquitel.Core.Parsing.TagParser.StripTags(
                item.DescriptionSnapshot ?? item.Product?.Description) ?? "Ítem";
            _toastService.ShowSuccess($"{Truncate(name, 40)} quitado del pedido.", "Deshacer",
                () => { if (UndoCartCommand.CanExecute(null)) UndoCartCommand.Execute(null); });
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
        private void EmptyCart()
        {
            if (!SelectedItems.Any()) return;
            if (!_dialogService.ShowConfirm("Vaciar pedido",
                "¿Quitar todos los productos del pedido actual?\n\nPodés recuperarlos con Deshacer (Ctrl+Z)."))
                return;
            PushUndoSnapshot();
            ClearCart();
        }

        [RelayCommand]
        private void ToggleSmartSearchInput() => IsSmartSearchVisible = !IsSmartSearchVisible;

        // CTS del análisis con IA en curso (pedido automático). Ver CancelAiParse.
        private CancellationTokenSource? _aiParseCts;

        /// <summary>Corta el análisis con IA en curso; el flujo cae al motor local.</summary>
        [RelayCommand]
        private void CancelAiParse() => _aiParseCts?.Cancel();

        // ── Escalabilidad del pedido automático con IA ──────────────────
        // Con catálogos grandes no se manda todo a la IA: se preseleccionan candidatos
        // con el motor local (tokens + trigramas) y la IA solo desambigua entre ellos.
        // Mantiene el prompt chico (~15 tokens por producto) sin importar cuánto crezca
        // el catálogo, y de paso mejora la precisión al reducir ruido.

        /// <summary>Hasta este tamaño el catálogo completo entra al prompt sin preselección.</summary>
        private const int AiFullCatalogLimit = 60;
        /// <summary>Candidatos que aporta cada segmento del texto del cliente.</summary>
        private const int AiCandidatesPerSegment = 8;
        /// <summary>Techo absoluto de productos enviados a la IA.</summary>
        private const int AiMaxCatalogProducts = 80;
        /// <summary>Largo máximo de descripción enviada a la IA (economía de tokens).</summary>
        private const int AiMaxDescriptionLength = 140;

        /// <summary>
        /// Preselecciona los productos del catálogo relevantes para el texto del cliente
        /// (retrieval delegado a <see cref="ProductMatcher.SelectAiCandidates"/>).
        /// </summary>
        private List<Product> BuildAiCandidateProducts(string customerText)
        {
            if (_matcher == null) return AvailableProducts.ToList();
            return _matcher.SelectAiCandidates(customerText,
                AiFullCatalogLimit, AiCandidatesPerSegment, AiMaxCatalogProducts).ToList();
        }

        [RelayCommand]
        private async Task ParseSmartSearchAsync()
        {
            if (string.IsNullOrWhiteSpace(SmartSearchText))
            {
                _toastService.ShowInfo("Pegá un texto para analizar primero.");
                return;
            }

            // Productos resueltos (por IA o por el motor local) con cantidad y medida.
            var resolved = new List<(Product Product, int Quantity, string? Measure)>();
            var unmatched = new List<string>();
            int? aiDays = null;
            bool aiSucceeded = false;

            if (_aiOrderParser.IsConfigured)
            {
                // Cancelable: el peor caso de la IA son ~90 s (dos modelos con timeout
                // de 45 s); el usuario puede cortar y caer al motor local al instante.
                _aiParseCts?.Cancel();
                _aiParseCts = new CancellationTokenSource();
                var aiToken = _aiParseCts.Token;

                IsAiParsing = true;
                try
                {
                    // Lista de candidatos preseleccionados localmente: el índice "ref"
                    // que ve la IA apunta a esta lista (snapshot, inmune a cambios
                    // de la colección durante el await).
                    var products = BuildAiCandidateProducts(SmartSearchText);
                    var catalog = products
                        .Select((p, i) =>
                        {
                            var desc = Alquitel.Core.Parsing.TagParser.StripTags(p.Description) ?? p.Description;
                            if (desc.Length > AiMaxDescriptionLength)
                                desc = desc[..AiMaxDescriptionLength] + "…";
                            return new AiCatalogProduct(i, desc, p.Category);
                        })
                        .ToList();

                    var result = await _aiOrderParser.ParseOrderAsync(SmartSearchText, catalog, aiToken);
                    if (result != null)
                    {
                        foreach (var item in result.Items)
                        {
                            if (item.Ref < 0 || item.Ref >= products.Count) continue;
                            resolved.Add((products[item.Ref], item.Quantity, item.RequestedMeasure));
                        }
                        aiDays = result.Days;
                        unmatched.AddRange(result.Unmatched);
                        aiSucceeded = resolved.Count > 0 || unmatched.Count > 0;
                    }
                }
                catch (OperationCanceledException)
                {
                    _toastService.ShowInfo("Análisis con IA cancelado — se usa el motor local.");
                }
                catch (Exception ex)
                {
                    AppLog.Warning(ex, "Pedido automático: la IA falló — se usa el motor local");
                }
                finally
                {
                    IsAiParsing = false;
                }
            }

            if (!aiSucceeded && _matcher != null)
            {
                var matches = _matcher.FindMatches(SmartSearchText);
                resolved.AddRange(matches
                    .OrderByDescending(m => m.Score)
                    .Select(m => (m.Product, m.Quantity, (string?)null)));
            }

            if (!resolved.Any())
            {
                _dialogService.ShowWarning("Pedido automático",
                    unmatched.Any()
                        ? "Ningún pedido coincide con el catálogo:\n\n- " + string.Join("\n- ", unmatched)
                        : "No detecté productos del catálogo en ese texto.");
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

            // Si la IA detectó la duración ("por 3 días"), se aplica a todo el pedido.
            if (aiDays is int days) EventDays = days;

            int addedCount = 0;
            foreach (var (product, quantity, measure) in resolved)
            {
                if (SelectedItems.Any(i => i.ProductId == product.Id)) continue;
                var item = new OrderItem
                {
                    ProductId = product.Id,
                    Product = product,
                    Quantity = quantity,
                    Dias = EventDays,
                    UnitPrice = product.BasePrice,
                    ImagePath = product.ImagePath,
                    CustomFieldsJson = product.CustomFieldsJson,
                    DescriptionSnapshot = product.Description,
                    RequestedMeasure = measure ?? string.Empty
                };
                AddItemToCart(item);
                addedCount++;
            }

            // Quick-win IA: en el mismo texto suele venir la firma del cliente
            // (empresa, contacto, teléfono) — completar los campos vacíos en background.
            _ = DetectClientDataFromTextAsync(SmartSearchText);

            var summary = new StringBuilder();
            summary.Append($"Se agregaron {addedCount} producto(s)");
            summary.Append(aiSucceeded ? " con IA." : " con el motor local.");
            if (aiDays is int d) summary.Append($"\nDías detectados: {d}.");
            if (unmatched.Any())
            {
                // Hay renglones sin coincidencia: eso requiere atención, se mantiene el modal.
                summary.Append("\n\nPedidos sin coincidencia en el catálogo:\n- ");
                summary.Append(string.Join("\n- ", unmatched));
                _dialogService.ShowInfo("Pedido automático", summary.ToString());
            }
            else
            {
                _toastService.ShowSuccess(summary.ToString());
            }
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

                // Pre-chequeo de numeración: si otro usuario ya usó este número desde que
                // se asignó acá, renumerar ANTES de generar el documento (así el .docx
                // sale con el número definitivo). La colisión residual dentro de la
                // ventana de segundos la cubre el retry de OrderPersistenceService.
                await EnsureBudgetNumberAvailableAsync();

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

                var generationResult = await _documentService.GenerateDocumentAsync(
                    CurrentOrder, effectiveTemplate, outputPath, isTechnical, _appSettings.ExportPdf);
                outputPath = generationResult.DocumentPath;
                fileName = Path.GetFileName(outputPath);
                if (generationResult.Warnings.Count > 0)
                {
                    _dialogService.ShowWarning(
                        "Documento generado con advertencias",
                        string.Join("\n", generationResult.Warnings.Distinct()));
                }

                // Firma multi-usuario: la primera persistencia registra quién creó la orden.
                CurrentOrder.CreatedByUserId ??= _currentUserService.Current?.Id;

                string numberBeforePersist = CurrentOrder.BudgetNumber;
                var persistenceOperationId = Guid.NewGuid();
                var persistResult = await _orderPersistence.PersistAsync(
                    CurrentOrder, operationId: persistenceOperationId);

                if (persistResult.Status == OrderPersistStatus.Conflict &&
                    persistResult.Conflict is { } conflict)
                {
                    var fields = conflict.ChangedFields.Count == 0
                        ? "No se pudo determinar qué campos cambiaron."
                        : "Cambios detectados: " + string.Join(", ", conflict.ChangedFields) + ".";
                    var reload = _dialogService.ShowConfirm(
                        "Conflicto de edición",
                        "Otro usuario modificó este presupuesto mientras lo editabas.\n\n" +
                        fields + "\n\n¿Querés recargar la versión vigente? " +
                        "Elegí No para conservar tus cambios locales y decidir si sobrescribir.");
                    if (reload)
                    {
                        ApplyLoadedOrder(conflict.LatestOrder);
                        _toastService.ShowInfo("Se recargó la versión más reciente del presupuesto.");
                        return;
                    }

                    var overwrite = _dialogService.ShowConfirm(
                        "Sobrescribir cambios",
                        "Tus cambios siguen intactos y todavía no se guardaron.\n\n" +
                        "¿Querés sobrescribir conscientemente la versión vigente?");
                    if (!overwrite)
                    {
                        _toastService.ShowInfo("Se conservaron tus cambios locales sin guardar.");
                        return;
                    }

                    persistResult = await _orderPersistence.PersistAsync(
                        CurrentOrder,
                        OrderConflictResolution.OverwriteLatest,
                        persistenceOperationId);
                    if (persistResult.Status == OrderPersistStatus.Conflict)
                    {
                        _dialogService.ShowWarning(
                            "Nuevo conflicto",
                            "La orden volvió a cambiar antes de confirmar la sobrescritura. " +
                            "No se guardó nada; revisá nuevamente la versión vigente.");
                        return;
                    }
                }

                if (persistResult.Status == OrderPersistStatus.Saved)
                {
                    _draftService.DeleteDraft(CurrentOrder.Id);
                    LastGeneratedPath = outputPath;
                    SendByEmailCommand.NotifyCanExecuteChanged();
                    CopyApprovalLinkCommand.NotifyCanExecuteChanged();
                    _ = _auditService.LogAsync(CurrentOrder.Id,
                        $"Documento generado ({templateKind})",
                        generationResult.PdfPath == null ? "DOCX" : "DOCX y PDF");

                    if (!string.Equals(numberBeforePersist, CurrentOrder.BudgetNumber, StringComparison.Ordinal))
                    {
                        // Colisión de numeración con otro usuario: la orden quedó guardada
                        // con un número nuevo, pero el documento ya salió con el viejo.
                        OnPropertyChanged(nameof(CurrentOrder));
                        _dialogService.ShowWarning(
                            "Número de presupuesto renumerado",
                            $"El número {numberBeforePersist} ya fue usado por otro usuario. " +
                            $"La orden se guardó como {CurrentOrder.BudgetNumber}.\n\n" +
                            "El documento generado todavía muestra el número viejo: " +
                            "volvé a generarlo para que salga con el número correcto.");
                    }
                    else
                    {
                        // ArgumentList en vez de una línea de comando armada a mano: el
                        // nombre del archivo lleva la razón social del cliente y una
                        // comilla ahí adentro rompía (o inyectaba) los argumentos.
                        var revealPath = outputPath;
                        _toastService.ShowSuccess($"Documento guardado: {fileName}", "Abrir carpeta",
                            () => Alquitel.UI.Helpers.ShellLauncher.RevealInExplorer(revealPath));
                    }
                }
                else if (persistResult.Status == OrderPersistStatus.Error)
                {
                    if (persistResult.ErrorCode is "invalid_status_transition" or "order_not_found")
                    {
                        _dialogService.ShowWarning(
                            "No se guardó la orden",
                            persistResult.ErrorCode == "order_not_found"
                                ? "La orden ya no existe en la base. Recargá el historial antes de continuar."
                                : "El cambio de estado solicitado no es válido para esta orden.");
                        return;
                    }
                    if (_orderOutbox.Enqueue(CurrentOrder, persistenceOperationId))
                    {
                        _dialogService.ShowWarning(
                            "Documento generado, persistencia falló",
                            $"El documento se generó correctamente:\n{outputPath}\n\n" +
                            "ATENCIÓN: la orden no pudo guardarse en la base de datos. " +
                            "Quedó encolada y se reintentará automáticamente cuando vuelva la conexión.");
                    }
                    else
                    {
                        _dialogService.ShowError(
                            "Orden sin respaldo local",
                            $"El documento se generó correctamente:\n{outputPath}\n\n" +
                            "No se pudo guardar la orden en la base ni asegurarla en la cola local. " +
                            "No cierres esta pantalla y volvé a intentar cuando haya espacio y conexión.");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error(
                    "GenerateDocument failed ({TemplateKind}, {ErrorType}, 0x{HResult:X8})",
                    templateKind,
                    ex.GetType().Name,
                    ex.HResult);
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

        /// <summary>
        /// Verifica que el número del presupuesto actual no esté usado por OTRA orden
        /// (multi-usuario: alguien pudo tomarlo desde que se asignó). Si está tomado,
        /// renumera y avisa por toast.
        /// </summary>
        private async Task EnsureBudgetNumberAvailableAsync()
        {
            if (string.IsNullOrWhiteSpace(CurrentOrder.BudgetNumber)) return;
            try
            {
                var existing = await _orderRepository.GetByBudgetNumberAsync(CurrentOrder.BudgetNumber);
                if (existing == null || existing.Id == CurrentOrder.Id) return;

                var numbers = await _orderRepository.GetAllBudgetNumbersAsync();
                var oldNumber = CurrentOrder.BudgetNumber;
                CurrentOrder.BudgetNumber = BudgetNumberHelper.VersionPart(oldNumber) > 1
                    ? BudgetNumberHelper.NextVersion(oldNumber, numbers)
                    : BudgetNumberHelper.NextSerial(numbers);
                OnPropertyChanged(nameof(CurrentOrder));
                _toastService.ShowInfo(
                    $"El número {oldNumber} ya fue usado por otro usuario: este presupuesto pasa a ser el {CurrentOrder.BudgetNumber}.");
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "EnsureBudgetNumberAvailableAsync failed");
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
                CurrentOrder.RowVersion = Guid.NewGuid();
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
                _toastService.ShowSuccess(
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
            CurrentOrder.RowVersion = Guid.NewGuid();
            CurrentOrder.BudgetNumber = string.Empty;
            CurrentOrder.CreatedDate = DateTime.UtcNow;
            CurrentOrder.EventDate = null; // el evento nuevo tendrá otra fecha; forzar selección
            CurrentOrder.EventEndDate = null;
            CurrentOrder.Status = OrderStatus.Draft;
            CurrentOrder.Comments = null; // los comentarios son propios de cada evento

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

            // El pedido repetido cotiza a precios de HOY, no a los congelados del
            // histórico. Se avisa qué cambió y qué producto ya no está en catálogo.
            RefreshCopiedPricesFromCatalog();

            // Order no implementa INotifyPropertyChanged: re-notificar la raíz refresca los bindings.
            OnPropertyChanged(nameof(CurrentOrder));

            // La copia arranca con el próximo número de la serie.
            await AssignNextSerialIfEmptyAsync();
        }

        /// <summary>
        /// Actualiza el precio unitario de cada ítem copiado al precio vigente del
        /// catálogo y detecta productos archivados. Avisa por toast si algo cambió.
        /// </summary>
        private void RefreshCopiedPricesFromCatalog()
        {
            int priceChanges = 0;
            var archived = new List<string>();

            foreach (var item in SelectedItems)
            {
                if (item.Product == null) continue;

                if (item.Product.IsArchived)
                {
                    archived.Add(Alquitel.Core.Parsing.TagParser.StripTags(item.Product.Description)
                                 ?? item.Product.Description);
                    continue;
                }

                if (item.UnitPrice != item.Product.BasePrice)
                {
                    item.UnitPrice = item.Product.BasePrice;
                    priceChanges++;
                }
            }

            if (priceChanges > 0)
                _toastService.ShowInfo(
                    $"{priceChanges} precio(s) actualizados al valor vigente del catálogo (el original quedaba viejo).");
            if (archived.Count > 0)
                _dialogService.ShowWarning("Productos fuera de catálogo",
                    "Estos productos del pedido original ya no están en el catálogo " +
                    "(se copiaron con su precio histórico — revisalos o reemplazalos):\n\n- " +
                    string.Join("\n- ", archived.Select(a => Truncate(a, 60))));
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
                Comments = full.Comments,
                DiscountPercent = full.DiscountPercent,
                DiscountAmount = full.DiscountAmount,
                AddVat = full.AddVat,
                RowVersion = full.RowVersion,
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

                // Fin del rango pedido: el mayor entre EventDate + Dias y EventEndDate + 1
                // (mismo criterio que EfOrderRepository al computar lo comprometido).
                var end = start.AddDays(Math.Max(1, group.Max(i => i.Dias)));
                if (CurrentOrder.EventEndDate is DateTime evEnd && evEnd.Date >= start)
                {
                    var byEndDate = evEnd.Date.AddDays(1);
                    if (byEndDate > end) end = byEndDate;
                }
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

        // ── Quick-wins IA ────────────────────────────────────────────

        /// <summary>True mientras la IA redacta las notas técnicas sugeridas.</summary>
        [ObservableProperty]
        private bool _isSuggestingNotes;

        /// <summary>
        /// Autocompleta las notas técnicas de la OT para los ítems que no tienen nota,
        /// a partir de la descripción y campos de cada producto. Nunca pisa notas escritas.
        /// </summary>
        [RelayCommand]
        private async Task SuggestTechnicalNotesAsync()
        {
            if (!_aiAssistant.IsConfigured)
            {
                _toastService.ShowInfo("La IA no está configurada (falta la API key de Pollinations).");
                return;
            }
            var pending = SelectedItems
                .Select((item, idx) => (item, idx))
                .Where(x => string.IsNullOrWhiteSpace(x.item.TechnicalNotes))
                .ToList();
            if (pending.Count == 0)
            {
                _toastService.ShowInfo("Todos los ítems ya tienen notas técnicas.");
                return;
            }

            IsSuggestingNotes = true;
            try
            {
                var sb = new StringBuilder();
                foreach (var (item, idx) in pending)
                {
                    var desc = Alquitel.Core.Parsing.TagParser.StripTags(
                        item.DescriptionSnapshot ?? item.Product?.Description) ?? "Equipo";
                    sb.AppendLine($"{idx} | {item.Quantity}x {desc} | {item.Product?.Category ?? ""}");
                }

                const string system =
                    "Sos el jefe técnico de una empresa argentina de alquiler de equipamiento audiovisual. " +
                    "Para cada ítem numerado escribí UNA nota técnica breve (máx. 15 palabras) para la orden de " +
                    "trabajo del armador de depósito: cableado, montaje, accesorios a no olvidar. " +
                    "Respondé SOLO JSON: {\"notas\":[{\"idx\":0,\"nota\":\"...\"}]}";

                var json = await _aiAssistant.CompleteToJsonAsync(system, sb.ToString());
                if (json == null)
                {
                    _toastService.ShowInfo("La IA no devolvió sugerencias. Reintentá en unos segundos.");
                    return;
                }

                int applied = 0;
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("notas", out var notas))
                {
                    foreach (var n in notas.EnumerateArray())
                    {
                        if (!n.TryGetProperty("idx", out var idxEl) || !n.TryGetProperty("nota", out var notaEl)) continue;
                        int idx = idxEl.GetInt32();
                        string? nota = notaEl.GetString();
                        if (string.IsNullOrWhiteSpace(nota)) continue;
                        if (idx < 0 || idx >= SelectedItems.Count) continue;
                        if (!string.IsNullOrWhiteSpace(SelectedItems[idx].TechnicalNotes)) continue;
                        SelectedItems[idx].TechnicalNotes = nota.Trim();
                        applied++;
                    }
                }

                SelectionVersion++; // refrescar las tarjetas con la nota nueva
                _toastService.ShowSuccess(applied > 0
                    ? $"Notas técnicas sugeridas para {applied} ítem(s). Revisalas antes de generar la OT."
                    : "La IA no sugirió notas aplicables.");
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "SuggestTechnicalNotesAsync failed");
                _toastService.ShowInfo("No se pudieron sugerir notas técnicas.");
            }
            finally
            {
                IsSuggestingNotes = false;
            }
        }

        /// <summary>
        /// Busca datos del cliente (empresa, contacto, teléfono, email, CUIT) en el mismo
        /// texto pegado del pedido automático y completa SOLO los campos vacíos del formulario.
        /// </summary>
        private async Task DetectClientDataFromTextAsync(string customerText)
        {
            if (!_aiAssistant.IsConfigured) return;
            var client = CurrentOrder.Client ??= new Client();

            // Con la ficha ya completa no hay nada que detectar.
            if (!string.IsNullOrWhiteSpace(client.CompanyName) &&
                !string.IsNullOrWhiteSpace(client.ContactName) &&
                !string.IsNullOrWhiteSpace(client.Phone)) return;

            try
            {
                const string system =
                    "Extraé los datos del REMITENTE/cliente de este texto de pedido (mail o WhatsApp argentino). " +
                    "Respondé SOLO JSON: {\"empresa\":null,\"contacto\":null,\"telefono\":null,\"email\":null,\"cuit\":null}. " +
                    "Usá null para todo dato que el texto no mencione explícitamente. Nunca inventes.";

                var json = await _aiAssistant.CompleteToJsonAsync(system, customerText);
                if (json == null) return;

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                string? Get(string prop) =>
                    doc.RootElement.TryGetProperty(prop, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String
                        ? el.GetString()?.Trim()
                        : null;

                var filled = new List<string>();
                if (string.IsNullOrWhiteSpace(client.CompanyName) && Get("empresa") is string emp && emp.Length > 1)
                { client.CompanyName = emp; filled.Add("empresa"); }
                if (string.IsNullOrWhiteSpace(client.ContactName) && Get("contacto") is string con && con.Length > 1)
                { client.ContactName = con; filled.Add("contacto"); }
                if (string.IsNullOrWhiteSpace(client.Phone) && Get("telefono") is string tel && tel.Length > 5)
                { client.Phone = tel; filled.Add("teléfono"); }
                if (string.IsNullOrWhiteSpace(client.Email) && Get("email") is string mail && mail.Contains('@'))
                { client.Email = mail; filled.Add("email"); }
                if (string.IsNullOrWhiteSpace(client.Cuit) && Get("cuit") is string cuit && CuitValidator.IsValid(cuit))
                { client.Cuit = cuit; CuitInput = cuit; filled.Add("CUIT"); }

                if (filled.Count > 0)
                {
                    OnPropertyChanged(nameof(CurrentOrder));
                    _toastService.ShowSuccess($"Datos del cliente detectados en el texto: {string.Join(", ", filled)}.");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "DetectClientDataFromTextAsync failed");
            }
        }

        // ── Envío por correo del último documento generado ──────────

        /// <summary>Ruta del último .docx generado en esta sesión del armador.</summary>
        [ObservableProperty]
        private string? _lastGeneratedPath;

        private bool CanSendByEmail() =>
            !string.IsNullOrEmpty(LastGeneratedPath) && File.Exists(LastGeneratedPath);

        /// <summary>
        /// Abre un borrador de Outlook con el .docx (y el PDF si se exportó) adjuntos,
        /// dirigido al email del cliente si está cargado. No envía: el usuario revisa.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanSendByEmail))]
        private void SendByEmail()
        {
            if (LastGeneratedPath == null) return;
            try
            {
                var attachments = new List<string> { LastGeneratedPath };
                var pdf = Path.ChangeExtension(LastGeneratedPath, ".pdf");
                if (File.Exists(pdf)) attachments.Add(pdf);

                string subject = $"Presupuesto {CurrentOrder.BudgetNumber} - Grupo Alquitel";
                string body =
                    $"Estimado/a,\n\nAdjuntamos el presupuesto {CurrentOrder.BudgetNumber} solicitado.\n\n" +
                    "Quedamos a disposición por cualquier consulta.\n\nSaludos cordiales,\n" +
                    $"{CurrentOrder.AdminName}\nGrupo Alquitel";

                _emailService.CreateDraft(CurrentOrder.Client?.Email, subject, body, attachments);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "SendByEmail failed");
                _dialogService.ShowError("Enviar por mail", ex.Message);
            }
        }

        // ── Link de aprobación por web (§4 PENDING_FEATURES) ─────────

        private bool CanCopyApprovalLink() =>
            _approvalLinkService.IsConfigured && !string.IsNullOrEmpty(LastGeneratedPath);

        /// <summary>
        /// Genera (o reutiliza) el link público de aprobación del presupuesto actual y
        /// lo copia al portapapeles para pegarlo en el mail al cliente.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanCopyApprovalLink))]
        private async Task CopyApprovalLinkAsync()
        {
            try
            {
                var url = await _approvalLinkService.CreateApprovalLinkAsync(CurrentOrder.Id);
                if (url == null)
                {
                    _dialogService.ShowWarning("Link de aprobación",
                        "No se pudo generar el link. Verificá que el presupuesto esté guardado " +
                        "y que haya conexión con el servidor.");
                    return;
                }

                Clipboard.SetText(url);
                _toastService.ShowSuccess(
                    "Link de aprobación copiado al portapapeles. Pegalo en el mail al cliente: " +
                    "cuando apruebe o rechace, el estado del presupuesto se actualiza solo.");
                // La bitácora es visible para todos los usuarios: registrar el evento sin
                // la URL (el token dentro de la query ES la autorización del link).
                _ = _auditService.LogAsync(CurrentOrder.Id, "Link de aprobación generado",
                    "portal público de aprobación");
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "CopyApprovalLinkAsync failed");
                _dialogService.ShowError("Link de aprobación", ex.Message);
            }
        }

        // ── Combos de evento (carritos predefinidos) ─────────────────

        /// <summary>Combos guardados, para el selector "Empezar desde combo".</summary>
        public ObservableCollection<EventTemplate> EventCombos { get; } = new();

        /// <summary>Combo seleccionado en el ComboBox del rail (solo selección; aplicar es explícito).</summary>
        [ObservableProperty]
        private EventTemplate? _selectedCombo;

        /// <summary>
        /// Carga el combo seleccionado al carrito con precios VIGENTES del catálogo.
        /// Si ya hay productos, pregunta si reemplazar o sumar.
        /// </summary>
        [RelayCommand]
        private void ApplyCombo()
        {
            if (SelectedCombo == null)
            {
                _toastService.ShowInfo("Elegí un combo de la lista primero.");
                return;
            }

            List<EventTemplateItem> items;
            try
            {
                items = System.Text.Json.JsonSerializer
                    .Deserialize<List<EventTemplateItem>>(SelectedCombo.ItemsJson) ?? new();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Combo corrupto: {Name}", SelectedCombo.Name);
                _dialogService.ShowWarning("Combo dañado",
                    $"El combo \"{SelectedCombo.Name}\" no se pudo leer. Guardalo de nuevo desde un pedido armado.");
                return;
            }

            if (items.Count == 0)
            {
                _toastService.ShowInfo($"El combo \"{SelectedCombo.Name}\" está vacío.");
                return;
            }

            PushUndoSnapshot();
            if (SelectedItems.Any())
            {
                if (_dialogService.ShowConfirm("Cargar combo",
                    $"Ya hay productos en el pedido. ¿Reemplazarlos con el combo \"{SelectedCombo.Name}\"?\n\n" +
                    "(No: el combo se SUMA a lo que ya está.)"))
                    ClearCart();
            }

            int added = 0;
            var missing = new List<Guid>();
            foreach (var ci in items)
            {
                var product = AvailableProducts.FirstOrDefault(p => p.Id == ci.ProductId);
                if (product == null) { missing.Add(ci.ProductId); continue; }

                var existing = SelectedItems.FirstOrDefault(i => i.ProductId == product.Id);
                if (existing != null)
                {
                    existing.Quantity += Math.Max(1, ci.Quantity);
                    continue;
                }

                AddItemToCart(new OrderItem
                {
                    ProductId = product.Id,
                    Product = product,
                    Quantity = Math.Max(1, ci.Quantity),
                    Dias = Math.Max(1, ci.Dias),
                    UnitPrice = product.BasePrice, // receta: siempre precio de hoy
                    ImagePath = product.ImagePath,
                    CustomFieldsJson = product.CustomFieldsJson,
                    DescriptionSnapshot = product.Description,
                    RequestedMeasure = string.Empty,
                });
                added++;
            }

            var msg = $"Combo \"{SelectedCombo.Name}\": {added} producto(s) cargados con precios actuales.";
            if (missing.Count > 0)
                msg += $"\n{missing.Count} producto(s) del combo ya no están en el catálogo y se omitieron.";
            _toastService.ShowSuccess(msg);
        }

        /// <summary>
        /// Guarda el carrito actual como combo con nombre. Mismo nombre = pisa el combo
        /// existente (previa confirmación).
        /// </summary>
        [RelayCommand]
        private async Task SaveCartAsComboAsync()
        {
            if (!SelectedItems.Any())
            {
                _toastService.ShowInfo("Armá el pedido primero: el combo se guarda desde el carrito actual.");
                return;
            }

            var name = _dialogService.ShowInput("Guardar como combo",
                "Nombre del combo (ej.: \"Casamiento clásico\", \"Stand de feria\"). " +
                "Después lo cargás en un clic desde \"Empezar desde combo\".");
            if (string.IsNullOrWhiteSpace(name)) return;

            var itemsJson = System.Text.Json.JsonSerializer.Serialize(SelectedItems
                .Select(i => new EventTemplateItem
                {
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    Dias = i.Dias,
                })
                .ToList());

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var existing = await db.EventTemplates.FirstOrDefaultAsync(t => t.Name == name);
                if (existing != null)
                {
                    if (!_dialogService.ShowConfirm("Combo existente",
                        $"Ya existe un combo llamado \"{name}\". ¿Reemplazarlo con el pedido actual?"))
                        return;
                    existing.ItemsJson = itemsJson;
                    existing.CreatedDate = DateTime.UtcNow;
                    existing.CreatedByName = _currentUserService.Current?.Name;
                }
                else
                {
                    db.EventTemplates.Add(new EventTemplate
                    {
                        Name = name,
                        ItemsJson = itemsJson,
                        CreatedByName = _currentUserService.Current?.Name,
                    });
                }
                await db.SaveChangesAsync();

                // Refrescar el selector con la lista real de la base.
                var combos = await db.EventTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
                EventCombos.Clear();
                foreach (var t in combos) EventCombos.Add(t);
                SelectedCombo = EventCombos.FirstOrDefault(t => t.Name == name);

                _toastService.ShowSuccess($"Combo \"{name}\" guardado ({SelectedItems.Count} producto(s)).");
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "SaveCartAsComboAsync failed");
                _dialogService.ShowError("Guardar combo", ex.Message);
            }
        }

        /// <summary>Elimina el combo seleccionado (los combos son recetas, no órdenes: sin FKs).</summary>
        [RelayCommand]
        private async Task DeleteComboAsync()
        {
            if (SelectedCombo == null) return;
            if (!_dialogService.ShowConfirm("Eliminar combo",
                $"¿Eliminar el combo \"{SelectedCombo.Name}\"? Los presupuestos ya generados no se tocan."))
                return;

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var row = await db.EventTemplates.FirstOrDefaultAsync(t => t.Id == SelectedCombo.Id);
                if (row != null)
                {
                    db.EventTemplates.Remove(row);
                    await db.SaveChangesAsync();
                }
                EventCombos.Remove(SelectedCombo);
                SelectedCombo = null;
                _toastService.ShowSuccess("Combo eliminado.");
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "DeleteComboAsync failed");
                _dialogService.ShowError("Eliminar combo", ex.Message);
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
            NotifyCommercialTotals();
            ScheduleStockConflictRefresh();
            RefreshGenerationWarnings();
        }

        private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(OrderItem.Quantity) or nameof(OrderItem.Dias) or nameof(OrderItem.UnitPrice) or nameof(OrderItem.Total))
            {
                SelectionVersion++;
                OnPropertyChanged(nameof(FinalBudget));
                NotifyCommercialTotals();
                if (e.PropertyName is nameof(OrderItem.Quantity) or nameof(OrderItem.Dias))
                    ScheduleStockConflictRefresh();
            }
        }

        // ── Debounce del chequeo de stock ────────────────────────────
        // Cargar una orden de N items dispara N CollectionChanged: sin debounce eso eran
        // N rondas de queries (una por producto cada una) contra la base — y en modo
        // servidor, decenas de round-trips de red. Además, sin cancelación una ronda
        // vieja podía completar DESPUÉS de una nueva y pisar los flags con datos stale.
        private CancellationTokenSource? _stockRefreshCts;

        private void ScheduleStockConflictRefresh()
        {
            _stockRefreshCts?.Cancel();
            _stockRefreshCts = new CancellationTokenSource();
            var token = _stockRefreshCts.Token;
            _ = DebouncedStockRefreshAsync(token);
        }

        private async Task DebouncedStockRefreshAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(300, token);
                var warnings = await RefreshStockConflictsAsync();
                _ = warnings; // los flags por ítem ya quedaron seteados; el modal solo se usa al generar
                RefreshGenerationWarnings(); // el panel de avisos refleja los conflictos nuevos
            }
            catch (TaskCanceledException) { /* llegó un cambio más nuevo */ }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Debounced stock refresh failed");
            }
        }

    }
}
