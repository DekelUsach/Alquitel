using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Core.Helpers;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure;
using Alquitel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;

namespace Alquitel.UI.ViewModels
{
    /// <summary>Lote de curaduría del padrón. Todas = sin filtro estructural.</summary>
    public enum LocationScope { Todas, ConActividad, SinUso, ARevisar }

    /// <summary>Opción del rail con su contador vivo (mismo patrón que PoolFilterOption).</summary>
    public sealed partial class LocationScopeOption : ObservableObject
    {
        public LocationScope Key { get; }
        public string Label { get; }
        public string Hint { get; }

        [ObservableProperty]
        private int _count;

        public LocationScopeOption(LocationScope key, string label, string hint)
        {
            Key = key;
            Label = label;
            Hint = hint;
        }
    }

    /// <summary>
    /// Fila del padrón. Se arma en memoria cruzando Locations con una proyección de
    /// Orders; NO es la entidad EF (que viene AsNoTracking, así que mutarla no persiste).
    /// </summary>
    public sealed partial class LocationRow : ObservableObject
    {
        private static readonly CultureInfo EsAr = CultureInfo.GetCultureInfo("es-AR");

        public Guid Id { get; }

        /// <summary>Observable para que el renombrado se refleje sin recargar la fila.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        private string _name;

        public int Events { get; }
        public int DistinctClients { get; }
        public DateTime? LastEventDate { get; }
        public DateTime? NextEventDate { get; }

        /// <summary>Clave normalizada: base de la detección de duplicados y del orden A–Z.</summary>
        public string NormalizedKey { get; }

        public bool IsSentinel { get; }
        public bool IsUnnamed { get; }

        /// <summary>
        /// Lo setea el ViewModel después de agrupar todas las filas por NormalizedKey:
        /// una fila sola no puede saber si su nombre está repetido.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BadgeText))]
        [NotifyPropertyChangedFor(nameof(BadgeKind))]
        private bool _isDuplicate;

        public LocationRow(Guid id, string? name, int events, int distinctClients,
                           DateTime? lastEventDate, DateTime? nextEventDate)
        {
            Id = id;
            _name = name ?? string.Empty;
            Events = events;
            DistinctClients = distinctClients;
            LastEventDate = lastEventDate;
            NextEventDate = nextEventDate;

            NormalizedKey = LocationNameNormalizer.Normalize(_name);
            IsSentinel = LocationNameNormalizer.IsSentinel(_name);
            IsUnnamed = string.IsNullOrWhiteSpace(_name);

            MetaLine = BuildMetaLine();
            AccessibleName = BuildAccessibleName();
        }

        public bool IsUnused => Events == 0;

        /// <summary>El armador crea lugares con nombre vacío si el campo quedó sin completar.</summary>
        public string DisplayName => IsUnnamed ? "(sin nombre)" : Name;

        /// <summary>0 = centinela (siempre arriba), 1 = el resto. Es la primera SortDescription.</summary>
        public int SortGroup => IsSentinel ? 0 : 1;

        /// <summary>Orden A–Z insensible a mayúsculas y acentos.</summary>
        public string SortNameKey => NormalizedKey;

        /// <summary>Sin próximo evento, la fila se va al final del orden cronológico.</summary>
        public DateTime NextSortKey => NextEventDate ?? DateTime.MaxValue;

        public bool HasNextEvent => NextEventDate.HasValue;
        public string NextDay => NextEventDate?.ToString("dd", EsAr) ?? string.Empty;
        public string NextMonth => NextEventDate?.ToString("MMM yy", EsAr) ?? string.Empty;

        public string EventsLabel => Events == 1 ? "presupuesto" : "presupuestos";

        public string MetaLine { get; }
        public string AccessibleName { get; }

        public string BadgeKind =>
            IsSentinel ? "Sistema"
            : IsUnnamed ? "SinNombre"
            : IsDuplicate ? "Repetido"
            : IsUnused ? "SinUso"
            : string.Empty;

        public string BadgeText => BadgeKind switch
        {
            "Sistema" => "SISTEMA",
            "SinNombre" => "SIN NOMBRE",
            "Repetido" => "REPETIDO",
            "SinUso" => "SIN USO",
            _ => string.Empty
        };

        /// <summary>El centinela no se renombra ni se borra desde la UI.</summary>
        public bool CanRename => !IsSentinel;
        public bool CanDelete => !IsSentinel;

