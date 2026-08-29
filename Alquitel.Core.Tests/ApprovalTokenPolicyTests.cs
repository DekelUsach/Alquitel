using Alquitel.Core.Security;

namespace Alquitel.Core.Tests;

public class ApprovalTokenPolicyTests
{
    private static readonly DateTime Hoy = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LinkReciente_NoEstaVencido()
        => Assert.False(ApprovalTokenPolicy.IsExpired(Hoy.AddDays(-1), Hoy));

    [Fact]
    public void LinkEnElBorde_TodaviaSirve()
        => Assert.False(ApprovalTokenPolicy.IsExpired(Hoy.AddDays(-ApprovalTokenPolicy.MaxAgeDays), Hoy));

    [Fact]
    public void LinkPasadoElPlazo_EstaVencido()
    {
        // Conocer el token ES la autorización: sin vencimiento, un mail reenviado un
        // año después seguía mostrando importes y permitía aprobar.
        Assert.True(ApprovalTokenPolicy.IsExpired(Hoy.AddDays(-ApprovalTokenPolicy.MaxAgeDays).AddHours(-1), Hoy));
    }

    [Fact]
    public void RemainingDays_CuentaLoQueFalta()
    {
        Assert.Equal(ApprovalTokenPolicy.MaxAgeDays, ApprovalTokenPolicy.RemainingDays(Hoy, Hoy));
        Assert.Equal(ApprovalTokenPolicy.MaxAgeDays - 10, ApprovalTokenPolicy.RemainingDays(Hoy.AddDays(-10), Hoy));
    }

    [Fact]
    public void RemainingDays_NuncaEsNegativo()
        => Assert.Equal(0, ApprovalTokenPolicy.RemainingDays(Hoy.AddDays(-500), Hoy));

    [Fact]
    public void PlazoPersonalizado_SeRespeta()
    {
        Assert.False(ApprovalTokenPolicy.IsExpired(Hoy.AddDays(-40), Hoy, maxAgeDays: 60));
        Assert.True(ApprovalTokenPolicy.IsExpired(Hoy.AddDays(-40), Hoy, maxAgeDays: 7));
    }

    [Fact]
    public void PlazoInvalido_CaeAlDefault()
        => Assert.True(ApprovalTokenPolicy.IsExpired(Hoy.AddDays(-100), Hoy, maxAgeDays: 0));
}
