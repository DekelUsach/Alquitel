using Alquitel.Core.Entities;
using Alquitel.Core.Helpers;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Infrastructure.Services;

public sealed class OrderStatusService : IOrderStatusService
{
    private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUser;

    public OrderStatusService(
        IDbContextFactory<AlquitelDbContext> dbContextFactory,
        ICurrentUserService currentUser)
    {
        _dbContextFactory = dbContextFactory;
        _currentUser = currentUser;
    }

    public async Task<OrderPersistOutcome> ChangeAsync(
        Guid orderId,
        Guid expectedRowVersion,
        OrderStatus newStatus,
        OrderConflictResolution resolution = OrderConflictResolution.Reject,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = db.Database.CreateExecutionStrategy();
        var candidateRowVersion = Guid.NewGuid();
        var auditEventId = Guid.NewGuid();

        try
        {
            var attempt = await strategy.ExecuteInTransactionAsync(
                async ct =>
                {
                    db.ChangeTracker.Clear();
                    var order = await db.Orders.IgnoreQueryFilters()
                        .Include(o => o.Client)
                        .Include(o => o.Location)
                        .Include(o => o.Items).ThenInclude(i => i.Product)
                        .SingleOrDefaultAsync(o => o.Id == orderId, ct);
                    if (order == null) return StatusAttempt.Error("order_not_found");

                    if (order.Status == newStatus)
                        return StatusAttempt.Saved(order.RowVersion);

                    if (resolution == OrderConflictResolution.Reject &&
                        order.RowVersion != expectedRowVersion)
                        return StatusAttempt.Conflict(order);

                    if (!OrderStatusTransitionPolicy.CanTransition(order.Status, newStatus))
                        return StatusAttempt.Error("invalid_status_transition");

                    var oldStatus = order.Status;
                    var actualRowVersion = order.RowVersion;
                    db.Entry(order).Property(o => o.RowVersion).OriginalValue =
                        resolution == OrderConflictResolution.OverwriteLatest
                            ? actualRowVersion
                            : expectedRowVersion;
                    order.Status = newStatus;
                    order.RowVersion = candidateRowVersion;
                    db.OrderAuditEvents.Add(new OrderAuditEvent
                    {
                        Id = auditEventId,
                        OrderId = orderId,
                        UserName = _currentUser.Current?.Name ?? "(desconocido)",
                        UserId = _currentUser.Current?.Id,
                        EventType = $"Estado: {oldStatus} → {newStatus}",
                    });

                    await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, ct);
                    return StatusAttempt.Saved(candidateRowVersion);
                },
                async ct =>
                {
                    db.ChangeTracker.Clear();
                    return await db.Orders.AsNoTracking().AnyAsync(
                               o => o.Id == orderId && o.RowVersion == candidateRowVersion, ct)
                           && await db.OrderAuditEvents.AsNoTracking().AnyAsync(
                               e => e.Id == auditEventId, ct);
                },
                cancellationToken);

            return attempt.Status switch
            {
                OrderPersistStatus.Saved => new OrderPersistOutcome(
                    OrderPersistStatus.Saved, attempt.PersistedRowVersion),
                OrderPersistStatus.Conflict => CreateConflict(
                    orderId, expectedRowVersion, attempt.LatestOrder!),
                _ => new OrderPersistOutcome(
                    OrderPersistStatus.Error, ErrorCode: attempt.ErrorCode),
            };
        }
        catch (DbUpdateConcurrencyException)
        {
            var latest = await LoadLatestAsync(orderId, cancellationToken);
            return latest == null
                ? new OrderPersistOutcome(OrderPersistStatus.Error, ErrorCode: "order_not_found")
                : CreateConflict(orderId, expectedRowVersion, latest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Status change failed for order {OrderId}", orderId);
            return new OrderPersistOutcome(OrderPersistStatus.Error, ErrorCode: "status_change_failed");
        }
    }

    private static OrderPersistOutcome CreateConflict(
        Guid orderId, Guid expectedRowVersion, Order latest) => new(
        OrderPersistStatus.Conflict,
        Conflict: new OrderConflictDetails(
            orderId,
            expectedRowVersion,
            latest.RowVersion,
            new[] { "Estado" },
            latest));

    private async Task<Order?> LoadLatestAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Orders.AsNoTracking().IgnoreQueryFilters()
            .Include(o => o.Client)
            .Include(o => o.Location)
            .Include(o => o.Items).ThenInclude(i => i.Product)
            .SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken);
    }

    private sealed record StatusAttempt(
        OrderPersistStatus Status,
        Guid? PersistedRowVersion = null,
        Order? LatestOrder = null,
        string? ErrorCode = null)
    {
        public static StatusAttempt Saved(Guid rowVersion) =>
            new(OrderPersistStatus.Saved, rowVersion);
        public static StatusAttempt Conflict(Order latest) =>
            new(OrderPersistStatus.Conflict, LatestOrder: latest);
        public static StatusAttempt Error(string errorCode) =>
            new(OrderPersistStatus.Error, ErrorCode: errorCode);
    }
}