        private string BuildMetaLine()
        {
            if (Events == 0) return "Sin presupuestos todavía";

            var clientes = DistinctClients == 1 ? "1 cliente" : $"{DistinctClients} clientes distintos";

            return LastEventDate is DateTime last
                ? $"{clientes} · último evento {last:dd/MM/yy}"
                : $"{clientes} · sin fechas de evento cargadas";
        }

        private string BuildAccessibleName()
        {
            var partes = new List<string> { DisplayName, $"{Events} {EventsLabel}" };
            if (NextEventDate is DateTime next)
                partes.Add($"próximo evento {next.ToString("d 'de' MMMM", EsAr)}");
            if (BadgeText.Length > 0)
                partes.Add(BadgeText.ToLowerInvariant());
            return string.Join(", ", partes);
        }
    }

    /// <summary>Fila del bloque «Equipamiento que más se pide acá» (ficha del lugar).</summary>
    public sealed record TopItemRow(string Description, int Units);

    /// <summary>
    /// Fila del bloque «Últimos presupuestos» de la ficha del lugar.
    /// No se reusa el RecentOrderRow del Dashboard: ese carga otros campos y sirve al
    /// comando "repetir pedido"; compartirlo ataría dos pantallas sin necesidad.
    /// </summary>
    public sealed record LocationOrderRow(Guid OrderId, string BudgetNumber, string ClientName,
                                          OrderStatus Status, DateTime? EventDate, DateTime CreatedDate)
    {
        public string DateLabel { get; } = (EventDate ?? CreatedDate).ToString("dd/MM/yy");
    }

    /// <summary>
    /// El padrón de lugares: un registro que se audita solo.
    ///
    /// La pantalla anterior era un tablero de fichas donde cada una mostraba un único
    /// dato (el nombre) en 228 px de ancho, sin buscador, sin orden útil y sin forma de
    /// distinguir "La Rural" de "la rural". Como el campo "Lugar del evento" del armador
    /// es texto libre con find-or-create por nombre exacto, el padrón se ensucia solo —
    /// y ese nombre se imprime en el presupuesto que va al cliente.
    ///
    /// Acá el foco es el mantenimiento: lista densa con la actividad real de cada lugar
    /// (derivada de Orders, sin tocar la entidad Location ni agregar migraciones), lotes
    /// de curaduría, detección de nombres repetidos y fusión de duplicados que reasigna
    /// los presupuestos en una transacción auditada.
    /// </summary>
    public partial class LocationsViewModel : ObservableObject, IAsyncInitialization
    {
        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
        private readonly IDialogService _dialogService;
        private readonly IDispatcher _dispatcher;
        private readonly IToastService _toastService;
        private readonly IOrderAuditService _auditService;
        private readonly INavigationService _navigationService;
        private readonly Func<OrderPoolViewModel> _orderPoolFactory;

        private readonly ICollectionView _rowsView;

        // ── Colección y vista ────────────────────────────────────────
        public ObservableCollection<LocationRow> Rows { get; } = new();
        public ICollectionView RowsView => _rowsView;
        public ObservableCollection<LocationScopeOption> ScopeOptions { get; } = new();

