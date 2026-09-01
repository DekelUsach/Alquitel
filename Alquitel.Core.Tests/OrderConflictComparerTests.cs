using Alquitel.Core.Entities;
using Alquitel.Core.Helpers;

namespace Alquitel.Core.Tests;

public class OrderConflictComparerTests
{
    private static readonly Guid ClientA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LocationA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProductA = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ItemA = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static Order Sample() => new()
    {
        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        BudgetNumber = "31294",
        AdminName = "Ana",
        ClientId = ClientA,
        LocationId = LocationA,
        EventDate = new DateTime(2026, 9, 10),
        EventEndDate = new DateTime(2026, 9, 11),
        Status = OrderStatus.Draft,
        Comments = "Montaje 08:00",
        DiscountPercent = 10,
        DiscountAmount = 500,
        AddVat = true,
        Items =
        {
            new OrderItem
            {
                Id = ItemA,
                ProductId = ProductA,
                Quantity = 2,
                Dias = 2,
                UnitPrice = 1000,
                TechnicalNotes = "HDMI",
                ImagePath = "producto.png",
                CustomFieldsJson = "[]",
                DescriptionSnapshot = "Pantalla",
                RequestedMeasure = "4 x 2",
            }
        }
    };

    [Fact]
    public void OrdenesEquivalentesNoInformanCambios()
    {
        var local = Sample();
        var latest = Sample();

        local.RowVersion = Guid.NewGuid();
        latest.RowVersion = Guid.NewGuid();
        local.Items[0].HasStockConflict = true;

        Assert.Empty(OrderConflictComparer.Compare(local, latest));
    }

    [Fact]
    public void InformaLosCamposEditablesQueDifieren()
    {
        var local = Sample();
        var latest = Sample();
        latest.BudgetNumber = "31295";
        latest.AdminName = "Beto";
        latest.ClientId = Guid.NewGuid();
        latest.LocationId = Guid.NewGuid();
        latest.EventDate = latest.EventDate!.Value.AddDays(1);
        latest.EventEndDate = latest.EventEndDate!.Value.AddDays(1);
        latest.Status = OrderStatus.Approved;
        latest.Comments = "Otro montaje";
        latest.DiscountPercent = 5;
        latest.DiscountAmount = 0;
        latest.AddVat = false;

        var changed = OrderConflictComparer.Compare(local, latest);

        Assert.Equal(new[]
        {
            "Número de presupuesto", "Responsable", "Cliente", "Ubicación",
            "Fecha del evento", "Fecha de finalización", "Estado", "Comentarios",
            "Descuento porcentual", "Descuento fijo", "IVA"
        }, changed);
    }

    [Fact]
    public void InformaCambiosPersistidosDeLosItems()
    {
        var local = Sample();
        var latest = Sample();
        latest.Items[0].Quantity = 3;

        Assert.Equal(new[] { "Productos" }, OrderConflictComparer.Compare(local, latest));
    }

    [Fact]
    public void InformaAltasYBajasDeItemsSinDependerDelOrdenDeLaLista()
    {
        var local = Sample();
        var latest = Sample();
        local.Items.Add(new OrderItem { Id = Guid.NewGuid(), ProductId = Guid.NewGuid() });

        Assert.Equal(new[] { "Productos" }, OrderConflictComparer.Compare(local, latest));

        latest.Items.Reverse();
        local.Items.RemoveAt(1);
        Assert.Empty(OrderConflictComparer.Compare(local, latest));
    }
}
