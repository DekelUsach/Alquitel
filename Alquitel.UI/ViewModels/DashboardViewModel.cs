using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Infrastructure.Persistence;
using Alquitel.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Alquitel.UI.ViewModels
{
    /// <summary>Fila liviana para la lista de presupuestos recientes del dashboard.</summary>
    public record RecentOrderRow(Guid OrderId, string BudgetNumber, string ClientName,
        DateTime CreatedDate, decimal Total, string StatusLabel);

    /// <summary>Fila liviana para el ranking de productos más presupuestados.</summary>
    public record TopProductRow(int Rank, string Description, int TimesQuoted);

    public partial class DashboardViewModel : ObservableObject, IAsyncInitialization
    {
        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
        private readonly INavigationService _navigationService;
        private readonly IServiceProvider _serviceProvider;

        [ObservableProperty]
        private int _totalProducts;

        [ObservableProperty]
        private int _totalClients;

        [ObservableProperty]
        private int _totalOrders;

        [ObservableProperty]
        private string _welcomeMessage = string.Empty;

        // ── Métricas extendidas ──────────────────────────────────────

        [ObservableProperty]
        private decimal _revenueLast30Days;

        [ObservableProperty]
        private int _ordersLast30Days;

        public ObservableCollection<RecentOrderRow> RecentOrders { get; } = new();

        public ObservableCollection<TopProductRow> TopProducts { get; } = new();

        public string TodayLabel => DateTime.Now.ToString("dddd d 'de' MMMM yyyy",
            System.Globalization.CultureInfo.GetCultureInfo("es-AR"));

        public DashboardViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory,
            INavigationService navigationService, IServiceProvider serviceProvider)
        {
            _dbContextFactory = dbContextFactory;
            _navigationService = navigationService;
            _serviceProvider = serviceProvider;

            var hour = DateTime.Now.Hour;
            WelcomeMessage = hour switch
            {
                < 12 => "Buenos días",
                < 18 => "Buenas tardes",
                _ => "Buenas noches"
            };
        }

        // Metrics load happens here (invoked by NavigationService) instead of the
        // constructor, so navigation never blocks on the database.
        public async System.Threading.Tasks.Task InitializeAsync() => await LoadMetricsAsync();

        private async System.Threading.Tasks.Task LoadMetricsAsync()
        {
            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();
                TotalProducts = await db.Products.CountAsync();
                TotalClients = await db.Clients.CountAsync();
                TotalOrders = await db.Orders.CountAsync();

                await LoadExtendedMetricsAsync(db);
            }
            catch (Exception ex)
            {
                Alquitel.Infrastructure.AppLog.Warning(ex, "Dashboard metrics load failed");
                TotalProducts = 0;
                TotalClients = 0;
                TotalOrders = 0;
            }
        }

        private async System.Threading.Tasks.Task LoadExtendedMetricsAsync(AlquitelDbContext db)
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);

            // IgnoreQueryFilters: el historial debe incluir órdenes de clientes o
            // productos archivados, igual que en PresupuestosViewModel.
            var recentWindow = await db.Orders.IgnoreQueryFilters()
                .Include(o => o.Client)
                .Include(o => o.Items)
                .Where(o => o.CreatedDate >= cutoff)
                .ToListAsync();

            OrdersLast30Days = recentWindow.Count;
            RevenueLast30Days = recentWindow.Sum(o => o.Total);

            var lastOrders = await db.Orders.IgnoreQueryFilters()
                .Include(o => o.Client)
                .Include(o => o.Items)
                .OrderByDescending(o => o.CreatedDate)
                .Take(6)
                .ToListAsync();

            var topRaw = await db.Set<Alquitel.Core.Entities.OrderItem>().IgnoreQueryFilters()
                .Include(i => i.Product)
                .Where(i => i.Product != null)
                .GroupBy(i => i.Product!.Description)
                .Select(g => new { Description = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToListAsync();

            RecentOrders.Clear();
            foreach (var o in lastOrders)
            {
                RecentOrders.Add(new RecentOrderRow(
                    o.Id,
                    string.IsNullOrWhiteSpace(o.BudgetNumber) ? "(sin número)" : o.BudgetNumber,
                    o.Client?.CompanyName ?? "(sin cliente)",
                    o.CreatedDate.ToLocalTime(),
                    o.Total,
                    StatusLabel(o.Status)));
            }

            TopProducts.Clear();
            var rank = 1;
            foreach (var t in topRaw)
                TopProducts.Add(new TopProductRow(rank++,
                    Alquitel.Core.Parsing.TagParser.StripTags(t.Description) ?? t.Description,
                    t.Count));
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

        /// <summary>
        /// Repetir pedido: abre el armador de presupuestos con una copia de la orden
        /// (mismos productos y cliente, número y fechas en blanco).
        /// </summary>
        [RelayCommand]
        private async System.Threading.Tasks.Task RepeatOrderAsync(RecentOrderRow? row)
        {
            if (row == null) return;
            var builder = _serviceProvider.GetRequiredService<BudgetBuilderViewModel>();
            await builder.LoadOrderCopyByIdAsync(row.OrderId);
            _navigationService.NavigateTo(builder);
        }

        /// <summary>
        /// Tocar un presupuesto de "Actividad reciente" lo abre en el armador para
        /// verlo o editarlo (conserva número, fechas y estado).
        /// </summary>
        [RelayCommand]
        private async System.Threading.Tasks.Task OpenOrderAsync(RecentOrderRow? row)
        {
            if (row == null) return;
            var builder = _serviceProvider.GetRequiredService<BudgetBuilderViewModel>();
            if (!await builder.LoadOrderForEditAsync(row.OrderId)) return;
            _navigationService.NavigateTo(builder);
        }

        [RelayCommand]
        private void NewBudget() => _navigationService.NavigateTo<BudgetBuilderViewModel>();

        [RelayCommand]
        private void GoToPresupuestos() => _navigationService.NavigateTo<PresupuestosViewModel>();

        [RelayCommand]
        private void GoToProducts() => _navigationService.NavigateTo<ProductEditorViewModel>();

        [RelayCommand]
        private void GoToClients() => _navigationService.NavigateTo<ClientsViewModel>();

        [RelayCommand]
        private async System.Threading.Tasks.Task RefreshAsync() => await LoadMetricsAsync();
    }
}
