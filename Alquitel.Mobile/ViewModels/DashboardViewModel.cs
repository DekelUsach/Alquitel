using System.Collections.ObjectModel;
using Alquitel.Core.Entities;
using Alquitel.Mobile.Data;
using Alquitel.Mobile.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Mobile.ViewModels;

public partial class DashboardViewModel : BaseViewModel
{
    private readonly IDbContextFactory<MobileDbContext> _factory;
    private readonly SessionService _session;

    [ObservableProperty] private string _greeting = string.Empty;
    [ObservableProperty] private int _monthOrders;
    [ObservableProperty] private decimal _monthTotal;
    [ObservableProperty] private int _pendingApprovals;
    [ObservableProperty] private int _approvedOrders;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _canCreateBudgets;

    public ObservableCollection<Order> RecentOrders { get; } = new();

    public DashboardViewModel(IDbContextFactory<MobileDbContext> factory, SessionService session)
    {
        _factory = factory;
        _session = session;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            Greeting = $"Hola, {_session.UserName}";
            CanCreateBudgets = _session.CanCreateBudgets;

            using var db = _factory.CreateDbContext();

            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            MonthOrders = await db.Orders.CountAsync(o => o.CreatedDate >= monthStart);
            PendingApprovals = await db.OrderApprovals.CountAsync(a => a.Status == ApprovalStatus.Pending);
            ApprovedOrders = await db.Orders.CountAsync(o => o.Status == OrderStatus.Approved);

            var monthOrders = await db.Orders
                .IgnoreQueryFilters()
                .Where(o => o.CreatedDate >= monthStart && o.Status != OrderStatus.Rejected && o.Status != OrderStatus.Archived)
                .Include(o => o.Items)
                .AsNoTracking()
                .ToListAsync();
            MonthTotal = monthOrders.Sum(o => o.GrandTotal);

            var recent = await db.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Client)
                .Include(o => o.Items)
                .OrderByDescending(o => o.CreatedDate)
                .Take(10)
                .AsNoTracking()
                .ToListAsync();

            RecentOrders.Clear();
            foreach (var o in recent) RecentOrders.Add(o);
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

    [RelayCommand]
    private async Task OpenOrderAsync(Order? order)
    {
        if (order == null) return;
        await Shell.Current.GoToAsync("orderdetail", new Dictionary<string, object> { ["orderId"] = order.Id });
    }

    [RelayCommand]
    private async Task NewBudgetAsync()
    {
        if (!_session.CanCreateBudgets) return;
        await Shell.Current.GoToAsync("//main/budget");
    }
}
