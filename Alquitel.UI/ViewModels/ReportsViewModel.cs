using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure;
using Alquitel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Alquitel.UI.ViewModels
{
    /// <summary>Facturación agregada por cliente dentro del rango elegido.</summary>
    public record ClientBillingRow(string ClientName, int OrdersCount, decimal Total);

    /// <summary>Facturación agregada por mes calendario dentro del rango elegido.</summary>
    public record MonthlyBillingRow(string MonthLabel, int OrdersCount, decimal Total);

    /// <summary>
    /// Ranking de productos por facturación y margen. Margin es null cuando el producto
    /// no tiene Product.Cost cargado (no se puede calcular margen real).
    /// </summary>
    public record ProductProfitRow(int Rank, string Description, int TimesQuoted,
        decimal Revenue, decimal? Cost, decimal? Margin);

    /// <summary>
    /// §3 Reportes: facturación por cliente y por mes + rentabilidad de productos, con
    /// rango de fechas libre. Generaliza las queries fijas a 30 días del Dashboard.
    /// Se excluyen presupuestos Rechazados; el resto de los estados cuenta como facturado.
    /// </summary>
    public partial class ReportsViewModel : ObservableObject, IAsyncInitialization
    {
        private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
        private readonly IDialogService _dialogService;

        [ObservableProperty]
        private DateTime? _fromDate;

        [ObservableProperty]
        private DateTime? _toDate;

        [ObservableProperty]
        private decimal _totalRevenue;

        [ObservableProperty]
        private int _totalOrders;

        [ObservableProperty]
        private decimal _totalMargin;

        [ObservableProperty]
        private bool _hasProductsWithoutCost;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public ObservableCollection<ClientBillingRow> ClientBilling { get; } = new();
        public ObservableCollection<MonthlyBillingRow> MonthlyBilling { get; } = new();
        public ObservableCollection<ProductProfitRow> ProductProfit { get; } = new();

        public ReportsViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory, IDialogService dialogService)
        {
            _dbContextFactory = dbContextFactory;
            _dialogService = dialogService;

            // Rango inicial: últimos 12 meses calendario incluyendo el actual.
            var today = DateTime.Today;
            FromDate = new DateTime(today.Year, today.Month, 1).AddMonths(-11);
            ToDate = today;
        }

        public async Task InitializeAsync() => await LoadAsync();

        [RelayCommand]
        private async Task RefreshAsync() => await LoadAsync();

        private async Task LoadAsync()
        {
            if (FromDate == null || ToDate == null)
            {
                StatusMessage = "Elegí un rango de fechas válido.";
                return;
            }

            // CreatedDate se guarda en UTC; el rango elegido es en hora local.
            var fromUtc = FromDate.Value.Date.ToUniversalTime();
            var toUtc = ToDate.Value.Date.AddDays(1).ToUniversalTime();

            try
            {
                using var db = await _dbContextFactory.CreateDbContextAsync();

                var orders = await db.Orders.IgnoreQueryFilters().AsNoTracking()
                    .Include(o => o.Client)
                    .Include(o => o.Items).ThenInclude(i => i.Product)
                    .Where(o => o.CreatedDate >= fromUtc && o.CreatedDate < toUtc
                                && o.Status != OrderStatus.Rejected)
                    .ToListAsync();

                BuildClientBilling(orders);
                BuildMonthlyBilling(orders);
                BuildProductProfit(orders);

                TotalOrders = orders.Count;
                TotalRevenue = orders.Sum(o => o.Total);
                TotalMargin = ProductProfit.Where(p => p.Margin.HasValue).Sum(p => p.Margin!.Value);
                HasProductsWithoutCost = ProductProfit.Any(p => !p.Cost.HasValue);

                StatusMessage = $"{orders.Count} presupuestos en el rango (excluye rechazados).";
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Reports load failed");
                StatusMessage = $"✗ Error al cargar reportes: {ex.Message}";
            }
        }

        private void BuildClientBilling(List<Order> orders)
        {
            ClientBilling.Clear();
            var rows = orders
                .GroupBy(o => o.Client?.CompanyName ?? "(sin cliente)")
                .Select(g => new ClientBillingRow(g.Key, g.Count(), g.Sum(o => o.Total)))
                .OrderByDescending(r => r.Total);
            foreach (var r in rows) ClientBilling.Add(r);
        }

        private void BuildMonthlyBilling(List<Order> orders)
        {
            MonthlyBilling.Clear();
            var culture = CultureInfo.GetCultureInfo("es-AR");
            var rows = orders
                .GroupBy(o =>
                {
                    var local = o.CreatedDate.ToLocalTime();
                    return new DateTime(local.Year, local.Month, 1);
                })
                .OrderBy(g => g.Key)
                .Select(g => new MonthlyBillingRow(
                    g.Key.ToString("MMMM yyyy", culture),
                    g.Count(),
                    g.Sum(o => o.Total)));
            foreach (var r in rows) MonthlyBilling.Add(r);
        }

        private void BuildProductProfit(List<Order> orders)
        {
            ProductProfit.Clear();

            var rows = orders
                .SelectMany(o => o.Items)
                .GroupBy(i => i.Product?.Description ?? i.DescriptionSnapshot ?? "(producto eliminado)")
                .Select(g =>
                {
                    decimal revenue = g.Sum(i => i.Total);
                    var cost = g.Any(i => i.Product?.Cost == null)
                        ? (decimal?)null
                        : g.Sum(i => i.Product!.Cost!.Value * i.Quantity * Math.Max(1, i.Dias));
                    return new
                    {
                        Description = g.Key,
                        Times = g.Count(),
                        Revenue = revenue,
                        Cost = cost,
                        Margin = cost.HasValue ? revenue - cost.Value : (decimal?)null
                    };
                })
                .OrderByDescending(x => x.Margin ?? x.Revenue)
                .ToList();

            var rank = 1;
            foreach (var x in rows)
                ProductProfit.Add(new ProductProfitRow(rank++, x.Description, x.Times, x.Revenue, x.Cost, x.Margin));
        }

        /// <summary>
        /// Exporta las tres grillas a un único CSV con secciones (separador ';', BOM
        /// UTF-8), mismo formato que los exports de Clientes y Productos.
        /// </summary>
        [RelayCommand]
        private void ExportCsv()
        {
            if (ClientBilling.Count == 0 && MonthlyBilling.Count == 0 && ProductProfit.Count == 0)
            {
                _dialogService.ShowInfo("Exportar", "No hay datos para exportar. Actualizá el reporte primero.");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Archivo CSV (*.csv)|*.csv",
                FileName = $"Reporte_Alquitel_{FromDate:yyyyMMdd}_{ToDate:yyyyMMdd}.csv"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.AppendLine($"Reporte Alquitel;{FromDate:dd/MM/yyyy} a {ToDate:dd/MM/yyyy}");
                sb.AppendLine();

                sb.AppendLine("FACTURACIÓN POR CLIENTE");
                sb.AppendLine("Cliente;Presupuestos;Total");
                foreach (var r in ClientBilling)
                    sb.AppendLine(string.Join(";", CsvField(r.ClientName), r.OrdersCount, r.Total.ToString(inv)));
                sb.AppendLine();

                sb.AppendLine("FACTURACIÓN POR MES");
                sb.AppendLine("Mes;Presupuestos;Total");
                foreach (var r in MonthlyBilling)
                    sb.AppendLine(string.Join(";", CsvField(r.MonthLabel), r.OrdersCount, r.Total.ToString(inv)));
                sb.AppendLine();

                sb.AppendLine("RENTABILIDAD POR PRODUCTO");
                sb.AppendLine("Ranking;Producto;Veces presupuestado;Facturación;Costo;Margen");
                foreach (var r in ProductProfit)
                    sb.AppendLine(string.Join(";",
                        r.Rank, CsvField(r.Description), r.TimesQuoted,
                        r.Revenue.ToString(inv),
                        r.Cost?.ToString(inv) ?? "",
                        r.Margin?.ToString(inv) ?? ""));

                File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
                StatusMessage = "✓ Reporte exportado correctamente.";
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Reports ExportCsv failed");
                _dialogService.ShowError("Error al exportar", ex.Message);
            }
        }

        private static string CsvField(string? value)
        {
            var v = value ?? string.Empty;
            if (v.Contains(';') || v.Contains('"') || v.Contains('\n'))
                v = $"\"{v.Replace("\"", "\"\"")}\"";
            return v;
        }
    }
}
