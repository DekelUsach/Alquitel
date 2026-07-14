using System.Collections.ObjectModel;
using Alquitel.Core.Entities;
using Alquitel.Mobile.Data;
using Alquitel.Mobile.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Mobile.ViewModels;

public class ProductRow
{
    public required Product Product { get; init; }
    public required FormattedString FormattedDescription { get; init; }
    public string PriceText => $"$ {Product.BasePrice:N0}";
    public string Category => Product.Category;
}

public partial class CatalogViewModel : BaseViewModel
{
    private readonly IDbContextFactory<MobileDbContext> _factory;
    private List<Product> _all = new();

    public ObservableCollection<ProductRow> Products { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedCategory = "Todas";
    [ObservableProperty] private bool _isRefreshing;

    public CatalogViewModel(IDbContextFactory<MobileDbContext> factory) => _factory = factory;

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            using var db = _factory.CreateDbContext();
            _all = await db.Products.AsNoTracking().OrderBy(p => p.Description).ToListAsync();

            Categories.Clear();
            Categories.Add("Todas");
            foreach (var c in _all.Select(p => p.Category).Distinct().OrderBy(c => c))
                Categories.Add(c);

            ApplyFilter();
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

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = _all.AsEnumerable();

        if (SelectedCategory != "Todas")
            query = query.Where(p => p.Category == SelectedCategory);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.Trim();
            query = query.Where(p => p.Description.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        Products.Clear();
        foreach (var p in query)
            Products.Add(new ProductRow
            {
                Product = p,
                FormattedDescription = DescriptionFormatter.ToFormatted(p.Description, bold: true),
            });
    }

    [RelayCommand]
    private async Task OpenProductAsync(ProductRow? row)
    {
        if (row == null) return;
        await Shell.Current.GoToAsync("productdetail", new Dictionary<string, object> { ["productId"] = row.Product.Id });
    }
}