        // ── Filtros y orden ──────────────────────────────────────────
        [ObservableProperty]
        private LocationScopeOption? _selectedScope;

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSortAlpha))]
        [NotifyPropertyChangedFor(nameof(IsSortUso))]
        [NotifyPropertyChangedFor(nameof(IsSortProximo))]
        private string _sortMode = "Alfabetico";

        public bool IsSortAlpha => SortMode == "Alfabetico";
        public bool IsSortUso => SortMode == "Uso";
        public bool IsSortProximo => SortMode == "Proximo";

        // ── Estado de pantalla ───────────────────────────────────────
        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private LocationRow? _selectedRow;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        // Estados vacíos: booleanos explícitos, nunca .Count (con filtro activo, un
        // contador en cero no distingue "no hay datos" de "no hay coincidencias").
        [ObservableProperty]
        private bool _isEmptyCatalog;

        [ObservableProperty]
        private bool _hasNoMatches;

        [ObservableProperty]
        private bool _isCleanRegistry;

        [ObservableProperty]
        private bool _hasHygieneWarnings;

        [ObservableProperty]
        private string _hygieneSummary = string.Empty;

        // ── Ficha del lugar (drawer) ─────────────────────────────────
        [ObservableProperty]
        private bool _isEditing;

        [ObservableProperty]
        private Guid _editingId;

        [ObservableProperty]
        private bool _isNewLocation;

        /// <summary>True para el centinela: su nombre es una constante del sistema.</summary>
        [ObservableProperty]
        private bool _isRenameLocked;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveLocationCommand))]
        private string _editName = string.Empty;

        [ObservableProperty]
        private string _nameFeedback = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveLocationCommand))]
        private bool _nameFeedbackIsError;

        [ObservableProperty]
        private bool _isDetailLoading;

        [ObservableProperty]
        private int _detailEvents;

        [ObservableProperty]
        private int _detailClients;

        [ObservableProperty]
        private string _detailFirstLast = string.Empty;

        [ObservableProperty]
        private string _detailNextLabel = string.Empty;

        public ObservableCollection<TopItemRow> DetailTopItems { get; } = new();
        public ObservableCollection<LocationOrderRow> DetailRecentOrders { get; } = new();

        // ── Fusión ───────────────────────────────────────────────────
        public ObservableCollection<LocationRow> MergeCandidates { get; } = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(MergeIntoCommand))]
        private LocationRow? _mergeTarget;

        [ObservableProperty]
        private int _exactDuplicateCount;

        public LocationsViewModel(
            IDbContextFactory<AlquitelDbContext> dbContextFactory,
            IDialogService dialogService,
            IDispatcher dispatcher,
            IToastService toastService,
            IOrderAuditService auditService,
            INavigationService navigationService,
            Func<OrderPoolViewModel> orderPoolFactory)
        {
            _dbContextFactory = dbContextFactory;
            _dialogService = dialogService;
            _dispatcher = dispatcher;
            _toastService = toastService;
            _auditService = auditService;
            _navigationService = navigationService;
            _orderPoolFactory = orderPoolFactory;

            ScopeOptions.Add(new(LocationScope.Todas, "Todas", "Todo el padrón"));
            ScopeOptions.Add(new(LocationScope.ConActividad, "Con actividad", "Tienen al menos un presupuesto"));
            ScopeOptions.Add(new(LocationScope.SinUso, "Sin uso", "Nunca se usaron: se pueden borrar sin reasignar nada"));
            ScopeOptions.Add(new(LocationScope.ARevisar, "A revisar", "Nombres repetidos y lugares sin nombre"));

            _rowsView = CollectionViewSource.GetDefaultView(Rows);
            _rowsView.Filter = FilterRow;
            ApplySort();
            SelectedScope = ScopeOptions[0];
        }

        partial void OnSearchTextChanged(string value)
        {
            _rowsView.Refresh();
            RecalcViewState();
        }

        partial void OnSelectedScopeChanged(LocationScopeOption? value)
        {
            _rowsView.Refresh();
            RecalcViewState();
        }

        partial void OnSortModeChanged(string value) => ApplySort();

        partial void OnEditNameChanged(string value) => UpdateNameFeedback();

        /// <summary>El centinela queda fijado arriba en cualquier modo de orden.</summary>
        private void ApplySort()
        {
            using (_rowsView.DeferRefresh())
            {
                _rowsView.SortDescriptions.Clear();
                _rowsView.SortDescriptions.Add(
                    new SortDescription(nameof(LocationRow.SortGroup), ListSortDirection.Ascending));

                switch (SortMode)
                {
                    case "Uso":
                        _rowsView.SortDescriptions.Add(
                            new SortDescription(nameof(LocationRow.Events), ListSortDirection.Descending));
                        break;
                    case "Proximo":
                        _rowsView.SortDescriptions.Add(
                            new SortDescription(nameof(LocationRow.NextSortKey), ListSortDirection.Ascending));
                        break;
                }

                _rowsView.SortDescriptions.Add(
                    new SortDescription(nameof(LocationRow.SortNameKey), ListSortDirection.Ascending));
            }
        }

        private bool FilterRow(object item)
        {
            if (item is not LocationRow row) return true;

            switch (SelectedScope?.Key)
            {
                case LocationScope.ConActividad when row.Events == 0: return false;
                case LocationScope.SinUso when row.Events > 0: return false;
                case LocationScope.ARevisar when !row.IsDuplicate && !row.IsUnnamed: return false;
            }

            if (string.IsNullOrWhiteSpace(SearchText)) return true;

            var texto = SearchText.Trim();
            // Se busca contra el nombre crudo Y contra el normalizado: escribir "rural"
            // encuentra "Predio de La Rural" y "la rural" aunque estén escritos distinto.
            return row.Name.Contains(texto, StringComparison.OrdinalIgnoreCase)
                || row.NormalizedKey.Contains(LocationNameNormalizer.Normalize(texto), StringComparison.Ordinal);
        }

        private void RecalcViewState()
        {
            IsEmptyCatalog = Rows.Count == 0;
            HasNoMatches = Rows.Count > 0 && _rowsView.IsEmpty
                           && SelectedScope?.Key != LocationScope.ARevisar;
            IsCleanRegistry = Rows.Count > 0 && _rowsView.IsEmpty
                              && SelectedScope?.Key == LocationScope.ARevisar;
        }

        private void RecalcScopeCounts()
        {
            foreach (var option in ScopeOptions)
            {
                option.Count = option.Key switch
                {
                    LocationScope.ConActividad => Rows.Count(r => r.Events > 0),
                    LocationScope.SinUso => Rows.Count(r => r.IsUnused),
                    LocationScope.ARevisar => Rows.Count(r => r.IsDuplicate || r.IsUnnamed),
                    _ => Rows.Count,
                };
            }
        }

        private void RecalcHygiene()
        {
            int repetidos = Rows.Count(r => r.IsDuplicate);
            int sinNombre = Rows.Count(r => r.IsUnnamed);

            var partes = new List<string>();
            if (repetidos > 0)
                partes.Add(repetidos == 1 ? "1 nombre repetido" : $"{repetidos} nombres repetidos");
            if (sinNombre > 0)
                partes.Add(sinNombre == 1 ? "1 sin nombre" : $"{sinNombre} sin nombre");

            HygieneSummary = string.Join(" · ", partes);
            HasHygieneWarnings = partes.Count > 0;
        }

        // ── Carga ────────────────────────────────────────────────────

        public async Task InitializeAsync() => await LoadAsync();

        [RelayCommand]
        private async Task RefreshAsync() => await LoadAsync();

        /// <summary>
        /// Dos consultas de proyección escalar y toda la agregación en memoria. Es la
        /// convención de la app (ReportsViewModel, DashboardViewModel) y esquiva de un
        /// saque los tres modos de falla que solo explotan en runtime: GroupBy con
        /// proyecciones complejas, COUNT(DISTINCT) anidado, y MAX sobre un DateTime que
        /// en SQLite es una columna TEXT.
        /// </summary>
        private async Task LoadAsync()
        {
            IsLoading = true;
            try
            {
                var hoy = DateTime.Today;
                List<LocationRow> nuevas;

                using (var db = await _dbContextFactory.CreateDbContextAsync())
                {
                    // Location no tiene filtro global de borrado lógico: devuelve todo.
                    var lugares = await db.Locations.AsNoTracking()
                        .Select(l => new { l.Id, l.Name })
                        .ToListAsync();

                    var ordenes = await db.Orders.AsNoTracking()
                        .Select(o => new { o.LocationId, o.ClientId, o.Status, o.EventDate })
                        .ToListAsync();

                    var porLugar = ordenes.GroupBy(o => o.LocationId)
                                          .ToDictionary(g => g.Key, g => g.ToList());

                    nuevas = new List<LocationRow>(lugares.Count);
                    foreach (var l in lugares)
                    {
                        porLugar.TryGetValue(l.Id, out var suyas);
                        suyas ??= new();

                        // Cuenta TODAS las órdenes que apuntan acá, incluidas rechazadas y
                        // archivadas: es exactamente lo que se reasigna si el lugar se borra
                        // o se fusiona, así que el número de la fila y el del diálogo coinciden.
                        var eventos = suyas.Count;
                        var clientes = suyas.Select(o => o.ClientId).Distinct().Count();

                        var vivas = suyas.Where(o => o.Status != OrderStatus.Rejected
                                                  && o.Status != OrderStatus.Archived).ToList();

                        var pasadas = vivas.Where(o => o.EventDate.HasValue && o.EventDate.Value.Date <= hoy)
                                           .Select(o => o.EventDate!.Value).ToList();
                        var futuras = vivas.Where(o => o.EventDate.HasValue && o.EventDate.Value.Date >= hoy)
                                           .Select(o => o.EventDate!.Value).ToList();

                        nuevas.Add(new LocationRow(
                            l.Id, l.Name, eventos, clientes,
                            pasadas.Count == 0 ? null : pasadas.Max(),
                            futuras.Count == 0 ? null : futuras.Min()));
                    }
                }

                // Duplicados SIEMPRE en memoria: ToLowerInvariant no traduce a SQL y
                // ToLower() depende de la collation de cada proveedor (PostgreSQL es
                // sensible a mayúsculas y acentos).
                var repetidas = LocationNameNormalizer.DuplicateKeys(nuevas.Select(r => r.Name));
                foreach (var r in nuevas)
                    r.IsDuplicate = !r.IsUnnamed && repetidas.Contains(r.NormalizedKey);

                var idSeleccionado = SelectedRow?.Id;

                // UN SOLO marshaling, con todo lo que depende de la colección adentro:
                // IDispatcher.InvokeAsync es BeginInvoke (fire-and-forget), así que
                // tocar Rows en la línea siguiente sería una carrera.
                _dispatcher.InvokeAsync(() =>
                {
                    Rows.Clear();
                    foreach (var r in nuevas) Rows.Add(r);

                    RecalcScopeCounts();
                    RecalcHygiene();
                    _rowsView.Refresh();
                    RecalcViewState();

                    if (idSeleccionado is Guid id)
                        SelectedRow = Rows.FirstOrDefault(r => r.Id == id);

                    int sinUso = Rows.Count(r => r.IsUnused);
                    StatusMessage =
                        $"{Rows.Count} {(Rows.Count == 1 ? "lugar" : "lugares")} · " +
                        $"{sinUso} sin uso · " +
                        $"{repetidas.Count} {(repetidas.Count == 1 ? "nombre repetido" : "nombres repetidos")}";

                    IsLoading = false;
                });
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Locations LoadAsync failed");
                StatusMessage = $"Error al cargar el padrón: {ex.Message}";
                IsLoading = false;
            }
        }

        // ── Ficha del lugar ──────────────────────────────────────────

        [RelayCommand]
        private void NewLocation()
        {
            // Sin nombre precargado (antes se guardaba el literal "Nueva Ubicación" si el
            // usuario no lo tocaba) y sin tocar la selección: asignarle a un Selector un
            // objeto que no está en su ItemsSource hace que WPF lo rechace y devuelva
            // null, cerrando el editor apenas se abre.
            EditingId = Guid.NewGuid();
            IsNewLocation = true;
            IsRenameLocked = false;
            EditName = string.Empty;
            ClearDetail();
            IsEditing = true;
            UpdateNameFeedback();
        }

        [RelayCommand]
        private async Task OpenLocationAsync(LocationRow? row)
        {
            if (row == null) return;

            SelectedRow = row;
            EditingId = row.Id;
            IsNewLocation = false;
            IsRenameLocked = !row.CanRename;
            EditName = row.Name;
            IsEditing = true;
            UpdateNameFeedback();
            BuildMergeCandidates(row);
            await LoadDetailAsync(row.Id);
        }

        [RelayCommand]
        private void CancelEdit()
        {
            IsEditing = false;
            IsNewLocation = false;
            EditName = string.Empty;
            ClearDetail();
        }

        private void ClearDetail()
        {
            DetailTopItems.Clear();
            DetailRecentOrders.Clear();
            MergeCandidates.Clear();
            MergeTarget = null;
            ExactDuplicateCount = 0;
            DetailEvents = 0;
            DetailClients = 0;
            DetailFirstLast = string.Empty;
            DetailNextLabel = string.Empty;
        }

        private void UpdateNameFeedback()
        {
            if (string.IsNullOrWhiteSpace(EditName))
            {
                NameFeedback = "✗ Poné un nombre";
                NameFeedbackIsError = true;
                return;
            }

            var clave = LocationNameNormalizer.Normalize(EditName);
            var choca = Rows.Any(r => r.Id != EditingId && r.NormalizedKey == clave);
            NameFeedback = choca ? "✗ Ya hay un lugar con ese nombre" : "✓ Nombre disponible";
            NameFeedbackIsError = choca;
        }

        private void BuildMergeCandidates(LocationRow row)
        {
            MergeCandidates.Clear();
            MergeTarget = null;

            // Se listan TODOS los demás lugares: el duplicado que el normalizado exacto
            // no detecta ("La Rural" vs "Predio La Rural") se arma a mano. Preferimos
            // que el usuario elija el par antes que adivinarlo con similitud difusa.
            foreach (var candidato in Rows.Where(r => r.Id != row.Id).OrderBy(r => r.SortNameKey))
                MergeCandidates.Add(candidato);

            ExactDuplicateCount = Rows.Count(r => r.Id != row.Id && r.NormalizedKey == row.NormalizedKey);
        }

        private void FillDetailHeader()
        {
            var row = SelectedRow;
            if (row == null) return;

            DetailEvents = row.Events;
            DetailClients = row.DistinctClients;
            DetailNextLabel = row.NextEventDate is DateTime next ? next.ToString("dd/MM/yy") : "—";
            DetailFirstLast = row.LastEventDate is DateTime last
                ? $"Último evento: {last:dd/MM/yy}"
                : "Todavía no hay eventos con fecha cargada.";
        }

        /// <summary>
        /// Detalle del drawer: dos consultas que corren SOLO al abrir la ficha, nunca por
        /// fila. Si se dispararan desde el DataTemplate serían un N+1 contra Supabase.
        /// </summary>
        private async Task LoadDetailAsync(Guid id)
        {
            IsDetailLoading = true;
            DetailTopItems.Clear();
            DetailRecentOrders.Clear();

            try
            {
                List<LocationOrderRow> recientes;
                List<TopItemRow> top;

                using (var db = await _dbContextFactory.CreateDbContextAsync())
                {
                    // IgnoreQueryFilters es obligatorio: Client tiene filtro global
                    // !IsArchived y la relación es requerida, así que sin esto desaparecen
                    // las órdenes de clientes archivados. Sin Include: la proyección
                    // o.Client!.CompanyName ya arma el join (un Include seguido de Select
                    // lo descarta EF y loguea warning).
                    var crudas = await db.Orders.AsNoTracking().IgnoreQueryFilters()
                        .Where(o => o.LocationId == id)
                        .OrderByDescending(o => o.CreatedDate)
                        .Take(6)
                        .Select(o => new
                        {
                            o.Id,
                            o.BudgetNumber,
                            o.Status,
                            o.EventDate,
                            o.CreatedDate,
                            Cliente = o.Client!.CompanyName
                        })
                        .ToListAsync();

                    recientes = crudas
                        .Select(r => new LocationOrderRow(r.Id, r.BudgetNumber, r.Cliente,
                                                        r.Status, r.EventDate, r.CreatedDate))
                        .ToList();

                    // Se agrupa por DescriptionSnapshot (el título congelado del ítem) para
                    // no joinear Products, que sí tiene filtro global !IsArchived.
                    var items = await db.OrderItems.AsNoTracking()
                        .Where(i => db.Orders.Any(o => o.Id == i.OrderId && o.LocationId == id))
                        .Select(i => new { i.DescriptionSnapshot, i.Quantity })
                        .ToListAsync();

                    top = items
                        .GroupBy(i => Alquitel.Core.Parsing.TagParser.StripTags(i.DescriptionSnapshot)?.Trim() is { Length: > 0 } d
                            ? d
                            : "(producto sin descripción)")
                        .Select(g => new TopItemRow(g.Key, g.Sum(x => x.Quantity)))
                        .OrderByDescending(t => t.Units)
                        .Take(5)
                        .ToList();
                }

                _dispatcher.InvokeAsync(() =>
                {
                    foreach (var r in recientes) DetailRecentOrders.Add(r);
                    foreach (var t in top) DetailTopItems.Add(t);
                    FillDetailHeader();
                    IsDetailLoading = false;
                });
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Locations LoadDetailAsync failed for {LocationId}", id);
                IsDetailLoading = false;
            }
        }

        // ── Guardar ──────────────────────────────────────────────────

        private bool CanSaveLocation()
            => !IsRenameLocked && !string.IsNullOrWhiteSpace(EditName) && !NameFeedbackIsError;

        [RelayCommand(CanExecute = nameof(CanSaveLocation))]
        private async Task SaveLocationAsync()
        {
            // Trim: sin esto conviven "La Rural " y "La Rural" como dos lugares distintos.
            var nombre = EditName.Trim();
            if (nombre.Length == 0)
            {
                _dialogService.ShowWarning("Validación", "El nombre del lugar no puede estar vacío.");
                return;
            }

            try
            {
                using (var db = await _dbContextFactory.CreateDbContextAsync())
                {
                    var location = await db.Locations.FirstOrDefaultAsync(l => l.Id == EditingId);

                    if (location == null)
                    {
                        // Upsert conservando el Guid que asignó NewLocation().
                        location = new Location { Id = EditingId };
                        db.Locations.Add(location);
                    }
                    else if (LocationNameNormalizer.IsSentinel(location.Name) &&
                             !LocationNameNormalizer.IsSentinel(nombre))
                    {
                        _dialogService.ShowWarning("Ubicación del sistema",
                            $"«{LocationNameNormalizer.SentinelName}» es la bandeja donde caen los presupuestos " +
                            "de lugares borrados. No se puede renombrar.");
                        return;
                    }

                    location.Name = nombre;
                    await db.SaveChangesAsync();
                }

                _toastService.ShowSuccess(IsNewLocation ? $"Se creó «{nombre}»" : $"Se guardó «{nombre}»");
                IsEditing = false;
                await LoadAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Locations SaveLocationAsync failed for {LocationId}", EditingId);
                _dialogService.ShowError("Error al guardar", ex.Message);
            }
        }

        // ── Eliminar y fusionar ──────────────────────────────────────

        [RelayCommand]
        private async Task DeleteLocationAsync(LocationRow? row)
        {
            row ??= SelectedRow;
            if (row == null || !row.CanDelete) return;

            try
            {
                int enUso;
                using (var db = await _dbContextFactory.CreateDbContextAsync())
                {
                    // Se recuenta contra la base: sobre la base compartida pueden haber
                    // entrado órdenes nuevas entre la carga de la pantalla y este clic.
                    enUso = await db.Orders.CountAsync(o => o.LocationId == row.Id);
                }

                string nombre = row.DisplayName;
                string prompt = enUso == 0
                    ? $"¿Eliminar el lugar «{nombre}»? No tiene ningún presupuesto asociado."
                    : $"«{nombre}» figura en {enUso} presupuesto(s).\n\n" +
                      $"Si lo eliminás, esos presupuestos pasan a «{LocationNameNormalizer.SentinelName}» " +
                      "y no se pierden.\n\n¿Continuar?";

                if (!_dialogService.ShowConfirm("Eliminar ubicación", prompt))
                    return;

                int movidas = await ReassignAndDeleteAsync(row.Id, nombre, enUso > 0);

                _toastService.ShowSuccess(movidas > 0
                    ? $"Se eliminó «{nombre}» · {movidas} presupuesto(s) reasignados"
                    : $"Se eliminó «{nombre}»");

                IsEditing = false;
                SelectedRow = null;
                await LoadAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Locations DeleteLocationAsync failed for {LocationId}", row.Id);
                _dialogService.ShowError("Error al eliminar",
                    $"No se pudo eliminar la ubicación: {ex.Message}");
            }
        }

        private bool CanMergeInto()
            => SelectedRow != null && !IsRenameLocked && MergeTarget != null && MergeTarget.Id != SelectedRow.Id;

        [RelayCommand(CanExecute = nameof(CanMergeInto))]
        private async Task MergeIntoAsync()
        {
            var origen = SelectedRow;
            var destino = MergeTarget;
            if (origen == null || destino == null || origen.Id == destino.Id) return;

            try
            {
                int enUso;
                using (var db = await _dbContextFactory.CreateDbContextAsync())
                    enUso = await db.Orders.CountAsync(o => o.LocationId == origen.Id);

                if (!_dialogService.ShowConfirm("Fusionar lugares",
                        $"Se van a mover {enUso} presupuesto(s) de «{origen.DisplayName}» a " +
                        $"«{destino.DisplayName}» y «{origen.DisplayName}» se va a borrar.\n\n" +
                        "Esto no se deshace.\n\n¿Continuar?"))
                    return;

                int movidas = await ReassignAndDeleteAsync(origen.Id, origen.DisplayName,
                    reassign: true, explicitTargetId: destino.Id, targetName: destino.DisplayName);

                _toastService.ShowSuccess(
                    $"Se fusionó «{origen.DisplayName}» en «{destino.DisplayName}» · {movidas} presupuesto(s) reasignados");

                IsEditing = false;
                SelectedRow = null;
                await LoadAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Locations MergeIntoAsync failed for {LocationId}", origen.Id);
                _dialogService.ShowError("Error al fusionar", ex.Message);
            }
        }

        /// <summary>
        /// Mueve todas las órdenes del lugar origen al destino y borra el origen, todo en
        /// una transacción. Devuelve cuántas órdenes se movieron.
        ///
        /// Por qué hay que reasignar antes de borrar: <c>Order.LocationId</c> es un Guid NO
        /// nullable con la relación <c>.IsRequired()</c> y <c>OnDelete(DeleteBehavior.Restrict)</c>
        /// desde la migración RestrictOrderFks. Una orden no puede quedar sin lugar, y borrar
        /// sin reasignar lanza una violación de FK en ambos proveedores.
        ///
        /// Limitación conocida: <c>ExecuteUpdateAsync</c> saltea el change tracker, así que
        /// NO rota <c>Order.RowVersion</c>. Sobre la base compartida, otro usuario con la
        /// orden abierta puede pisar la reasignación sin enterarse. La transacción y la
        /// auditoría acotan el daño pero no eliminan esa carrera.
        /// </summary>
        private async Task<int> ReassignAndDeleteAsync(
            Guid sourceId, string sourceName, bool reassign,
            Guid? explicitTargetId = null, string? targetName = null)
        {
            List<Guid> afectadas = new();
            string destinoNombre = targetName ?? LocationNameNormalizer.SentinelName;

            using (var db = await _dbContextFactory.CreateDbContextAsync())
            {
                var strategy = db.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    afectadas.Clear();

                    await using var tx = await db.Database.BeginTransactionAsync();

                    if (reassign)
                    {
                        Guid targetId;
                        if (explicitTargetId is Guid id)
                        {
                            targetId = id;
                        }
                        else
                        {
                            var centinela = await GetOrCreateSentinelAsync(db);
                            targetId = centinela.Id;
                            destinoNombre = centinela.Name;
                        }

                        // Los ids se leen ANTES del update: ExecuteUpdateAsync no devuelve filas.
                        afectadas = await db.Orders.AsNoTracking()
                            .Where(o => o.LocationId == sourceId)
                            .Select(o => o.Id)
                            .ToListAsync();

                        if (afectadas.Count > 0)
                            await db.Orders.Where(o => o.LocationId == sourceId)
                                .ExecuteUpdateAsync(s => s.SetProperty(o => o.LocationId, targetId));
                    }

                    var origen = await db.Locations.FirstOrDefaultAsync(l => l.Id == sourceId);
                    if (origen != null)
                    {
                        db.Locations.Remove(origen);
                        await db.SaveChangesAsync();
                    }

                    await tx.CommitAsync();
                });
            }

            // La auditoría va DESPUÉS del commit: EfOrderAuditService abre su propio
            // DbContext desde la fábrica y no participa de esta transacción.
            foreach (var orderId in afectadas)
                await _auditService.LogAsync(orderId, "Ubicación reasignada",
                    $"«{sourceName}» → «{destinoNombre}»");

            return afectadas.Count;
        }

        /// <summary>
        /// Busca o crea el centinela comparando en memoria. El
        /// <c>FirstOrDefaultAsync(l =&gt; l.Name == "(Sin ubicación)")</c> anterior se
        /// traducía a SQL, y en PostgreSQL la comparación es sensible a mayúsculas y
        /// acentos: un "(sin ubicacion)" existente no se encontraba y se terminaba
        /// creando un segundo centinela. La tabla es chica: traerla entera es barato.
        /// </summary>
        private static async Task<Location> GetOrCreateSentinelAsync(AlquitelDbContext db)
        {
            var todas = await db.Locations.ToListAsync();
            var centinela = todas.FirstOrDefault(l => LocationNameNormalizer.IsSentinel(l.Name));

            if (centinela == null)
            {
                centinela = new Location { Id = Guid.NewGuid(), Name = LocationNameNormalizer.SentinelName };
                db.Locations.Add(centinela);
                await db.SaveChangesAsync();
            }

            return centinela;
        }

        // ── Navegación y filtros ─────────────────────────────────────

        [RelayCommand]
        private void ViewOrders(LocationRow? row)
        {
            row ??= SelectedRow;
            if (row == null) return;

            // El buscador de Seguimiento ya filtra por nombre de lugar: reusarlo evita
            // mantener una segunda vista de las mismas órdenes que en seis meses diría
            // algo distinto.
            var pool = _orderPoolFactory();
            pool.FilterText = row.Name;
            IsEditing = false;
            _navigationService.NavigateTo(pool);
        }

        [RelayCommand]
        private void SetSort(string? key)
        {
            if (!string.IsNullOrWhiteSpace(key)) SortMode = key;
        }

        [RelayCommand]
        private void ClearFilters()
        {
            SearchText = string.Empty;
            SelectedScope = ScopeOptions[0];
        }

        [RelayCommand]
        private void ShowReview()
            => SelectedScope = ScopeOptions.First(o => o.Key == LocationScope.ARevisar);
    }
}
