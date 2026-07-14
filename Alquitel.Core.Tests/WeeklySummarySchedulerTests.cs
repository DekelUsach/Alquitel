using Alquitel.Core.Helpers;

namespace Alquitel.Core.Tests;

public class WeeklySummarySchedulerTests
{
    // Lunes 2026-07-13 (semana actual); la semana a resumir es 2026-07-06 → 2026-07-12.

    [Fact]
    public void WeekStart_DeLunes_EsElMismoDia()
    {
        var monday = new DateTime(2026, 7, 13);
        Assert.Equal(monday, WeeklySummaryScheduler.WeekStart(monday));
    }

    [Fact]
    public void WeekStart_DeDomingo_EsElLunesAnterior()
    {
        var sunday = new DateTime(2026, 7, 19);
        Assert.Equal(new DateTime(2026, 7, 13), WeeklySummaryScheduler.WeekStart(sunday));
    }

    [Fact]
    public void ShouldGenerate_NuncaGenerado_True()
    {
        Assert.True(WeeklySummaryScheduler.ShouldGenerate(null, new DateTime(2026, 7, 14)));
    }

    [Fact]
    public void ShouldGenerate_GeneradoEstaSemana_False()
    {
        var lastRun = new DateTime(2026, 7, 13); // lunes de esta semana
        Assert.False(WeeklySummaryScheduler.ShouldGenerate(lastRun, new DateTime(2026, 7, 14)));
    }

    [Fact]
    public void ShouldGenerate_GeneradoSemanaPasada_True()
    {
        var lastRun = new DateTime(2026, 7, 10); // viernes de la semana anterior
        Assert.True(WeeklySummaryScheduler.ShouldGenerate(lastRun, new DateTime(2026, 7, 14)));
    }

    [Fact]
    public void PreviousWeekRange_DevuelveLunesADomingoAnteriores()
    {
        var (start, endExclusive) = WeeklySummaryScheduler.PreviousWeekRange(new DateTime(2026, 7, 14));
        Assert.Equal(new DateTime(2026, 7, 6), start);
        Assert.Equal(new DateTime(2026, 7, 13), endExclusive);
    }
}
