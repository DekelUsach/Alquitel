using Alquitel.Core.Entities;
using Alquitel.Mobile.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alquitel.Mobile.ViewModels;

/// <summary>Renglón del carrito de presupuesto en mobile.</summary>
public partial class CartItemViewModel : ObservableObject
{
    public Product Product { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Total))]
    private int _quantity = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Total))]
    private int _dias = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Total))]
    private decimal _unitPrice;

    [ObservableProperty]
    private string? _requestedMeasure;

    public decimal Total => Quantity * UnitPrice * Dias;

    public FormattedString FormattedDescription { get; }

    public event Action? TotalsChanged;

    public CartItemViewModel(Product product, int quantity = 1, int dias = 1, string? requestedMeasure = null)
    {
        Product = product;
        _quantity = Math.Max(1, quantity);
        _dias = Math.Max(1, dias);
        _unitPrice = product.BasePrice;
        _requestedMeasure = requestedMeasure;
        FormattedDescription = DescriptionFormatter.ToFormatted(product.Description, bold: true);
    }

    partial void OnQuantityChanged(int value) => TotalsChanged?.Invoke();
    partial void OnDiasChanged(int value) => TotalsChanged?.Invoke();
    partial void OnUnitPriceChanged(decimal value) => TotalsChanged?.Invoke();

    public OrderItem ToOrderItem(Guid orderId) => new()
    {
        OrderId = orderId,
        ProductId = Product.Id,
        Quantity = Quantity,
        Dias = Dias,
        UnitPrice = UnitPrice,
        RequestedMeasure = string.IsNullOrWhiteSpace(RequestedMeasure) ? null : RequestedMeasure.Trim(),
        DescriptionSnapshot = Product.Description,
        CustomFieldsJson = Product.CustomFieldsJson,
        ImagePath = Product.ImagePath,
    };
}
