using System.Collections.ObjectModel;
using Alquitel.Core.Entities;
using Alquitel.Mobile.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

using Alquitel.Mobile.Services;

namespace Alquitel.Mobile.ViewModels;

public class MonthRow
{
    public string Month { get; init; } = string.Empty;
    public int Count { get; init; }
    public decimal Total { get; init; }
    public string TotalText => $"$ {Total:N0}";
}

public class TopProductRow
{
    public string Description { get; init; } = string.Empty;
    public int Quantity { get; init; }
}

public partial class ReportsViewModel : BaseViewModel
{
    private readonly IDbContextFactory<MobileDbContext> _factory;
    private readonly SessionService _session;

    [ObservableProperty] private int _totalOrders;
    [ObservableProperty] private string _conversionText = "—";
    [ObservableProperty] private bool _isRefreshing;

    public ObservableCollection<MonthRow> Months { get; } = new();
    public ObservableCollection<TopProductRow> TopProducts { get; } = new();

    public ReportsViewModel(IDbContextFactory<MobileDbContext> factory, SessionService session)
    {
        _factory = factory;
        _session = session;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!_session.CanSeeReports)
        {
            await ShowAlertAsync("Acceso denegado", "No tienes permisos para ver reportes.");
            await Shell.Current.GoToAsync("//main/dashboard");
            return;
        }
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            using var db = _factory.CreateDbContext();

            var since = DateTime.UtcNow.AddMonths(-6);
            var orders = await db.Orders
                .IgnoreQueryFilters()
                .Where(o => o.CreatedDate >= since)
                .Include(o => o.Items)
                .AsNoTracking()
                .ToListAsync();

            TotalOrders = orders.Count;

            // Conversión: aprobados o avanzados sobre el total no archivado.
            var considered = orders.Where(o => o.Status != OrderStatus.Archived).ToList();
            var converted = considered.Count(o => o.Status is OrderStatus.Approved or OrderStatus.SentToOF or OrderStatus.SentToOT);
            ConversionText = considered.Count > 0
                ? $"{(double)converted / considered.Count:P0} ({converted} de {considered.Count})"
                : "Sin datos";

            var culture = new System.Globalization.CultureInfo("es-AR");
            Months.Clear();
            foreach (var g in orders
                         .GroupBy(o => new { o.CreatedDate.Year, o.CreatedDate.Month })
                         .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month))
            {
                var name = culture.TextInfo.ToTitleCase(
                    new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMMM yyyy", culture));
                Months.Add(new MonthRow
                {
                    Month = name,
                    Count = g.Count(),
                    Total = g.Where(o => o.Status != OrderStatus.Rejected && o.Status != OrderStatus.Archived)
                             .Sum(o => o.GrandTotal),
                });
            }

            // Top 5 productos por unidades (usando el snapshot para órdenes históricas).
            var itemsWithDesc = await (
                from i in db.OrderItems
                join o in db.Orders.IgnoreQueryFilters() on i.OrderId equals o.Id
                where o.CreatedDate >= since
                select new { i.DescriptionSnapshot, i.Quantity })
                .AsNoTracking()
                .ToListAsync();

            TopProducts.Clear();
            foreach (var g in itemsWithDesc
                         .Where(i => !string.IsNullOrWhiteSpace(i.DescriptionSnapshot))
                         .GroupBy(i => StripTags(i.DescriptionSnapshot!))
                         .Select(g => new TopProductRow { Description = g.Key, Quantity = g.Sum(i => i.Quantity) })
                         .OrderByDescending(r => r.Quantity)
                         .Take(5))
                TopProducts.Add(g);
        }
        catch (Exception ex)
        {
            ErrorMessage = DescribeDbError(ex);
        }
        finally
        {
            IsBusy = false;
            IsRefreshing = false;
        }
    }

    private static string StripTags(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\[/?[a-zA-Z]+\]", string.Empty).Trim();
}
