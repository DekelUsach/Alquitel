using Alquitel.Core.Entities;

namespace Alquitel.Core.Helpers;

public static class OrderConflictComparer
{
    public static IReadOnlyList<string> Compare(Order expected, Order actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var changed = new List<string>();
        AddIfDifferent(changed, "Número de presupuesto", expected.BudgetNumber, actual.BudgetNumber);
        AddIfDifferent(changed, "Responsable", expected.AdminName, actual.AdminName);
        AddIfDifferent(changed, "Cliente", expected.ClientId, actual.ClientId);
        AddIfDifferent(changed, "Ubicación", expected.LocationId, actual.LocationId);
        AddIfDifferent(changed, "Fecha del evento", expected.EventDate, actual.EventDate);
        AddIfDifferent(changed, "Fecha de finalización", expected.EventEndDate, actual.EventEndDate);
        AddIfDifferent(changed, "Estado", expected.Status, actual.Status);
        AddIfDifferent(changed, "Comentarios", expected.Comments, actual.Comments);
        AddIfDifferent(changed, "Descuento porcentual", expected.DiscountPercent, actual.DiscountPercent);
        AddIfDifferent(changed, "Descuento fijo", expected.DiscountAmount, actual.DiscountAmount);
        AddIfDifferent(changed, "IVA", expected.AddVat, actual.AddVat);

        if (!ItemsAreEquivalent(expected.Items, actual.Items))
            changed.Add("Productos");

        return changed;
    }

    private static void AddIfDifferent<T>(List<string> changed, string label, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            changed.Add(label);
    }

    private static bool ItemsAreEquivalent(IReadOnlyCollection<OrderItem>? expected, IReadOnlyCollection<OrderItem>? actual)
    {
        if (ReferenceEquals(expected, actual)) return true;
        if (expected == null || actual == null || expected.Count != actual.Count) return false;

        var expectedItems = expected.OrderBy(i => i.Id).Select(ToPersistedValues);
        var actualItems = actual.OrderBy(i => i.Id).Select(ToPersistedValues);
        return expectedItems.SequenceEqual(actualItems);
    }

    private static PersistedItemValues ToPersistedValues(OrderItem item) => new(
        item.Id,
        item.ProductId,
        item.Quantity,
        item.UnitPrice,
        item.Dias,
        item.TechnicalNotes,
        item.ImagePath,
        item.CustomFieldsJson,
        item.DescriptionSnapshot,
        item.RequestedMeasure);

    private sealed record PersistedItemValues(
        Guid Id,
        Guid ProductId,
        int Quantity,
        decimal UnitPrice,
        int Days,
        string? TechnicalNotes,
        string? ImagePath,
        string? CustomFieldsJson,
        string? DescriptionSnapshot,
        string? RequestedMeasure);
}
