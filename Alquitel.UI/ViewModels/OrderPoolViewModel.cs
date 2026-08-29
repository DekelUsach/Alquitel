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
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;

namespace Alquitel.UI.ViewModels
{
    /// <summary>Filtro de estado del pool: Key null = todos.</summary>
    public sealed partial class PoolFilterOption : ObservableObject
    {
        public OrderStatus? Key { get; }
        public string Label { get; }

        [ObservableProperty]
        private int _count;

        public PoolFilterOption(OrderStatus? key, string label)
        {
            Key = key;
            Label = label;
        }
    }

    /// <summary>
    /// Fila del pool de seguimiento. El setter de Status persiste el cambio en la base
    /// a través del callback del ViewModel (con rollback si la escritura falla).
    /// </summary>
    public sealed class OrderPoolRow : ObservableObject
    {
        private readonly Func<OrderPoolRow, OrderStatus, OrderStatus, Task> _onStatusChanged;
        private OrderStatus _status;
        internal bool SuppressPersist;

        public Guid OrderId { get; }
        public string BudgetNumber { get; }
        public string ClientName { get; }
        public string LocationName { get; }
        public DateTime CreatedDate { get; }
        public string EventLabel { get; }
        public decimal Total { get; }
        public string AdminName { get; }

        /// <summary>Fecha de creación en UTC (CreatedDate es la versión local, para mostrar).</summary>
        private readonly DateTime _createdDateUtc;

