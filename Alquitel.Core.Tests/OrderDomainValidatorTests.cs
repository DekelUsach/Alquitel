using Alquitel.Core.Entities;
using Alquitel.Core.Validation;

namespace Alquitel.Core.Tests;

public sealed class OrderDomainValidatorTests
{
    [Fact]
    public void RedondeaCadaLineaYElIvaConCriterioComercial()
    {
        var order = ValidOrder();
        order.Items[0].Quantity = 1;
        order.Items[0].Dias = 1;
        order.Items[0].UnitPrice = 10.125m;
        order.AddVat = true;

        Assert.Equal(10.13m, order.Items[0].Total);
        Assert.Equal(2.13m, order.VatValue);
        Assert.Equal(12.26m, order.GrandTotal);
    }

    [Theory]
    [InlineData(0, 1, 10, "invalid_quantity")]
    [InlineData(1, 0, 10, "invalid_days")]
    [InlineData(1, 1, -1, "invalid_unit_price")]
    public void RechazaValoresDeLineaFueraDeRango(
        int quantity, int days, decimal price, string expectedCode)
    {
        var order = ValidOrder();
        order.Items[0].Quantity = quantity;
        order.Items[0].Dias = days;
        order.Items[0].UnitPrice = price;

        var result = OrderDomainValidator.ValidateAndNormalize(order);

        Assert.False(result.IsValid);
        Assert.Equal(expectedCode, result.ErrorCode);
    }

    [Fact]
    public void RechazaRangoDeEventoInvertido()
    {
        var order = ValidOrder();
        order.EventDate = new DateTime(2026, 9, 10);
        order.EventEndDate = new DateTime(2026, 9, 9);

        Assert.Equal(
            "invalid_event_date_range",
            OrderDomainValidator.ValidateAndNormalize(order).ErrorCode);
    }

    [Fact]
    public void CongelaSnapshotDesdeElProductoAntesDePersistir()
    {
        var order = ValidOrder();
        order.Items[0].DescriptionSnapshot = null;
        order.Items[0].Product = new Product { Description = "Pantalla histórica" };

        var result = OrderDomainValidator.ValidateAndNormalize(order);

        Assert.True(result.IsValid);
        Assert.Equal("Pantalla histórica", order.Items[0].DescriptionSnapshot);
    }

    [Fact]
    public void RechazaItemSinSnapshotNiProductoDisponible()
    {
        var order = ValidOrder();
        order.Items[0].DescriptionSnapshot = " ";
        order.Items[0].Product = null;

        Assert.Equal(
            "missing_description_snapshot",
            OrderDomainValidator.ValidateAndNormalize(order).ErrorCode);
    }

    [Fact]
    public void RechazaItemNuloConErrorEstructurado()
    {
        var order = ValidOrder();
        order.Items.Add(null!);

        Assert.Equal(
            "null_order_item",
            OrderDomainValidator.ValidateAndNormalize(order).ErrorCode);
    }

    private static Order ValidOrder() => new()
    {
        BudgetNumber = "100/1",
        EventDate = new DateTime(2026, 9, 10),
        EventEndDate = new DateTime(2026, 9, 10),
        Items =
        {
            new OrderItem
            {
                Quantity = 1,
                Dias = 1,
                UnitPrice = 10,
                DescriptionSnapshot = "Pantalla",
            },
        },
    };
}
