using Alquitel.Core.Entities;

namespace Alquitel.Core.Tests;

public class OrderTotalsTests
{
    private static Order OrdenCon(decimal subtotal) => new()
    {
        Items = { new OrderItem { Quantity = 1, Dias = 1, UnitPrice = subtotal } }
    };

    [Fact]
    public void SinDescuentoNiIva_TotalIgualSubtotal()
    {
        var o = OrdenCon(1000m);
        Assert.Equal(1000m, o.Total);
        Assert.Equal(0m, o.DiscountValue);
        Assert.Equal(0m, o.VatValue);
        Assert.Equal(1000m, o.GrandTotal);
    }

    [Fact]
    public void DescuentoPorcentual()
    {
        var o = OrdenCon(1000m);
        o.DiscountPercent = 10;
        Assert.Equal(100m, o.DiscountValue);
        Assert.Equal(900m, o.GrandTotal);
    }

    [Fact]
    public void DescuentoMontoFijo_SeSumaAlPorcentual()
    {
        var o = OrdenCon(1000m);
        o.DiscountPercent = 10;
        o.DiscountAmount = 50;
        Assert.Equal(150m, o.DiscountValue);
        Assert.Equal(850m, o.GrandTotal);
    }

    [Fact]
    public void DescuentoNuncaSuperaSubtotal()
    {
        var o = OrdenCon(100m);
        o.DiscountAmount = 500;
        Assert.Equal(100m, o.DiscountValue);
        Assert.Equal(0m, o.GrandTotal);
    }

    [Fact]
    public void PorcentajeFueraDeRango_SeClampa()
    {
        var o = OrdenCon(1000m);
        o.DiscountPercent = 150;
        Assert.Equal(1000m, o.DiscountValue);
    }

    [Fact]
    public void Iva_SobreNetoConDescuento()
    {
        var o = OrdenCon(1000m);
        o.DiscountPercent = 10;
        o.AddVat = true;
        // Neto 900 → IVA 21% = 189 → total 1089
        Assert.Equal(900m, o.NetTotal);
        Assert.Equal(189m, o.VatValue);
        Assert.Equal(1089m, o.GrandTotal);
    }

    [Fact]
    public void IvaApagado_NoDiscrimina()
    {
        var o = OrdenCon(1000m);
        o.AddVat = false;
        Assert.Equal(0m, o.VatValue);
        Assert.Equal(o.NetTotal, o.GrandTotal);
    }
}
