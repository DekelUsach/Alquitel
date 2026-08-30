using Alquitel.Core.Entities;

namespace Alquitel.Core.Interfaces;

public interface IOrderStatusService
{
    Task<OrderPersistOutcome> ChangeAsync(
        Guid orderId,
        Guid expectedRowVersion,
        OrderStatus newStatus,
        OrderConflictResolution resolution = OrderConflictResolution.Reject,
        CancellationToken cancellationToken = default);
}
