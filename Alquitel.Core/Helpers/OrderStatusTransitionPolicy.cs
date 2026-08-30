using Alquitel.Core.Entities;

namespace Alquitel.Core.Helpers;

public static class OrderStatusTransitionPolicy
{
    public static bool CanTransition(OrderStatus from, OrderStatus to)
    {
        if (from == to) return true;

        return from switch
        {
            OrderStatus.Draft => to is OrderStatus.Approved
                or OrderStatus.Rejected
                or OrderStatus.Archived,
            OrderStatus.Approved => to is OrderStatus.Draft
                or OrderStatus.SentToOF
                or OrderStatus.SentToOT
                or OrderStatus.Rejected
                or OrderStatus.Archived,
            OrderStatus.SentToOF => to is OrderStatus.SentToOT
                or OrderStatus.Approved
                or OrderStatus.Archived,
            OrderStatus.SentToOT => to is OrderStatus.SentToOF
                or OrderStatus.Approved
                or OrderStatus.Archived,
            OrderStatus.Rejected => to is OrderStatus.Draft
                or OrderStatus.Archived,
            OrderStatus.Archived => to is OrderStatus.Draft,
            _ => false,
        };
    }
}
