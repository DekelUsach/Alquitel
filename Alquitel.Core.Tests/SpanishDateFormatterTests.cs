using Alquitel.Core.Helpers;

namespace Alquitel.Core.Tests;

public class SpanishDateFormatterTests
{
    [Fact]
    public void ToWords_DiaSuelto()
        => Assert.Equal("14 de abril de 2026", SpanishDateFormatter.ToWords(new DateTime(2026, 4, 14)));

    [Fact]
    public void ToWordsRange_SinFin_DevuelveDiaSuelto()
        => Assert.Equal("14 de abril de 2026",
            SpanishDateFormatter.ToWordsRange(new DateTime(2026, 4, 14), null));

    [Fact]
    public void ToWordsRange_FinIgualOAnterior_DevuelveDiaSuelto()
    {
        var start = new DateTime(2026, 4, 14);
        Assert.Equal("14 de abril de 2026", SpanishDateFormatter.ToWordsRange(start, start));
        Assert.Equal("14 de abril de 2026", SpanishDateFormatter.ToWordsRange(start, start.AddDays(-1)));
    }

    [Fact]
    public void ToWordsRange_MismoMes()
        => Assert.Equal("del 14 al 20 de abril de 2026",
            SpanishDateFormatter.ToWordsRange(new DateTime(2026, 4, 14), new DateTime(2026, 4, 20)));

    [Fact]
    public void ToWordsRange_MesesDistintos()
        => Assert.Equal("del 14 de abril al 15 de mayo de 2026",
            SpanishDateFormatter.ToWordsRange(new DateTime(2026, 4, 14), new DateTime(2026, 5, 15)));

    [Fact]
    public void ToWordsRange_AniosDistintos()
        => Assert.Equal("del 30 de diciembre de 2026 al 2 de enero de 2027",
            SpanishDateFormatter.ToWordsRange(new DateTime(2026, 12, 30), new DateTime(2027, 1, 2)));

    [Fact]
    public void MesesEnMinusculas()
        => Assert.Equal("1 de enero de 2027", SpanishDateFormatter.ToWords(new DateTime(2027, 1, 1)));
}
