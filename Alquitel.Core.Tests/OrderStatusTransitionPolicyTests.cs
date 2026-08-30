using Alquitel.Core.Entities;
using Alquitel.Core.Helpers;

namespace Alquitel.Core.Tests;

public class OrderStatusTransitionPolicyTests
{
    public static TheoryData<OrderStatus, OrderStatus> AllowedTransitions => new()
    {
        { OrderStatus.Draft, OrderStatus.Approved },
        { OrderStatus.Draft, OrderStatus.Rejected },
        { OrderStatus.Draft, OrderStatus.Archived },
        { OrderStatus.Approved, OrderStatus.Draft },
        { OrderStatus.Approved, OrderStatus.SentToOF },
        { OrderStatus.Approved, OrderStatus.SentToOT },
        { OrderStatus.Approved, OrderStatus.Rejected },
        { OrderStatus.Approved, OrderStatus.Archived },
        { OrderStatus.SentToOF, OrderStatus.SentToOT },
        { OrderStatus.SentToOF, OrderStatus.Approved },
        { OrderStatus.SentToOF, OrderStatus.Archived },
        { OrderStatus.SentToOT, OrderStatus.SentToOF },
        { OrderStatus.SentToOT, OrderStatus.Approved },
        { OrderStatus.SentToOT, OrderStatus.Archived },
        { OrderStatus.Rejected, OrderStatus.Draft },
        { OrderStatus.Rejected, OrderStatus.Archived },
        { OrderStatus.Archived, OrderStatus.Draft },
    };

    public static TheoryData<OrderStatus, OrderStatus> RejectedTransitions => new()
    {
        { OrderStatus.Draft, OrderStatus.SentToOF },
        { OrderStatus.Draft, OrderStatus.SentToOT },
        { OrderStatus.SentToOF, OrderStatus.Draft },
        { OrderStatus.SentToOF, OrderStatus.Rejected },
        { OrderStatus.SentToOT, OrderStatus.Draft },
        { OrderStatus.SentToOT, OrderStatus.Rejected },
        { OrderStatus.Rejected, OrderStatus.Approved },
        { OrderStatus.Rejected, OrderStatus.SentToOF },
        { OrderStatus.Rejected, OrderStatus.SentToOT },
        { OrderStatus.Archived, OrderStatus.Approved },
        { OrderStatus.Archived, OrderStatus.SentToOF },
        { OrderStatus.Archived, OrderStatus.SentToOT },
        { OrderStatus.Archived, OrderStatus.Rejected },
    };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void PermiteLasTransicionesDefinidas(OrderStatus from, OrderStatus to)
        => Assert.True(OrderStatusTransitionPolicy.CanTransition(from, to));

    [Theory]
    [MemberData(nameof(RejectedTransitions))]
    public void RechazaSaltosDeEstadoInvalidos(OrderStatus from, OrderStatus to)
        => Assert.False(OrderStatusTransitionPolicy.CanTransition(from, to));

    [Theory]
    [InlineData(OrderStatus.Draft)]
    [InlineData(OrderStatus.Approved)]
    [InlineData(OrderStatus.SentToOF)]
    [InlineData(OrderStatus.SentToOT)]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Archived)]
    public void AsignarElMismoEstadoEsIdempotente(OrderStatus status)
        => Assert.True(OrderStatusTransitionPolicy.CanTransition(status, status));
}
