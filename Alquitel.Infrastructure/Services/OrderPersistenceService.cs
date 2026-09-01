using Alquitel.Core.Entities;
using Alquitel.Core.Helpers;
using Alquitel.Core.Interfaces;
using Alquitel.Core.Validation;
using Alquitel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alquitel.Infrastructure.Services;

public class OrderPersistenceService : IOrderPersistenceService
{
    private const int MaxBudgetNumberRetries = 3;

    private readonly IDbContextFactory<AlquitelDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUser;

    public OrderPersistenceService(
        IDbContextFactory<AlquitelDbContext> dbContextFactory,
        ICurrentUserService currentUser)
    {
        _dbContextFactory = dbContextFactory;
        _currentUser = currentUser;
    }

    public async Task<OrderPersistOutcome> PersistAsync(
        Order order,
        OrderConflictResolution resolution = OrderConflictResolution.Reject,
        Guid? operationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var auditEventId = operationId ?? Guid.NewGuid();
        if (operationId.HasValue)
        {
            try
            {
                var alreadyApplied = await TryGetAppliedOutcomeAsync(
                    order.Id, auditEventId, cancellationToken);
                if (alreadyApplied != null)
                {
                    if (alreadyApplied.Status == OrderPersistStatus.Saved)
                    {
                        order.RowVersion = alreadyApplied.PersistedRowVersion!.Value;
                        order.BudgetNumber = alreadyApplied.PersistedBudgetNumber!;
                    }
                    return alreadyApplied;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warning(
                    "No se pudo verificar la operación idempotente ({ErrorType})",
                    ex.GetType().Name);
                return new OrderPersistOutcome(
                    OrderPersistStatus.Error, ErrorCode: "idempotency_check_failed");
            }
        }

        var validation = OrderDomainValidator.ValidateAndNormalize(
            order, requireDescriptionSnapshots: false);
        if (!validation.IsValid)
            return new OrderPersistOutcome(
                OrderPersistStatus.Error, ErrorCode: validation.ErrorCode);
        var candidateBudgetNumber = order.BudgetNumber;

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = await PersistOnceAsync(
                    order, candidateBudgetNumber, auditEventId, resolution, cancellationToken);
                if (outcome.Status == OrderPersistStatus.Saved)
                {
                    order.RowVersion = outcome.PersistedRowVersion!.Value;
                    order.BudgetNumber = outcome.PersistedBudgetNumber!;
                }
                return outcome;
            }
            catch (DbUpdateException ex) when (
                attempt < MaxBudgetNumberRetries && IsBudgetNumberCollision(ex))
            {
                var previous = candidateBudgetNumber;
                candidateBudgetNumber = await NextAvailableNumberAsync(
                    candidateBudgetNumber, cancellationToken);
                AppLog.Warning(
                    "Colisión de número de presupuesto {Old}: renumerado a {New}, reintento {Attempt}",
                    previous, candidateBudgetNumber, attempt + 1);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                try
                {
                    var applied = await TryGetAppliedOutcomeAsync(
                        order.Id, auditEventId, cancellationToken);
                    if (applied != null)
                    {
                        order.RowVersion = applied.PersistedRowVersion!.Value;
                        order.BudgetNumber = applied.PersistedBudgetNumber!;
                        return applied;
                    }
                }
                catch
                {
                    // Se conserva el error original; la próxima pasada del outbox
                    // verificará nuevamente la identidad de operación.
                }
                AppLog.Error(ex, "PersistAsync failed for order {OrderId}", order.Id);
                return new OrderPersistOutcome(OrderPersistStatus.Error, ErrorCode: "persistence_failed");
            }
        }
    }

    private async Task<OrderPersistOutcome> PersistOnceAsync(
        Order order,
        string budgetNumber,
        Guid auditEventId,
        OrderConflictResolution resolution,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = db.Database.CreateExecutionStrategy();
        var candidateRowVersion = Guid.NewGuid();

        var appliedEvent = await db.OrderAuditEvents.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == auditEventId, cancellationToken);
        if (appliedEvent != null)
        {
            if (appliedEvent.OrderId != order.Id)
                return new OrderPersistOutcome(
                    OrderPersistStatus.Error, ErrorCode: "operation_id_collision");

            var appliedOrder = await db.Orders.AsNoTracking().IgnoreQueryFilters()
                .SingleOrDefaultAsync(o => o.Id == order.Id, cancellationToken);
            return appliedOrder == null
                ? new OrderPersistOutcome(OrderPersistStatus.Error, ErrorCode: "order_not_found")
                : new OrderPersistOutcome(
                    OrderPersistStatus.Saved,
                    appliedOrder.RowVersion,
                    appliedOrder.BudgetNumber);
        }

        try
        {
            var attempt = await strategy.ExecuteInTransactionAsync(
                async ct =>
                {
                    db.ChangeTracker.Clear();
                    return await ApplyMutationAsync(
                        db, order, budgetNumber, candidateRowVersion, auditEventId, resolution, ct);
                },
                async ct =>
                {
                    db.ChangeTracker.Clear();
                    return await db.Orders.AsNoTracking().AnyAsync(
                               o => o.Id == order.Id && o.RowVersion == candidateRowVersion, ct)
                           && await db.OrderAuditEvents.AsNoTracking().AnyAsync(
                               e => e.Id == auditEventId, ct);
                },
                cancellationToken);

            if (attempt.Status == OrderPersistStatus.Conflict)
                return CreateConflict(order, attempt.LatestOrder!);

            if (attempt.Status == OrderPersistStatus.Error)
                return new OrderPersistOutcome(OrderPersistStatus.Error, ErrorCode: attempt.ErrorCode);

            AppLog.Information("Order persisted: {OrderId} ({Budget})", order.Id, budgetNumber);
            return new OrderPersistOutcome(
                OrderPersistStatus.Saved,
                candidateRowVersion,
                budgetNumber);
        }
        catch (DbUpdateConcurrencyException)
        {
            var applied = await TryGetAppliedOutcomeAsync(
                order.Id, auditEventId, cancellationToken);
            if (applied != null) return applied;
            var latest = await LoadLatestAsync(order.Id, cancellationToken);
            return latest == null
                ? new OrderPersistOutcome(OrderPersistStatus.Error, ErrorCode: "order_not_found")
                : CreateConflict(order, latest);
        }
    }

    private async Task<MutationAttempt> ApplyMutationAsync(
        AlquitelDbContext db,
        Order order,
        string budgetNumber,
        Guid candidateRowVersion,
        Guid auditEventId,
        OrderConflictResolution resolution,
        CancellationToken cancellationToken)
    {
        var location = await ResolveLocationAsync(db, order, cancellationToken);
        var client = await ResolveClientAsync(db, order, cancellationToken);
        var tracked = await db.Orders.IgnoreQueryFilters()
            .Include(o => o.Client)
            .Include(o => o.Location)
            .Include(o => o.Items).ThenInclude(i => i.Product)
            .SingleOrDefaultAsync(o => o.Id == order.Id, cancellationToken);

        if (tracked != null)
            HydrateHistoricalSnapshots(order.Items, tracked.Items);
        var strictValidation = OrderDomainValidator.ValidateAndNormalize(order);
        if (!strictValidation.IsValid)
            return MutationAttempt.Error(strictValidation.ErrorCode!);

        if (tracked == null)
        {
            tracked = CloneOrderForInsert(
                order, client.Id, location.Id, budgetNumber, candidateRowVersion);
            db.Orders.Add(tracked);
            db.OrderAuditEvents.Add(CreateAuditEvent(
                auditEventId, order.Id, "Creado", budgetNumber, order.Items.Count));
        }
        else
        {
            var actualRowVersion = tracked.RowVersion;
            if (resolution == OrderConflictResolution.Reject && actualRowVersion != order.RowVersion)
                return MutationAttempt.Conflict(tracked);

            if (!OrderStatusTransitionPolicy.CanTransition(tracked.Status, order.Status))
                return MutationAttempt.Error("invalid_status_transition");

            db.Entry(tracked).Property(o => o.RowVersion).OriginalValue =
                resolution == OrderConflictResolution.OverwriteLatest
                    ? actualRowVersion
                    : order.RowVersion;

            ApplyEditableValues(tracked, order, client.Id, location.Id, budgetNumber);
            SynchronizeItems(db, tracked, order.Items);
            tracked.RowVersion = candidateRowVersion;
            db.OrderAuditEvents.Add(CreateAuditEvent(
                auditEventId, order.Id, "Editado", budgetNumber, order.Items.Count));
        }

        await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);
        return MutationAttempt.Saved();
    }

    private static void HydrateHistoricalSnapshots(
        IReadOnlyCollection<OrderItem> incoming,
        IReadOnlyCollection<OrderItem> persisted)
    {
        var persistedById = persisted.ToDictionary(i => i.Id);
        foreach (var item in incoming.Where(i => string.IsNullOrWhiteSpace(i.DescriptionSnapshot)))
        {
            if (persistedById.TryGetValue(item.Id, out var historical))
                item.DescriptionSnapshot = historical.DescriptionSnapshot
                    ?? historical.Product?.Description;
            item.DescriptionSnapshot ??= item.Product?.Description;
        }
    }

    private async Task<Location> ResolveLocationAsync(
        AlquitelDbContext db, Order order, CancellationToken cancellationToken)
    {
        var name = (order.Location?.Name ?? string.Empty).Trim();
        var location = await db.Locations.FirstOrDefaultAsync(
            l => l.Name == name, cancellationToken);
        if (location != null) return location;

        location = new Location { Id = Guid.NewGuid(), Name = name };
        db.Locations.Add(location);
        return location;
    }

    private static async Task<Client> ResolveClientAsync(
        AlquitelDbContext db, Order order, CancellationToken cancellationToken)
    {
        var source = order.Client ?? new Client();
        if (source.Id != Guid.Empty)
        {
            var byId = await db.Clients.IgnoreQueryFilters().FirstOrDefaultAsync(
                c => c.Id == source.Id, cancellationToken);
            if (byId != null) return byId;
        }

        if (!string.IsNullOrWhiteSpace(source.Cuit))
        {
            var cuit = source.Cuit.Trim();
            var byCuit = await db.Clients.IgnoreQueryFilters().FirstOrDefaultAsync(
                c => c.Cuit == cuit, cancellationToken);
            if (byCuit != null) return byCuit;
        }

        var created = new Client
        {
            Id = source.Id == Guid.Empty ? Guid.NewGuid() : source.Id,
            CompanyName = source.CompanyName?.Trim() ?? string.Empty,
            Cuit = source.Cuit?.Trim() ?? string.Empty,
            ContactName = source.ContactName,
            Phone = source.Phone,
            Email = source.Email,
            InternalNotes = source.InternalNotes,
            SpecialDiscountPercent = source.SpecialDiscountPercent,
        };
        db.Clients.Add(created);
        return created;
    }

    private OrderAuditEvent CreateAuditEvent(
        Guid eventId, Guid orderId, string eventType, string budgetNumber, int itemCount) => new()
    {
        Id = eventId,
        OrderId = orderId,
        UserName = _currentUser.Current?.Name ?? "(desconocido)",
        UserId = _currentUser.Current?.Id,
        EventType = eventType,
        Detail = $"Presupuesto {budgetNumber} · {itemCount} ítem(s)",
    };

    private static Order CloneOrderForInsert(
        Order source, Guid clientId, Guid locationId, string budgetNumber, Guid rowVersion) => new()
    {
        Id = source.Id,
        BudgetNumber = budgetNumber,
        AdminName = source.AdminName,
        CreatedByUserId = source.CreatedByUserId,
        ClientId = clientId,
        LocationId = locationId,
        CreatedDate = source.CreatedDate,
        EventDate = source.EventDate,
        EventEndDate = source.EventEndDate,
        Status = source.Status,
        Comments = source.Comments,
        DiscountPercent = source.DiscountPercent,
        DiscountAmount = source.DiscountAmount,
        AddVat = source.AddVat,
        RowVersion = rowVersion,
        Items = source.Items.Select(i => CloneItem(i, source.Id)).ToList(),
    };

    private static void ApplyEditableValues(
        Order target, Order source, Guid clientId, Guid locationId, string budgetNumber)
    {
        target.BudgetNumber = budgetNumber;
        target.AdminName = source.AdminName;
        target.CreatedByUserId ??= source.CreatedByUserId;
        target.ClientId = clientId;
        target.LocationId = locationId;
        target.EventDate = source.EventDate;
        target.EventEndDate = source.EventEndDate;
        target.Status = source.Status;
        target.Comments = source.Comments;
        target.DiscountPercent = source.DiscountPercent;
        target.DiscountAmount = source.DiscountAmount;
        target.AddVat = source.AddVat;
    }

    private static void SynchronizeItems(
        AlquitelDbContext db, Order trackedOrder, IReadOnlyCollection<OrderItem> sourceItems)
    {
        var existingById = trackedOrder.Items.ToDictionary(i => i.Id);
        var incomingIds = sourceItems.Select(i => i.Id).ToHashSet();

        foreach (var removed in trackedOrder.Items.Where(i => !incomingIds.Contains(i.Id)).ToList())
            db.OrderItems.Remove(removed);

        foreach (var source in sourceItems)
        {
            if (existingById.TryGetValue(source.Id, out var target))
                ApplyItemValues(target, source, trackedOrder.Id);
            else
                db.OrderItems.Add(CloneItem(source, trackedOrder.Id));
        }
    }

    private static OrderItem CloneItem(OrderItem source, Guid orderId)
    {
        var clone = new OrderItem
        {
            Id = source.Id == Guid.Empty ? Guid.NewGuid() : source.Id,
        };
        ApplyItemValues(clone, source, orderId);
        return clone;
    }

    private static void ApplyItemValues(OrderItem target, OrderItem source, Guid orderId)
    {
        target.OrderId = orderId;
        target.ProductId = source.ProductId;
        target.Quantity = source.Quantity;
        target.UnitPrice = source.UnitPrice;
        target.Dias = source.Dias;
        target.TechnicalNotes = source.TechnicalNotes;
        target.ImagePath = source.ImagePath;
        target.CustomFieldsJson = source.CustomFieldsJson;
        target.DescriptionSnapshot = source.DescriptionSnapshot;
        target.RequestedMeasure = source.RequestedMeasure;
    }

    private OrderPersistOutcome CreateConflict(Order expected, Order latest)
    {
        AppLog.Warning(
            "Conflicto de concurrencia en orden {OrderId}: esperada {Expected}, vigente {Actual}",
            expected.Id, expected.RowVersion, latest.RowVersion);
        return new OrderPersistOutcome(
            OrderPersistStatus.Conflict,
            Conflict: new OrderConflictDetails(
                expected.Id,
                expected.RowVersion,
                latest.RowVersion,
                OrderConflictComparer.Compare(expected, latest),
                latest));
    }

    private async Task<Order?> LoadLatestAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Orders.AsNoTracking().IgnoreQueryFilters()
            .Include(o => o.Client)
            .Include(o => o.Location)
            .Include(o => o.Items).ThenInclude(i => i.Product)
            .SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken);
    }

    private async Task<OrderPersistOutcome?> TryGetAppliedOutcomeAsync(
        Guid orderId, Guid auditEventId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var auditOrderId = await db.OrderAuditEvents.AsNoTracking()
            .Where(e => e.Id == auditEventId)
            .Select(e => (Guid?)e.OrderId)
            .SingleOrDefaultAsync(cancellationToken);
        if (auditOrderId == null) return null;
        if (auditOrderId != orderId)
            return new OrderPersistOutcome(
                OrderPersistStatus.Error, ErrorCode: "operation_id_collision");

        var applied = await db.Orders.AsNoTracking().IgnoreQueryFilters()
            .Where(o => o.Id == orderId)
            .Select(o => new { o.RowVersion, o.BudgetNumber })
            .SingleOrDefaultAsync(cancellationToken);
        return applied == null
            ? new OrderPersistOutcome(OrderPersistStatus.Error, ErrorCode: "order_not_found")
            : new OrderPersistOutcome(
                OrderPersistStatus.Saved, applied.RowVersion, applied.BudgetNumber);
    }

    private async Task<string> NextAvailableNumberAsync(
        string collidedNumber, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var numbers = await db.Orders.IgnoreQueryFilters().AsNoTracking()
            .Select(o => o.BudgetNumber)
            .ToListAsync(cancellationToken);

        return BudgetNumberHelper.VersionPart(collidedNumber) > 1
            ? BudgetNumberHelper.NextVersion(collidedNumber, numbers)
            : BudgetNumberHelper.NextSerial(numbers);
    }

    private static bool IsBudgetNumberCollision(DbUpdateException ex) => ex.InnerException switch
    {
        Microsoft.Data.Sqlite.SqliteException sq =>
            sq.SqliteErrorCode == 19 &&
            sq.Message.Contains("BudgetNumber", StringComparison.OrdinalIgnoreCase),
        Npgsql.PostgresException pg =>
            pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation &&
            (pg.ConstraintName?.Contains("BudgetNumber", StringComparison.OrdinalIgnoreCase) ?? false),
        _ => false,
    };

    private sealed record MutationAttempt(
        OrderPersistStatus Status,
        Order? LatestOrder = null,
        string? ErrorCode = null)
    {
        public static MutationAttempt Saved() => new(OrderPersistStatus.Saved);
        public static MutationAttempt Conflict(Order latest) =>
            new(OrderPersistStatus.Conflict, latest);
        public static MutationAttempt Error(string errorCode) =>
            new(OrderPersistStatus.Error, ErrorCode: errorCode);
    }
}
