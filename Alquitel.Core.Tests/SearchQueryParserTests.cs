using Alquitel.Core.Search;

namespace Alquitel.Core.Tests;

public class SearchQueryParserTests
{
    [Fact]
    public void Parse_TextoSimple_CantidadUnoYTerminoCompleto()
    {
        var (qty, term) = SearchQueryParser.Parse("proyector");
        Assert.Equal(1, qty);
        Assert.Equal("proyector", term);
    }

    [Fact]
    public void Parse_PrefijoAsterisco_ExtraeCantidadYTermino()
    {
        var (qty, term) = SearchQueryParser.Parse("3*proyector");
        Assert.Equal(3, qty);
        Assert.Equal("proyector", term);
    }

    [Fact]
    public void Parse_PrefijoAsteriscoConEspacios_ExtraeCantidadYTermino()
    {
        var (qty, term) = SearchQueryParser.Parse("3 * pantalla led");
        Assert.Equal(3, qty);
        Assert.Equal("pantalla led", term);
    }

    [Fact]
    public void Parse_PrefijoX_ExtraeCantidad()
    {
        var (qty, term) = SearchQueryParser.Parse("2x notebook");
        Assert.Equal(2, qty);
        Assert.Equal("notebook", term);
    }

    [Fact]
    public void Parse_SoloNumero_EsTerminoLiteral()
    {
        // "85" es parte de un nombre de producto ("Touch Screen 85"), no una cantidad.
        var (qty, term) = SearchQueryParser.Parse("85");
        Assert.Equal(1, qty);
        Assert.Equal("85", term);
    }

    [Fact]
    public void Parse_CantidadCero_SeNormalizaAUno()
    {
        var (qty, term) = SearchQueryParser.Parse("0*cable");
        Assert.Equal(1, qty);
        Assert.Equal("cable", term);
    }

    [Fact]
    public void Parse_CantidadAbsurda_SeAcotaA999()
    {
        var (qty, _) = SearchQueryParser.Parse("5000*silla");
        Assert.Equal(999, qty);
    }

    [Fact]
    public void Parse_VacioONulo_DevuelveDefaults()
    {
        Assert.Equal((1, ""), SearchQueryParser.Parse(""));
        Assert.Equal((1, ""), SearchQueryParser.Parse(null));
        Assert.Equal((1, ""), SearchQueryParser.Parse("   "));
    }

    [Fact]
    public void Parse_NumeroSinSeparador_EsTerminoLiteral()
    {
        // "4k" es resolución, no cantidad ("Logitech MeetUp 4K").
        var (qty, term) = SearchQueryParser.Parse("4k");
        Assert.Equal(1, qty);
        Assert.Equal("4k", term);
    }
}
