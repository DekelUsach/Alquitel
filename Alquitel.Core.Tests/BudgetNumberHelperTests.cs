using Alquitel.Core.Helpers;

namespace Alquitel.Core.Tests;

public class BudgetNumberHelperTests
{
    [Theory]
    [InlineData("31294/2", "31294")]
    [InlineData("31294", "31294")]
    [InlineData("  31294/3  ", "31294")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void BasePart_ExtraeSerie(string? input, string expected)
        => Assert.Equal(expected, BudgetNumberHelper.BasePart(input));

    [Theory]
    [InlineData("31294/2", 2)]
    [InlineData("31294/3", 3)]
    [InlineData("31294", 1)]
    [InlineData("31294/1", 1)]   // versión 1 explícita se normaliza a 1
    [InlineData("31294/abc", 1)] // sufijo no numérico → 1
    [InlineData(null, 1)]
    public void VersionPart_ExtraeVersion(string? input, int expected)
        => Assert.Equal(expected, BudgetNumberHelper.VersionPart(input));

    [Fact]
    public void NextSerial_BaseVacia_ArrancaEnUno()
        => Assert.Equal("1", BudgetNumberHelper.NextSerial(Array.Empty<string?>()));

    [Fact]
    public void NextSerial_TomaMaximoMasUno_IgnorandoVersionesYNoNumericos()
    {
        var existing = new[] { "31294", "31294/2", "9054", "PRESUPUESTO-X", null, "" };
        Assert.Equal("31295", BudgetNumberHelper.NextSerial(existing));
    }

    [Fact]
    public void NextVersion_IncrementaSobreMaximaVersionDeLaRama()
    {
        var existing = new[] { "31294", "31294/2", "9054/7" };
        Assert.Equal("31294/3", BudgetNumberHelper.NextVersion("31294", existing));
        Assert.Equal("31294/3", BudgetNumberHelper.NextVersion("31294/2", existing));
    }

    [Fact]
    public void NextVersion_SinExistentes_DevuelveVersionDos()
        => Assert.Equal("31294/2", BudgetNumberHelper.NextVersion("31294", Array.Empty<string?>()));

    [Fact]
    public void NextVersion_SinNumeroBase_Lanza()
        => Assert.Throws<ArgumentException>(() => BudgetNumberHelper.NextVersion("", new[] { "1" }));

    [Theory]
    [InlineData("31294/2", "31294(2)")]
    [InlineData("31294", "31294")]
    public void ToFileNameForm_ReemplazaBarra(string input, string expected)
        => Assert.Equal(expected, BudgetNumberHelper.ToFileNameForm(input));

    [Theory]
    [InlineData("31294(2)", "31294/2")]
    [InlineData("31294", "31294")]
    [InlineData("(2)", "(2)")]      // sin base: se devuelve tal cual
    [InlineData("31294(2", "31294(2")] // paréntesis sin cerrar: tal cual
    public void FromFileNameForm_EsInversa(string input, string expected)
        => Assert.Equal(expected, BudgetNumberHelper.FromFileNameForm(input));

    [Theory]
    [InlineData("31294/2")]
    [InlineData("31294")]
    public void ToFileName_RoundTrip(string original)
        => Assert.Equal(original, BudgetNumberHelper.FromFileNameForm(BudgetNumberHelper.ToFileNameForm(original)));
}
