using System.Collections.ObjectModel;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;
using Alquitel.Core.Search;
using Alquitel.Mobile.Data;
using Alquitel.Mobile.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Location = Alquitel.Core.Entities.Location;

namespace Alquitel.Mobile.ViewModels;

/// <summary>
/// Armado de presupuestos desde el celular: texto natural → IA (Pollinations) con
/// fallback al ProductMatcher local de Core; carrito editable; guarda la orden en el
/// pool compartido. El documento Word lo genera después la app de escritorio.
/// </summary>
public partial class BudgetBuilderViewModel : BaseViewModel
{
    private readonly IDbContextFactory<MobileDbContext> _factory;
    private readonly SessionService _session;
    private readonly OrderService _orders;
    private readonly IAiOrderParser _aiParser;

    private List<Product> _catalog = new();

    public ObservableCollection<CartItemViewModel> Cart { get; } = new();
    public ObservableCollection<Client> Clients { get; } = new();
    public ObservableCollection<Location> Locations { get; } = new();
    public ObservableCollection<Product> SearchResults { get; } = new();

    [ObservableProperty] private string _smartText = string.Empty;
    [ObservableProperty] private string? _analysisMessage;
    [ObservableProperty] private Client? _selectedClient;
    [ObservableProperty] private Location? _selectedLocation;
    [ObservableProperty] private DateTime _eventDate = DateTime.Today;
    [ObservableProperty] private bool _hasEndDate;
    [ObservableProperty] private DateTime _eventEndDate = DateTime.Today;
    [ObservableProperty] private string? _comments;
    [ObservableProperty] private string _discountPercentText = "0";
    [ObservableProperty] private bool _addVat;
    [ObservableProperty] private string _productSearch = string.Empty;
    [ObservableProperty] private decimal _cartTotal;
    [ObservableProperty] private decimal _grandTotal;
    [ObservableProperty] private bool _hasItems;

