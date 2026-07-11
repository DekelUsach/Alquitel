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

        public OrderStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                var old = _status;
                _status = value;
                OnPropertyChanged();
                if (!SuppressPersist)
                    _ = _onStatusChanged(this, old, value);
            }
        }

        public OrderPoolRow(Order order, Func<OrderPoolRow, OrderStatus, OrderStatus, Task> onStatusChanged)
        {
            _onStatusChanged = onStatusChanged;
            OrderId = order.Id;
            BudgetNumber = string.IsNullOrWhiteSpace(order.BudgetNumber) ? "(sin número)" : order.BudgetNumber;
            ClientName = order.Client?.CompanyName ?? "(sin cliente)";
            LocationName = order.Location?.Name ?? string.Empty;
            CreatedDate = order.CreatedDate.ToLocalTime();
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

        public OrderPoolViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory,
            IDialogService dialogService, INavigationService navigationService,
            IServiceProvider serviceProvider)
        {
            _dbContextFactory = dbContextFactory;
            _dialogService = dialogService;
            _navigationService = navigationService;
            _serviceProvider = serviceProvider;

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
                || row.AdminName.Contains(text, StringComparison.OrdinalIgnoreCase);
        }

        public async Task InitializeAsync()
        {
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
