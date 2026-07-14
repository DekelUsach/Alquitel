using Alquitel.Core.Entities;

namespace Alquitel.Mobile.Helpers;

public static class OrderStatusDisplay
{
    public static string ToDisplay(this OrderStatus status) => status switch
    {
        OrderStatus.Draft => "Borrador",
        OrderStatus.Approved => "Aprobado",
        OrderStatus.SentToOF => "Enviado a OF",
        OrderStatus.SentToOT => "Enviado a OT",
        OrderStatus.Archived => "Archivado",
        OrderStatus.Rejected => "Rechazado",
        _ => status.ToString(),
    };

    public static string ToColorKey(this OrderStatus status) => status switch
    {
        OrderStatus.Draft => "StatusDraft",
        OrderStatus.Approved => "StatusApproved",
        OrderStatus.SentToOF => "StatusSentToOF",
        OrderStatus.SentToOT => "StatusSentToOT",
        OrderStatus.Archived => "StatusArchived",
        OrderStatus.Rejected => "StatusRejected",
        _ => "StatusDraft",
    };

    /// <summary>Todos los estados en orden de flujo, para pickers de cambio de estado.</summary>
    public static IReadOnlyList<OrderStatus> All { get; } = new[]
    {
        OrderStatus.Draft, OrderStatus.Approved, OrderStatus.SentToOF,
        OrderStatus.SentToOT, OrderStatus.Archived, OrderStatus.Rejected,
    };
}
