using System.Text.Json;
using Alquitel.Core.Entities;
using Alquitel.Mobile.Data;
using Alquitel.Mobile.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Mobile.ViewModels;

public class CustomFieldRow
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Display => string.IsNullOrEmpty(Value) ? Label : $"{Label}: {Value}";
}

[QueryProperty(nameof(ProductId), "productId")]
public partial class ProductDetailViewModel : BaseViewModel
{
    private readonly IDbContextFactory<MobileDbContext> _factory;

    [ObservableProperty] private Guid _productId;
    [ObservableProperty] private FormattedString? _formattedDescription;
    [ObservableProperty] private string _category = string.Empty;
    [ObservableProperty] private string _priceText = string.Empty;
    [ObservableProperty] private string _stockText = string.Empty;
    [ObservableProperty] private List<CustomFieldRow> _customFields = new();

    public ProductDetailViewModel(IDbContextFactory<MobileDbContext> factory) => _factory = factory;

    partial void OnProductIdChanged(Guid value) => _ = LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (ProductId == Guid.Empty) return;
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            using var db = _factory.CreateDbContext();
            var product = await db.Products.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(p => p.Id == ProductId);
            if (product == null)
            {
                ErrorMessage = "El producto ya no existe.";
                return;
            }

            FormattedDescription = DescriptionFormatter.ToFormatted(product.Description, bold: true, fontSize: 18);
            Category = product.Category;
            PriceText = $"$ {product.BasePrice:N2}";
            StockText = product.StockQuantity is { } stock ? $"{stock} unidades" : "Sin control de stock";
            CustomFields = ParseCustomFields(product.CustomFieldsJson);
        }
        catch (Exception ex)
        {
            ErrorMessage = DescribeDbError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static List<CustomFieldRow> ParseCustomFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var fields = JsonSerializer.Deserialize<List<CustomFieldDefinition>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            return fields?
                .Select(f => new CustomFieldRow { Label = f.Label ?? string.Empty, Value = f.Value ?? string.Empty })
                .Where(f => !string.IsNullOrWhiteSpace(f.Display))
                .ToList() ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
