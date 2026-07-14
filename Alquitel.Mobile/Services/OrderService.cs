using Alquitel.Core.Entities;
using Alquitel.Core.Helpers;
using Alquitel.Mobile.Data;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Mobile.Services;

/// <summary>
/// Persistencia de órdenes y bitácora desde mobile. Espejo acotado de
/// OrderPersistenceService + EfOrderAuditService del desktop.
/// </summary>
public class OrderService
{
    private readonly IDbContextFactory<MobileDbContext> _factory;
    private readonly SessionService _session;

    public OrderService(IDbContextFactory<MobileDbContext> factory, SessionService session)
    {
        _factory = factory;
        _session = session;
    }

    public async Task<string> NextBudgetNumberAsync()
    {
        using var db = _factory.CreateDbContext();
        var numbers = await db.Orders.Select(o => o.BudgetNumber).ToListAsync();
        return BudgetNumberHelper.NextSerial(numbers);
    }

    /// <summary>Crea la orden con sus ítems (snapshot de descripción incluido) y registra el evento en la bitácora.</summary>
    public async Task CreateOrderAsync(Order order)
    {
        using var db = _factory.CreateDbContext();
        db.Orders.Add(order);
        db.OrderAuditEvents.Add(NewAudit(order.Id, "Creado", $"Presupuesto {order.BudgetNumber} creado desde mobile"));
        await db.SaveChangesAsync();
    }

    public async Task ChangeStatusAsync(Guid orderId, OrderStatus newStatus)
    {
        using var db = _factory.CreateDbContext();
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId)
            ?? throw new InvalidOperationException("La orden ya no existe.");

        var old = order.Status;
        if (old == newStatus) return;

        order.Status = newStatus;
        order.RowVersion = Guid.NewGuid();
        db.OrderAuditEvents.Add(NewAudit(orderId, $"Estado: {old} → {newStatus}", "Cambio de estado desde mobile"));
        await db.SaveChangesAsync();
    }

    public async Task<List<OrderAuditEvent>> GetAuditAsync(Guid orderId)
    {
        using var db = _factory.CreateDbContext();
        return await db.OrderAuditEvents
            .Where(e => e.OrderId == orderId)
            .OrderByDescending(e => e.Timestamp)
            .AsNoTracking()
            .ToListAsync();
    }

    private OrderAuditEvent NewAudit(Guid orderId, string eventType, string? detail) => new()
    {
        OrderId = orderId,
        UserName = _session.UserName,
        UserId = _session.CurrentUser?.Id,
        EventType = eventType,
        Detail = detail,
    };
}
