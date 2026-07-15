using System.Collections.ObjectModel;
using Alquitel.Core.Entities;
using Alquitel.Mobile.Data;
using Alquitel.Mobile.Helpers;
using Alquitel.Mobile.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Mobile.ViewModels;

/// <summary>Renglón de detalle de orden con descripción renderizada.</summary>
public class OrderItemRow
{
    public required OrderItem Item { get; init; }
    public required FormattedString FormattedDescription { get; init; }
    public string QuantityLine => $"{Item.Quantity} u. × {Item.Dias} día(s) × $ {Item.UnitPrice:N0}";
    public string TotalText => $"$ {Item.Total:N0}";
    public string? Measure => string.IsNullOrWhiteSpace(Item.RequestedMeasure) ? null : $"Medida solicitada: {Item.RequestedMeasure}";
}

[QueryProperty(nameof(OrderId), "orderId")]
public partial class OrderDetailViewModel : BaseViewModel
{
    private readonly IDbContextFactory<MobileDbContext> _factory;
    private readonly OrderService _orders;
    private readonly ApprovalService _approvals;
    private readonly SessionService _session;

    [ObservableProperty] private Guid _orderId;
    [ObservableProperty] private Order? _order;
    [ObservableProperty] private string _statusDisplay = string.Empty;
    [ObservableProperty] private Color _statusColor = Colors.Gray;
    [ObservableProperty] private string _clientName = string.Empty;
    [ObservableProperty] private string _locationName = string.Empty;
    [ObservableProperty] private string _eventDateText = string.Empty;
    [ObservableProperty] private bool _canChangeOrderStatus;
    [ObservableProperty] private bool _canShareApproval;

    public ObservableCollection<OrderItemRow> Items { get; } = new();
    public ObservableCollection<OrderApproval> Approvals { get; } = new();
    public ObservableCollection<OrderAuditEvent> AuditEvents { get; } = new();

    public List<string> StatusOptions { get; } = OrderStatusDisplay.All.Select(s => s.ToDisplay()).ToList();

    public OrderDetailViewModel(
        IDbContextFactory<MobileDbContext> factory,
        OrderService orders,
        ApprovalService approvals,
        SessionService session)
    {
        _factory = factory;
        _orders = orders;
        _approvals = approvals;
        _session = session;
    }

    partial void OnOrderIdChanged(Guid value) => _ = LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (OrderId == Guid.Empty || IsBusy) return;
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            CanChangeOrderStatus = _session.CanChangeOrderStatus;
            CanShareApproval = _session.CanCreateBudgets;

            using var db = _factory.CreateDbContext();
            Order = await db.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Client)
                .Include(o => o.Location)
                .Include(o => o.Items)
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == OrderId);

            if (Order == null)
            {
                ErrorMessage = "La orden ya no existe.";
                return;
            }

            StatusDisplay = Order.Status.ToDisplay();
            StatusColor = Application.Current?.Resources.TryGetValue(Order.Status.ToColorKey(), out var c) == true && c is Color color
                ? color : Colors.Gray;
            ClientName = Order.Client?.CompanyName ?? "—";
            LocationName = Order.Location?.Name ?? "—";
            EventDateText = Order.EventDate is { } d
                ? (Order.EventEndDate is { } e && e.Date != d.Date
                    ? $"{d:dd/MM/yyyy} al {e:dd/MM/yyyy}"
                    : d.ToString("dd/MM/yyyy"))
                : "Sin fecha";

            Items.Clear();
            foreach (var item in Order.Items)
                Items.Add(new OrderItemRow
                {
                    Item = item,
                    FormattedDescription = DescriptionFormatter.ToFormatted(item.DescriptionSnapshot, bold: true),
                });

            Approvals.Clear();
            foreach (var a in await _approvals.GetForOrderAsync(OrderId)) Approvals.Add(a);

            AuditEvents.Clear();
            foreach (var audit in await _orders.GetAuditAsync(OrderId)) AuditEvents.Add(audit);
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

    [RelayCommand]
    private async Task ChangeStatusAsync()
    {
        if (Order == null || !_session.CanChangeOrderStatus) return;

        var choice = await Shell.Current.DisplayActionSheet(
            "Cambiar estado", "Cancelar", null, StatusOptions.ToArray());
        if (string.IsNullOrEmpty(choice) || choice == "Cancelar") return;

        var newStatus = OrderStatusDisplay.All.FirstOrDefault(s => s.ToDisplay() == choice);
        if (newStatus == Order.Status) return;

        try
        {
            IsBusy = true;
            await _orders.ChangeStatusAsync(OrderId, newStatus);
            await LoadAsync();
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
    private async Task ShareApprovalLinkAsync()
    {
        if (Order == null || !_session.CanCreateBudgets) return;
        try
        {
            IsBusy = true;
            var url = await _approvals.GetOrCreateLinkAsync(OrderId);
            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Title = $"Aprobación presupuesto Nº {Order.BudgetNumber}",
                Text = $"Presupuesto Nº {Order.BudgetNumber} - Alquitel\nRevisalo y aprobalo acá: {url}",
            });
            await LoadAsync();
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
}
