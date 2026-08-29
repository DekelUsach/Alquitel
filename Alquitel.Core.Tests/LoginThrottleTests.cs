using Alquitel.Core.Security;

namespace Alquitel.Core.Tests;

public class LoginThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);
    private const string Usuario = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void PrimerosIntentos_NoBloquean()
    {
        var throttle = new LoginThrottle();
        for (int i = 0; i < LoginThrottle.FreeAttempts; i++)
            Assert.Equal(TimeSpan.Zero, throttle.RegisterFailure(Usuario, T0));

        Assert.Equal(TimeSpan.Zero, throttle.GetRemainingLockout(Usuario, T0));
    }

    [Fact]
    public void PasadoElMargen_ElBloqueoCrece()
    {
        var throttle = new LoginThrottle();
        for (int i = 0; i < LoginThrottle.FreeAttempts; i++) throttle.RegisterFailure(Usuario, T0);

        var primero = throttle.RegisterFailure(Usuario, T0);
        var segundo = throttle.RegisterFailure(Usuario, T0);
        var tercero = throttle.RegisterFailure(Usuario, T0);

        Assert.Equal(TimeSpan.FromSeconds(2), primero);
        Assert.Equal(TimeSpan.FromSeconds(4), segundo);
        Assert.Equal(TimeSpan.FromSeconds(8), tercero);
    }

    [Fact]
    public void ElBloqueoTieneTecho()
    {
        var throttle = new LoginThrottle();
        TimeSpan ultimo = TimeSpan.Zero;
        for (int i = 0; i < 40; i++) ultimo = throttle.RegisterFailure(Usuario, T0);

        // Sin techo, 2^37 segundos desbordaba el cálculo y devolvía un valor sin sentido.
        Assert.Equal(LoginThrottle.MaxLockout, ultimo);
    }

    [Fact]
    public void ElBloqueoSeConsumeConElTiempo()
    {
        var throttle = new LoginThrottle();
        for (int i = 0; i < LoginThrottle.FreeAttempts + 1; i++) throttle.RegisterFailure(Usuario, T0);

        Assert.Equal(TimeSpan.FromSeconds(2), throttle.GetRemainingLockout(Usuario, T0));
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.GetRemainingLockout(Usuario, T0.AddSeconds(1)));
        Assert.Equal(TimeSpan.Zero, throttle.GetRemainingLockout(Usuario, T0.AddSeconds(3)));
    }

    [Fact]
    public void LoginExitoso_LimpiaElHistorial()
    {
        var throttle = new LoginThrottle();
        for (int i = 0; i < 6; i++) throttle.RegisterFailure(Usuario, T0);
        Assert.True(throttle.GetRemainingLockout(Usuario, T0) > TimeSpan.Zero);

        throttle.Reset(Usuario);

        Assert.Equal(TimeSpan.Zero, throttle.GetRemainingLockout(Usuario, T0));
        Assert.Equal(0, throttle.FailureCount(Usuario, T0));
    }

    [Fact]
    public void FallosViejos_SeOlvidan()
    {
        // Equivocarse tres veces ayer no debe penalizar el intento de hoy.
        var throttle = new LoginThrottle();
        for (int i = 0; i < 5; i++) throttle.RegisterFailure(Usuario, T0);

        var despues = T0 + LoginThrottle.FailureWindow + TimeSpan.FromMinutes(1);
        Assert.Equal(TimeSpan.Zero, throttle.GetRemainingLockout(Usuario, despues));
        Assert.Equal(TimeSpan.Zero, throttle.RegisterFailure(Usuario, despues));
    }

    [Fact]
    public void ElBloqueoEsPorUsuario()
    {
        var throttle = new LoginThrottle();
        for (int i = 0; i < 6; i++) throttle.RegisterFailure(Usuario, T0);

        Assert.True(throttle.GetRemainingLockout(Usuario, T0) > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, throttle.GetRemainingLockout("otro-usuario", T0));
    }

    [Fact]
    public void ClaveVacia_NoRompe()
    {
        var throttle = new LoginThrottle();
        Assert.Equal(TimeSpan.Zero, throttle.RegisterFailure("", T0));
        Assert.Equal(TimeSpan.Zero, throttle.GetRemainingLockout("", T0));
        throttle.Reset("");
    }
}
