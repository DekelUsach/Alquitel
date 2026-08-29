using Alquitel.Core.Helpers;

namespace Alquitel.Core.Tests;

public class BudgetValidityPolicyTests
{
    private static readonly DateTime Hoy = new(2026, 7, 25, 15, 30, 0, DateTimeKind.Utc);

    private static BudgetValidity Evaluar(int diasDesdeEmision, bool pendiente = true) =>
        BudgetValidityPolicy.Evaluate(Hoy.AddDays(-diasDesdeEmision), pendiente, Hoy);

    [Fact]
    public void PresupuestoRecien_EstaVigente()
    {
        var v = Evaluar(0);
        Assert.Equal(BudgetValidityState.Vigente, v.State);
        Assert.Equal(BudgetValidityPolicy.DefaultValidityDays, v.DaysRemaining);
    }

    [Fact]
    public void PresupuestoDeHaceDiezDias_SigueVigente()
        => Assert.Equal(BudgetValidityState.Vigente, Evaluar(10).State);

    [Fact]
    public void CercaDelPlazo_MarcaPorVencer()
    {
        // 15 días de plazo, emitido hace 13 → quedan 2 → dentro del umbral de aviso.
        var v = Evaluar(13);
        Assert.Equal(BudgetValidityState.PorVencer, v.State);
        Assert.Equal(2, v.DaysRemaining);
    }

    [Fact]
    public void ElUltimoDia_VenceHoy()
    {
        var v = Evaluar(BudgetValidityPolicy.DefaultValidityDays);
        Assert.Equal(BudgetValidityState.PorVencer, v.State);
        Assert.Equal(0, v.DaysRemaining);
        Assert.Equal("Vence hoy", v.Label);
    }

    [Fact]
    public void PasadoElPlazo_MarcaVencido()
    {
        var v = Evaluar(BudgetValidityPolicy.DefaultValidityDays + 1);
        Assert.Equal(BudgetValidityState.Vencido, v.State);
        Assert.Equal("Vencido hace 1 día", v.Label);
    }

    [Fact]
    public void PresupuestoViejo_DiceCuantoHaceQueVencio()
    {
        var v = Evaluar(BudgetValidityPolicy.DefaultValidityDays + 40);
        Assert.Equal(BudgetValidityState.Vencido, v.State);
        Assert.Equal("Vencido hace 40 días", v.Label);
    }

    [Fact]
    public void OrdenYaResuelta_NoAplicaElPlazo()
    {
        // Una vez aprobada o rechazada, el reloj comercial deja de correr.
        var v = Evaluar(200, pendiente: false);
        Assert.Equal(BudgetValidityState.NoAplica, v.State);
        Assert.Equal(string.Empty, v.Label);
    }

    [Fact]
    public void PlazoPersonalizado_SeRespeta()
    {
        var v = BudgetValidityPolicy.Evaluate(Hoy.AddDays(-40), true, Hoy, validityDays: 60);
        Assert.Equal(BudgetValidityState.Vigente, v.State);
        Assert.Equal(20, v.DaysRemaining);
    }

    [Fact]
    public void PlazoInvalido_CaeAlDefault()
    {
        var v = BudgetValidityPolicy.Evaluate(Hoy, true, Hoy, validityDays: 0);
        Assert.Equal(BudgetValidityPolicy.DefaultValidityDays, v.DaysRemaining);
    }

    [Fact]
    public void LaHoraDeEmisionNoCorreElVencimiento()
    {
        // Emitido a las 23:59 y consultado a las 00:01 del día siguiente: sigue
        // faltando el plazo completo menos un día, no menos dos.
        var emision = new DateTime(2026, 7, 1, 23, 59, 0, DateTimeKind.Utc);
        var consulta = new DateTime(2026, 7, 2, 0, 1, 0, DateTimeKind.Utc);
        Assert.Equal(BudgetValidityPolicy.DefaultValidityDays - 1,
            BudgetValidityPolicy.Evaluate(emision, true, consulta).DaysRemaining);
    }
}
