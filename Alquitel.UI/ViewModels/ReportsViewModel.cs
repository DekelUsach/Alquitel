using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure;
using Alquitel.Infrastructure.Persistence;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
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

        // ── Gráficos (LiveCharts2) ───────────────────────────────────
        // Paleta alineada a los brushes del tema (SecondaryColor/Primary/Success) con
        // gris neutro para textos, legible tanto en tema claro como oscuro.
        private static readonly CultureInfo ArCulture = CultureInfo.GetCultureInfo("es-AR");
        private static readonly SKColor AccentSk = SKColor.Parse("#0D84E7");
        private static readonly SKColor PrimarySk = SKColor.Parse("#3B5998");
        private static readonly SKColor SuccessSk = SKColor.Parse("#27AE60");
        private static readonly SKColor MutedSk = SKColor.Parse("#8A93A2");

        private static SolidColorPaint TextPaint() => new(MutedSk);
        private static SolidColorPaint SeparatorPaint() => new(new SKColor(138, 147, 162, 40));
        private static string Money(double v) => v.ToString("C0", ArCulture);

        /// <summary>True: las tres analíticas se muestran como gráficos; false: grillas.</summary>
        [ObservableProperty]
        private bool _showCharts = true;

        [ObservableProperty] private ISeries[] _monthlySeries = Array.Empty<ISeries>();
        [ObservableProperty] private Axis[] _monthlyXAxes = Array.Empty<Axis>();
        [ObservableProperty] private Axis[] _monthlyYAxes = Array.Empty<Axis>();

        [ObservableProperty] private ISeries[] _clientSeries = Array.Empty<ISeries>();
        [ObservableProperty] private Axis[] _clientXAxes = Array.Empty<Axis>();
        [ObservableProperty] private Axis[] _clientYAxes = Array.Empty<Axis>();

        [ObservableProperty] private ISeries[] _productSeries = Array.Empty<ISeries>();
        [ObservableProperty] private Axis[] _productXAxes = Array.Empty<Axis>();
        [ObservableProperty] private Axis[] _productYAxes = Array.Empty<Axis>();

        /// <summary>Pintura del texto de la leyenda (los charts la toman por binding).</summary>
        public SolidColorPaint LegendPaint { get; } = TextPaint();

        private readonly IToastService _toastService;

        public ReportsViewModel(IDbContextFactory<AlquitelDbContext> dbContextFactory, IDialogService dialogService, IToastService toastService)
        {
            _dbContextFactory = dbContextFactory;
            _dialogService = dialogService;
            _toastService = toastService;

            // Rango inicial: últimos 12 meses calendario incluyendo el actual.
            var today = DateTime.Today;
            FromDate = new DateTime(today.Year, today.Month, 1).AddMonths(-11);
            ToDate = today;
        }

        public async Task InitializeAsync() => await LoadAsync();

        [RelayCommand]
        private async Task RefreshAsync() => await LoadAsync();

        /// <summary>Presets de rango rápido: "30", "90", "12m" o "year".</summary>
        [RelayCommand]
        private async Task SetRangeAsync(string preset)
        {
            var today = DateTime.Today;
            (FromDate, ToDate) = preset switch
            {
                "30" => (today.AddDays(-29), today),
                "90" => (today.AddDays(-89), today),
                "year" => (new DateTime(today.Year, 1, 1), today),
                _ => (new DateTime(today.Year, today.Month, 1).AddMonths(-11), (DateTime?)today),
            };
            await LoadAsync();
        }

        [RelayCommand]
        private void ShowChartsView() => ShowCharts = true;

        [RelayCommand]
        private void ShowTableView() => ShowCharts = false;

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
                BuildCharts();

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

            // Se agrupa por la descripción SIN tags BBCode: además de mostrarse limpia,
            // unifica versiones del mismo producto que solo difieren en estilos.
            var rows = orders
                .SelectMany(o => o.Items)
                .GroupBy(i => Alquitel.Core.Parsing.TagParser.StripTags(
                    i.Product?.Description ?? i.DescriptionSnapshot) ?? "(producto eliminado)")
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
        /// Reconstruye las series de los tres gráficos a partir de las filas ya agregadas.
        /// Se llama en cada LoadAsync; con colecciones vacías produce gráficos vacíos válidos.
        /// </summary>
        private void BuildCharts()
        {
            // ── Tendencia mensual: columnas de facturación ──
            MonthlySeries = new ISeries[]
            {
                new ColumnSeries<decimal>
                {
                    Name = "Facturación",
                    Values = MonthlyBilling.Select(m => m.Total).ToArray(),
                    Fill = new SolidColorPaint(AccentSk),
                    Rx = 6,
                    Ry = 6,
                }
            };
            MonthlyXAxes = new[]
            {
                new Axis
                {
                    Labels = MonthlyBilling.Select(m => m.MonthLabel).ToArray(),
                    LabelsRotation = -35,
                    LabelsPaint = TextPaint(),
                    TextSize = 11,
                    SeparatorsPaint = null,
                }
            };
            MonthlyYAxes = new[]
            {
                new Axis
                {
                    Labeler = Money,
                    LabelsPaint = TextPaint(),
                    TextSize = 11,
                    MinLimit = 0,
                    SeparatorsPaint = SeparatorPaint(),
                }
            };

            // ── Facturación por cliente: barras horizontales top 10 ──
            // Reverse: LiveCharts dibuja el índice 0 abajo; así el mayor queda arriba.
            var topClients = ClientBilling.Take(10).Reverse().ToList();
            ClientSeries = new ISeries[]
            {
                new RowSeries<decimal>
                {
                    Name = "Facturación",
                    Values = topClients.Select(c => c.Total).ToArray(),
                    Fill = new SolidColorPaint(PrimarySk),
                    Rx = 6,
                    Ry = 6,
                }
            };
            ClientYAxes = new[]
            {
                new Axis
                {
                    Labels = topClients.Select(c => Truncate(c.ClientName, 28)).ToArray(),
                    LabelsPaint = TextPaint(),
                    TextSize = 11,
                    SeparatorsPaint = null,
                }
            };
            ClientXAxes = new[]
            {
                new Axis
                {
                    Labeler = Money,
                    LabelsPaint = TextPaint(),
                    TextSize = 11,
                    MinLimit = 0,
                    SeparatorsPaint = SeparatorPaint(),
                }
            };

            // ── Rentabilidad por producto: facturación vs margen, top 10 por facturación ──
            var topProducts = ProductProfit.OrderByDescending(p => p.Revenue).Take(10).Reverse().ToList();
            ProductSeries = new ISeries[]
            {
                new RowSeries<decimal>
                {
                    Name = "Facturación",
                    Values = topProducts.Select(p => p.Revenue).ToArray(),
                    Fill = new SolidColorPaint(AccentSk),
                    Rx = 6,
                    Ry = 6,
                },
                new RowSeries<decimal>
                {
                    Name = "Margen",
                    Values = topProducts.Select(p => p.Margin ?? 0m).ToArray(),
                    Fill = new SolidColorPaint(SuccessSk),
                    Rx = 6,
                    Ry = 6,
                }
            };
            ProductYAxes = new[]
            {
                new Axis
                {
                    Labels = topProducts.Select(p => Truncate(p.Description, 32)).ToArray(),
                    LabelsPaint = TextPaint(),
                    TextSize = 11,
                    SeparatorsPaint = null,
                }
            };
            ProductXAxes = new[]
            {
                new Axis
                {
                    Labeler = Money,
                    LabelsPaint = TextPaint(),
                    TextSize = 11,
                    MinLimit = 0,
                    SeparatorsPaint = SeparatorPaint(),
                }
            };
        }

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..(max - 1)] + "…";

        /// <summary>
        /// Exporta las tres grillas a un único CSV con secciones (separador ';', BOM
        /// UTF-8), mismo formato que los exports de Clientes y Productos.
        /// </summary>
        [RelayCommand]
        private void ExportCsv()
        {
            if (ClientBilling.Count == 0 && MonthlyBilling.Count == 0 && ProductProfit.Count == 0)
            {
                _toastService.ShowInfo("No hay datos para exportar. Actualizá el reporte primero.");
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
