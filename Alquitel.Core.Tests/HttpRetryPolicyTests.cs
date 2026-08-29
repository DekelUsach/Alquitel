using Alquitel.Core.Helpers;

namespace Alquitel.Core.Tests;

public class HttpRetryPolicyTests
{
    [Theory]
    [InlineData(408)] // request timeout
    [InlineData(425)] // too early
    [InlineData(429)] // rate limit — el más común en el plan gratuito de Pollinations
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void FallosTransitorios_SeReintentan(int status)
        => Assert.True(HttpRetryPolicy.IsTransient(status));

    [Theory]
    [InlineData(200)]
    [InlineData(400)] // pedido mal armado: reintentarlo solo quema tiempo
    [InlineData(401)] // API key mal configurada
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public void FallosDefinitivos_NoSeReintentan(int status)
        => Assert.False(HttpRetryPolicy.IsTransient(status));

    [Fact]
    public void ElBackoffEsExponencial()
    {
        var d1 = HttpRetryPolicy.DelayFor(1);
        var d2 = HttpRetryPolicy.DelayFor(2);
        var d3 = HttpRetryPolicy.DelayFor(3);

        Assert.Equal(HttpRetryPolicy.BaseDelay, d1);
        Assert.Equal(d1 * 2, d2);
        Assert.Equal(d1 * 4, d3);
    }

    [Fact]
    public void ElBackoffTieneTecho()
    {
        Assert.Equal(HttpRetryPolicy.MaxDelay, HttpRetryPolicy.DelayFor(50));
    }

    [Fact]
    public void RetryAfterDelServidor_TienePrioridad()
    {
        var pedido = TimeSpan.FromSeconds(7);
        Assert.Equal(pedido, HttpRetryPolicy.DelayFor(1, pedido));
    }

    [Fact]
    public void RetryAfterAbsurdo_SeAcota()
    {
        // Un Retry-After de una hora congelaría el armador: se recorta al techo.
        Assert.Equal(HttpRetryPolicy.MaxDelay, HttpRetryPolicy.DelayFor(1, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void RetryAfterNoPositivo_SeIgnora()
    {
        Assert.Equal(HttpRetryPolicy.BaseDelay, HttpRetryPolicy.DelayFor(1, TimeSpan.Zero));
        Assert.Equal(HttpRetryPolicy.BaseDelay, HttpRetryPolicy.DelayFor(1, TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void AttemptInvalido_SeTrataComoElPrimero()
    {
        Assert.Equal(HttpRetryPolicy.BaseDelay, HttpRetryPolicy.DelayFor(0));
        Assert.Equal(HttpRetryPolicy.BaseDelay, HttpRetryPolicy.DelayFor(-3));
    }
}