        public OrderStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                var old = _status;
                _status = value;
                OnPropertyChanged();
                // Aprobar o rechazar apaga el reloj de vigencia: el badge se recalcula.
                OnPropertyChanged(nameof(ValidityLabel));
                OnPropertyChanged(nameof(HasValidityBadge));
                OnPropertyChanged(nameof(IsExpired));
                if (!SuppressPersist)
                    _ = _onStatusChanged(this, old, value);
            }
        }

        // ── Vigencia comercial ───────────────────────────────────────
        // Un presupuesto en borrador de hace dos meses tiene precios que ya no se
        // sostienen y nada en la app lo señalaba: el vendedor se enteraba al facturar.

        private BudgetValidity Validity => BudgetValidityPolicy.Evaluate(
            _createdDateUtc, Status == OrderStatus.Draft, DateTime.UtcNow);

        public string ValidityLabel => Validity.Label;

        /// <summary>Solo se muestra el badge cuando aporta: por vencer o ya vencido.</summary>
        public bool HasValidityBadge => Validity.State is BudgetValidityState.PorVencer or BudgetValidityState.Vencido;

        public bool IsExpired => Validity.State == BudgetValidityState.Vencido;

        public OrderPoolRow(Order order, Func<OrderPoolRow, OrderStatus, OrderStatus, Task> onStatusChanged)
        {
            _onStatusChanged = onStatusChanged;
            OrderId = order.Id;
            BudgetNumber = string.IsNullOrWhiteSpace(order.BudgetNumber) ? "(sin número)" : order.BudgetNumber;
            ClientName = order.Client?.CompanyName ?? "(sin cliente)";
            LocationName = order.Location?.Name ?? string.Empty;
            CreatedDate = order.CreatedDate.ToLocalTime();
            _createdDateUtc = order.CreatedDate;
            EventLabel = order.EventDate.HasValue
                ? SpanishDateFormatter.ToWordsRange(order.EventDate.Value, order.EventEndDate)
                : string.Empty;
            Total = order.Total;
            AdminName = order.AdminName;
            _status = order.Status;
        }
    }

    /// <summary>
    /// Pool de seguimiento: todas las órdenes del sistema (presupuestos, OF/facturación
    /// y OT) con su estado editable en línea y filtros por estado.
    /// </summary>
    public partial class OrderPoolViewModel : ObservableObject, IAsyncInitialization
    {
        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
        private readonly IDialogService _dialogService;
        private readonly INavigationService _navigationService;
        private readonly IServiceProvider _serviceProvider;
        private readonly ICollectionView _rowsView;

        [ObservableProperty]
        private string _filterText = string.Empty;

        [ObservableProperty]
        private PoolFilterOption? _selectedFilter;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public ObservableCollection<OrderPoolRow> Rows { get; } = new();
        public ICollectionView RowsView => _rowsView;

        /// <summary>Chips de filtro: Todos + un chip por estado, con contador.</summary>
        public ObservableCollection<PoolFilterOption> FilterOptions { get; } = new()
        {
            new PoolFilterOption(null, "Todos"),
            new PoolFilterOption(OrderStatus.Draft, "Borrador"),
            new PoolFilterOption(OrderStatus.Approved, "Aprobado"),
            new PoolFilterOption(OrderStatus.SentToOF, "Facturación (OF)"),
            new PoolFilterOption(OrderStatus.SentToOT, "Orden de Trabajo"),
            new PoolFilterOption(OrderStatus.Rejected, "Rechazado"),
            new PoolFilterOption(OrderStatus.Archived, "Archivado"),
        };

        /// <summary>Estados asignables desde el combo de cada fila.</summary>
        public IReadOnlyList<StatusOption> StatusOptions { get; } = new[]
        {
            new StatusOption(OrderStatus.Draft, "Borrador"),
            new StatusOption(OrderStatus.Approved, "Aprobado"),
            new StatusOption(OrderStatus.SentToOF, "Facturación (OF)"),
            new StatusOption(OrderStatus.SentToOT, "Orden de Trabajo"),
            new StatusOption(OrderStatus.Rejected, "Rechazado"),
            new StatusOption(OrderStatus.Archived, "Archivado"),
        };

        private readonly Alquitel.Core.Interfaces.IOrderAuditService _auditService;

        public OrderPoolViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory,
            IDialogService dialogService, INavigationService navigationService,
            IServiceProvider serviceProvider, Alquitel.Core.Interfaces.IOrderAuditService auditService)
        {
            _dbContextFactory = dbContextFactory;
            _dialogService = dialogService;
            _navigationService = navigationService;
            _serviceProvider = serviceProvider;
            _auditService = auditService;

            _rowsView = CollectionViewSource.GetDefaultView(Rows);
            _rowsView.Filter = FilterRow;
            _rowsView.SortDescriptions.Add(
                new SortDescription(nameof(OrderPoolRow.CreatedDate), ListSortDirection.Descending));

            SelectedFilter = FilterOptions[0];
        }

        partial void OnFilterTextChanged(string value) => _rowsView.Refresh();
        partial void OnSelectedFilterChanged(PoolFilterOption? value) => _rowsView.Refresh();

        private bool FilterRow(object item)
        {
            if (item is not OrderPoolRow row) return true;

            if (SelectedFilter?.Key is OrderStatus status && row.Status != status)
                return false;

            if (string.IsNullOrWhiteSpace(FilterText)) return true;
            var text = FilterText.Trim();
            return row.BudgetNumber.Contains(text, StringComparison.OrdinalIgnoreCase)
                || row.ClientName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || row.LocationName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || row.AdminName.Contains(text, StringComparison.OrdinalIgnoreCase)
                // La vigencia entra al buscador: escribir "vencido" en la caja de
                // búsqueda deja a la vista solo los presupuestos que hay que rescatar,
                // sin sumar otro control a la barra.
                || row.ValidityLabel.Contains(text, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True mientras se cargan las órdenes: la vista muestra skeleton rows.</summary>
        [ObservableProperty]
        private bool _isLoading;

        public async Task InitializeAsync()
        {
            IsLoading = true;
            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                // IgnoreQueryFilters: el pool es el historial completo — debe incluir
                // órdenes de clientes o productos archivados.
                var orders = await db.Orders.AsNoTracking().IgnoreQueryFilters()
                    .Include(o => o.Client)
                    .Include(o => o.Location)
                    .Include(o => o.Items)
                    .OrderByDescending(o => o.CreatedDate)
                    .ToListAsync();

                Rows.Clear();
                foreach (var o in orders)
                    Rows.Add(new OrderPoolRow(o, PersistStatusChangeAsync));

                RefreshCounts();
                StatusMessage = $"{Rows.Count} orden(es) en el sistema";
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "OrderPool InitializeAsync failed");
                StatusMessage = $"Error al cargar las órdenes: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void RefreshCounts()
        {
            foreach (var option in FilterOptions)
                option.Count = option.Key is OrderStatus s
                    ? Rows.Count(r => r.Status == s)
                    : Rows.Count;
        }

        private async Task PersistStatusChangeAsync(OrderPoolRow row, OrderStatus oldStatus, OrderStatus newStatus)
        {
            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                var order = await db.Orders.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(o => o.Id == row.OrderId);
                if (order == null)
                    throw new InvalidOperationException("La orden ya no existe en la base.");

                order.Status = newStatus;
                await db.SaveChangesAsync();
                AppLog.Information("Order {Budget} status: {Old} → {New}",
                    row.BudgetNumber, oldStatus, newStatus);
                await _auditService.LogAsync(row.OrderId, $"Estado: {oldStatus} → {newStatus}");

                RefreshCounts();
                _rowsView.Refresh();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "PersistStatusChange failed for order {OrderId}", row.OrderId);
                // Rollback visual sin volver a disparar la persistencia.
                row.SuppressPersist = true;
                row.Status = oldStatus;
                row.SuppressPersist = false;
                _dialogService.ShowError("Error",
                    $"No se pudo guardar el cambio de estado: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task RefreshAsync() => await InitializeAsync();

        [RelayCommand]
        private void ClearFilters()
        {
            FilterText = string.Empty;
            SelectedFilter = FilterOptions[0];
        }

        /// <summary>Bitácora de la orden: quién generó, editó o cambió estado y cuándo.</summary>
        [RelayCommand]
        private async Task ShowHistoryAsync(OrderPoolRow? row)
        {
            if (row == null) return;
            var events = await _auditService.GetForOrderAsync(row.OrderId);
            if (events.Count == 0)
            {
                _dialogService.ShowInfo($"Historial de {row.BudgetNumber}",
                    "Sin eventos registrados todavía. La bitácora registra generaciones, " +
                    "ediciones y cambios de estado desde que se activó la auditoría.");
                return;
            }

            var lines = events.Select(e =>
                $"{e.Timestamp.ToLocalTime():dd/MM/yyyy HH:mm}  ·  {e.UserName}  ·  {e.EventType}" +
                (string.IsNullOrWhiteSpace(e.Detail) ? "" : $"\n      {e.Detail}"));
            _dialogService.ShowInfo($"Historial de {row.BudgetNumber}", string.Join("\n\n", lines));
        }

        /// <summary>
        /// Repetir pedido: abre el armador con una COPIA de la orden (mismos productos y
        /// cliente, número/fechas en blanco, precios actualizados al catálogo vigente).
        /// El caso de uso estrella con clientes recurrentes: "lo mismo del año pasado".
        /// </summary>
        [RelayCommand]
        private async Task RepeatOrderAsync(OrderPoolRow? row)
        {
            if (row == null) return;
            var builder = _serviceProvider.GetRequiredService<BudgetBuilderViewModel>();
            await builder.LoadOrderCopyByIdAsync(row.OrderId);
            _navigationService.NavigateTo(builder);
        }

        /// <summary>Abre la orden en el armador de presupuestos para editarla.</summary>
        [RelayCommand]
        private async Task OpenOrderAsync(OrderPoolRow? row)
        {
            if (row == null) return;
            var builder = _serviceProvider.GetRequiredService<BudgetBuilderViewModel>();
            if (!await builder.LoadOrderForEditAsync(row.OrderId))
            {
                _dialogService.ShowWarning("Orden no encontrada", "La orden ya no existe en la base.");
                return;
            }
            _navigationService.NavigateTo(builder);
        }
    }
}
