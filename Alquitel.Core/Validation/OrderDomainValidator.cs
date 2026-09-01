using Alquitel.Core.Entities;

namespace Alquitel.Core.Validation;

public sealed record OrderValidationResult(bool IsValid, string? ErrorCode = null)
{
    public static readonly OrderValidationResult Valid = new(true);
    public static OrderValidationResult Invalid(string code) => new(false, code);
}

public static class OrderDomainValidator
{
    private const int MaxQuantity = 9_999;
    private const int MaxDays = 3_650;
    private const decimal MaxUnitPrice = 1_000_000_000m;
    private const decimal MaxDiscountAmount = 1_000_000_000_000m;
    private const int MaxSnapshotLength = 10_000;

    public static OrderValidationResult ValidateAndNormalize(
        Order order,
        bool requireDescriptionSnapshots = true)
    {
        ArgumentNullException.ThrowIfNull(order);

        order.BudgetNumber = order.BudgetNumber?.Trim() ?? string.Empty;
        if (order.BudgetNumber.Length is 0 or > 64)
            return OrderValidationResult.Invalid("invalid_budget_number");
        if (order.CreatedDate == default)
            return OrderValidationResult.Invalid("invalid_created_date");
        if (order.EventEndDate.HasValue && !order.EventDate.HasValue)
            return OrderValidationResult.Invalid("invalid_event_date_range");
        if (order.EventDate.HasValue && order.EventEndDate < order.EventDate)
            return OrderValidationResult.Invalid("invalid_event_date_range");
        if (order.DiscountPercent is < 0m or > 100m)
            return OrderValidationResult.Invalid("invalid_discount_percent");
        if (order.DiscountAmount is < 0m or > MaxDiscountAmount)
            return OrderValidationResult.Invalid("invalid_discount_amount");
        if (order.Items == null)
            return OrderValidationResult.Invalid("invalid_items");

        var ids = new HashSet<Guid>();
        foreach (var item in order.Items)
        {
            if (item == null)
                return OrderValidationResult.Invalid("null_order_item");
            if (item.Quantity is < 1 or > MaxQuantity)
                return OrderValidationResult.Invalid("invalid_quantity");
            if (item.Dias is < 1 or > MaxDays)
                return OrderValidationResult.Invalid("invalid_days");
            if (item.UnitPrice is < 0m or > MaxUnitPrice)
                return OrderValidationResult.Invalid("invalid_unit_price");
            if (item.Id != Guid.Empty && !ids.Add(item.Id))
                return OrderValidationResult.Invalid("duplicate_order_item");

            if (string.IsNullOrWhiteSpace(item.DescriptionSnapshot))
                item.DescriptionSnapshot = item.Product?.Description?.Trim();
            if (requireDescriptionSnapshots &&
                (string.IsNullOrWhiteSpace(item.DescriptionSnapshot) ||
                 item.DescriptionSnapshot.Length > MaxSnapshotLength))
                return OrderValidationResult.Invalid("missing_description_snapshot");

            try
            {
                _ = item.Total;
            }
            catch (OverflowException)
            {
                return OrderValidationResult.Invalid("line_total_overflow");
            }
        }

        return OrderValidationResult.Valid;
    }
}