    public BudgetBuilderViewModel(
        IDbContextFactory<MobileDbContext> factory,
        SessionService session,
        OrderService orders,
        IAiOrderParser aiParser)
    {
        _factory = factory;
        _session = session;
        _orders = orders;
        _aiParser = aiParser;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            ErrorMessage = null;
            using var db = _factory.CreateDbContext();
            _catalog = await db.Products.AsNoTracking().OrderBy(p => p.Description).ToListAsync();

            var clients = await db.Clients.AsNoTracking().OrderBy(c => c.CompanyName).ToListAsync();
            var selectedClientId = SelectedClient?.Id;
            Clients.Clear();
            foreach (var c in clients) Clients.Add(c);
            if (selectedClientId != null)
                SelectedClient = Clients.FirstOrDefault(c => c.Id == selectedClientId);

            var locations = await db.Locations.AsNoTracking().OrderBy(l => l.Name).ToListAsync();
            var selectedLocationId = SelectedLocation?.Id;
            Locations.Clear();
            foreach (var l in locations) Locations.Add(l);
            if (selectedLocationId != null)
                SelectedLocation = Locations.FirstOrDefault(l => l.Id == selectedLocationId);
        }
        catch (Exception ex)
        {
            ErrorMessage = DescribeDbError(ex);
        }
    }

    partial void OnSelectedClientChanged(Client? value)
    {
        if (value?.SpecialDiscountPercent is > 0)
            DiscountPercentText = value.SpecialDiscountPercent.Value.ToString("0.##");
    }

    // ── Análisis de texto natural ────────────────────────────────

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrWhiteSpace(SmartText))
        {
            AnalysisMessage = "Pegá el pedido del cliente (mail o WhatsApp) para analizarlo.";
            return;
        }
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            AnalysisMessage = null;
            if (_catalog.Count == 0) await LoadAsync();
            if (_catalog.Count == 0)
            {
                AnalysisMessage = "El catálogo está vacío.";
                return;
            }

            var matcher = new ProductMatcher(_catalog, AppConfig.SmartSearchStopWords,
                AppConfig.SmartSearchThreshold, AppConfig.SmartSearchMargin);

            int added = 0;
            var notes = new List<string>();

            if (_aiParser.IsConfigured)
            {
                var candidates = matcher.SelectAiCandidates(SmartText);
                var aiCatalog = candidates
                    .Select((p, i) => new AiCatalogProduct(i, p.Description, p.Category))
                    .ToList();

                var result = await _aiParser.ParseOrderAsync(SmartText, aiCatalog);
                if (result != null)
                {
                    foreach (var item in result.Items)
                    {
                        AddToCart(candidates[item.Ref], item.Quantity, result.Days ?? 1, item.RequestedMeasure);
                        added++;
                    }
                    if (result.Unmatched.Count > 0)
                        notes.Add($"Sin coincidencia: {string.Join(", ", result.Unmatched)}");
                }
                else
                {
                    added = AnalyzeLocally(matcher);
                    notes.Add("La IA no respondió; se usó el buscador local.");
                }
            }
            else
            {
                added = AnalyzeLocally(matcher);
            }

            AnalysisMessage = added > 0
                ? $"Se agregaron {added} producto(s) al carrito." + (notes.Count > 0 ? "\n" + string.Join("\n", notes) : string.Empty)
                : "No se detectaron productos del catálogo en el texto." + (notes.Count > 0 ? "\n" + string.Join("\n", notes) : string.Empty);
            if (added > 0) SmartText = string.Empty;
        }
        catch (Exception ex)
        {
            AnalysisMessage = $"Error al analizar: {ex.GetBaseException().Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private int AnalyzeLocally(ProductMatcher matcher)
    {
        int added = 0;
        foreach (var match in matcher.FindMatches(SmartText))
        {
            AddToCart(match.Product, match.Quantity, 1, null);
            added++;
        }
        return added;
    }

    // ── Búsqueda manual de productos ─────────────────────────────

    partial void OnProductSearchChanged(string value)
    {
        SearchResults.Clear();
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2) return;

        var q = ProductMatcher.NormalizeText(value);
        foreach (var p in _catalog
                     .Where(p => ProductMatcher.NormalizeText(p.Description).Contains(q) ||
                                 ProductMatcher.NormalizeText(p.Category).Contains(q))
                     .Take(8))
            SearchResults.Add(p);
    }

    [RelayCommand]
    private void AddProduct(Product? product)
    {
        if (product == null) return;
        AddToCart(product, 1, 1, null);
        ProductSearch = string.Empty;
        SearchResults.Clear();
    }

    private void AddToCart(Product product, int quantity, int dias, string? measure)
    {
        var existing = Cart.FirstOrDefault(c => c.Product.Id == product.Id);
        if (existing != null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            var item = new CartItemViewModel(product, quantity, dias, measure);
            item.TotalsChanged += RecalculateTotals;
            Cart.Add(item);
        }
        RecalculateTotals();
    }

    [RelayCommand]
    private void RemoveItem(CartItemViewModel? item)
    {
        if (item == null) return;
        item.TotalsChanged -= RecalculateTotals;
        Cart.Remove(item);
        RecalculateTotals();
    }

    private void RecalculateTotals()
    {
        CartTotal = Cart.Sum(c => c.Total);
        HasItems = Cart.Count > 0;

        decimal discount = 0;
        if (decimal.TryParse(DiscountPercentText.Replace(',', '.'),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pct))
            discount = CartTotal * Math.Clamp(pct, 0m, 100m) / 100m;

        var net = CartTotal - discount;
        GrandTotal = AddVat ? Math.Round(net * (1 + Order.VatRate), 2) : net;
    }

    partial void OnDiscountPercentTextChanged(string value) => RecalculateTotals();
    partial void OnAddVatChanged(bool value) => RecalculateTotals();

    // ── Guardado ─────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Cart.Count == 0)
        {
            await ShowAlertAsync("Presupuesto", "El carrito está vacío.");
            return;
        }
        if (SelectedClient == null)
        {
            await ShowAlertAsync("Presupuesto", "Elegí el cliente.");
            return;
        }
        if (SelectedLocation == null)
        {
            await ShowAlertAsync("Presupuesto", "Elegí la ubicación del evento.");
            return;
        }
        if (IsBusy) return;

        try
        {
            IsBusy = true;

            decimal.TryParse(DiscountPercentText.Replace(',', '.'),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pct);

            var order = new Order
            {
                BudgetNumber = await _orders.NextBudgetNumberAsync(),
                AdminName = _session.UserName,
                CreatedByUserId = _session.CurrentUser?.Id,
                ClientId = SelectedClient.Id,
                LocationId = SelectedLocation.Id,
                EventDate = DateTime.SpecifyKind(EventDate, DateTimeKind.Utc),
                EventEndDate = HasEndDate ? DateTime.SpecifyKind(EventEndDate, DateTimeKind.Utc) : null,
                Comments = string.IsNullOrWhiteSpace(Comments) ? null : Comments.Trim(),
                DiscountPercent = Math.Clamp(pct, 0m, 100m),
                AddVat = AddVat,
                Status = OrderStatus.Draft,
            };
            order.Items = Cart.Select(c => c.ToOrderItem(order.Id)).ToList();

            await _orders.CreateOrderAsync(order);

            var number = order.BudgetNumber;
            ClearForm();
            await ShowAlertAsync("Listo", $"Presupuesto Nº {number} guardado en el pool. El documento Word se genera desde la app de escritorio.");
            await Shell.Current.GoToAsync("//main/orders");
        }
        catch (Exception ex)
        {
            await ShowAlertAsync("Error", DescribeDbError(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearCartAsync()
    {
        if (Cart.Count == 0) return;
        if (!await ConfirmAsync("Vaciar carrito", "¿Descartar todos los productos del carrito?")) return;
        ClearForm();
    }

    private void ClearForm()
    {
        foreach (var item in Cart) item.TotalsChanged -= RecalculateTotals;
        Cart.Clear();
        SmartText = string.Empty;
        AnalysisMessage = null;
        Comments = null;
        DiscountPercentText = "0";
        AddVat = false;
        HasEndDate = false;
        RecalculateTotals();
    }
}
