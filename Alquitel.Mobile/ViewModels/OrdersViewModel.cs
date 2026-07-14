using System.Collections.ObjectModel;
using Alquitel.Core.Entities;
using Alquitel.Mobile.Data;
using Alquitel.Mobile.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Mobile.ViewModels;

public partial class OrdersViewModel : BaseViewModel
{
    private readonly IDbContextFactory<MobileDbContext> _factory;
    private List<Order> _all = new();

    public ObservableCollection<Order> Orders { get; } = new();

    /// <summary>"Todos" + estados en español.</summary>
    public List<string> StatusFilters { get; } =
        new[] { "Todos" }.Concat(OrderStatusDisplay.All.Select(s => s.ToDisplay())).ToList();

    [ObservableProperty] private string _selectedStatusFilter = "Todos";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isRefreshing;

    public OrdersViewModel(IDbContextFactory<MobileDbContext> factory) => _factory = factory;

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            using var db = _factory.CreateDbContext();
            _all = await db.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Client)
                .Include(o => o.Items)
                .OrderByDescending(o => o.CreatedDate)
                .Take(300)
                .AsNoTracking()
                .ToListAsync();
            ApplyFilters();
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

    partial void OnSelectedStatusFilterChanged(string value) => ApplyFilters();
    partial void OnSearchTextChanged(string value) => ApplyFilters();

    private void ApplyFilters()
    {
        var query = _all.AsEnumerable();

        if (SelectedStatusFilter != "Todos")
            query = query.Where(o => o.Status.ToDisplay() == SelectedStatusFilter);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.Trim();
            query = query.Where(o =>
                o.BudgetNumber.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                (o.Client?.CompanyName.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        Orders.Clear();
        foreach (var o in query) Orders.Add(o);
    }

    [RelayCommand]
    private async Task OpenOrderAsync(Order? order)
    {
        if (order == null) return;
        await Shell.Current.GoToAsync("orderdetail", new Dictionary<string, object> { ["orderId"] = order.Id });
    }
}
