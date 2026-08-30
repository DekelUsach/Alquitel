using Alquitel.Core.Entities;

namespace Alquitel.Core.Interfaces;

public enum OrderPersistStatus
{
    Saved,
    Conflict,
    Error,
}

public enum OrderConflictResolution
{
    Reject,
    OverwriteLatest,
}

public sealed record OrderConflictDetails(
    Guid OrderId,
    Guid ExpectedRowVersion,
    Guid ActualRowVersion,
    IReadOnlyList<string> ChangedFields,
    Order LatestOrder);

public sealed record OrderPersistOutcome(
    OrderPersistStatus Status,
    Guid? PersistedRowVersion = null,
    string? PersistedBudgetNumber = null,
    OrderConflictDetails? Conflict = null,
    string? ErrorCode = null);

public interface IOrderPersistenceService
{
    Task<OrderPersistOutcome> PersistAsync(
        Order order,
        OrderConflictResolution resolution = OrderConflictResolution.Reject,
        CancellationToken cancellationToken = default);
}
