using System.Security.Cryptography;
using Alquitel.Core.Security;

namespace Alquitel.Core.Tests;

public class SessionTokenTests
{
    private static readonly byte[] Key = Enumerable.Range(0, SessionToken.KeySize).Select(i => (byte)i).ToArray();
    private static readonly byte[] OtraKey = Enumerable.Range(100, SessionToken.KeySize).Select(i => (byte)i).ToArray();

    private static readonly DateTimeOffset Ahora = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private const string Huella = "ABCDEF0123456789";

    private static bool Validar(string? token, byte[]? key = null, DateTimeOffset? now = null, TimeSpan? maxAge = null) =>
        SessionToken.TryValidate(token, key ?? Key, now ?? Ahora, maxAge ?? TimeSpan.FromDays(30),
            out _, out _, out _);

    [Fact]
    public void Issue_YTryValidate_DevuelvenLosMismosDatos()
    {
        var user = Guid.NewGuid();
        var token = SessionToken.Issue(user, Huella, Ahora, Key);

        Assert.True(SessionToken.TryValidate(token, Key, Ahora, TimeSpan.FromDays(30),
            out var userId, out var huella, out var savedAt));
        Assert.Equal(user, userId);
        Assert.Equal(Huella, huella);
        Assert.Equal(Ahora.ToUnixTimeSeconds(), savedAt.ToUnixTimeSeconds());
    }

    // ── El escenario que motivó todo esto ────────────────────────────
    // El archivo de sesión anterior era JSON plano: cambiar el Guid a mano por el de un
    // Admin salteaba el login con contraseña.

    [Fact]
    public void TryValidate_PayloadEditadoAMano_Falla()
    {
        var vendedor = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var token = SessionToken.Issue(vendedor, Huella, Ahora, Key);

        var falsificado = token.Replace(vendedor.ToString("D"), admin.ToString("D"));

        Assert.NotEqual(token, falsificado);
        Assert.False(Validar(falsificado));
    }

    [Fact]
    public void TryValidate_HuellaCambiada_Falla()
    {
        var token = SessionToken.Issue(Guid.NewGuid(), Huella, Ahora, Key);
        Assert.False(Validar(token.Replace(Huella, "0000000000000000")));
    }

    [Fact]
    public void TryValidate_ClaveDistinta_Falla()
    {
        // Sesión copiada a otra máquina/usuario: la clave DPAPI de allá es otra.
        var token = SessionToken.Issue(Guid.NewGuid(), Huella, Ahora, Key);
        Assert.False(Validar(token, key: OtraKey));
    }

    [Fact]
    public void TryValidate_FirmaTruncada_Falla()
    {
        var token = SessionToken.Issue(Guid.NewGuid(), Huella, Ahora, Key);
        Assert.False(Validar(token[..^4]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sin-punto")]
    [InlineData("1|abc|def|123.")]
    [InlineData(".firma")]
    [InlineData("1|no-es-guid|huella|123.QUJD")]
    public void TryValidate_TokenMalformado_Falla(string? token)
        => Assert.False(Validar(token));

    // ── Vigencia ─────────────────────────────────────────────────────

    [Fact]
    public void TryValidate_SesionVencida_Falla()
    {
        var token = SessionToken.Issue(Guid.NewGuid(), Huella, Ahora.AddDays(-31), Key);
        Assert.False(Validar(token, maxAge: TimeSpan.FromDays(30)));
    }

    [Fact]
    public void TryValidate_SesionDentroDelPlazo_Pasa()
    {
        var token = SessionToken.Issue(Guid.NewGuid(), Huella, Ahora.AddDays(-29), Key);
        Assert.True(Validar(token, maxAge: TimeSpan.FromDays(30)));
    }

    [Fact]
    public void TryValidate_MaxAgeExcesivo_SeAcotaAlTopeGlobal()
    {
        // Pedir 10 años no debe habilitar 10 años: el tope duro son 90 días.
        var token = SessionToken.Issue(Guid.NewGuid(), Huella, Ahora.AddDays(-120), Key);
        Assert.False(Validar(token, maxAge: TimeSpan.FromDays(3650)));
    }

    [Fact]
    public void TryValidate_SesionConFechaFutura_Falla()
    {
        // Reloj corrido hacia atrás o timestamp manipulado para estirar la vigencia.
        var token = SessionToken.Issue(Guid.NewGuid(), Huella, Ahora.AddHours(2), Key);
        Assert.False(Validar(token));
    }

    [Fact]
    public void TryValidate_ClaveDemasiadoCorta_Falla()
    {
        var token = SessionToken.Issue(Guid.NewGuid(), Huella, Ahora, Key);
        Assert.False(Validar(token, key: new byte[4]));
    }

    [Fact]
    public void Issue_ClaveDemasiadoCorta_Lanza()
        => Assert.Throws<ArgumentException>(() => SessionToken.Issue(Guid.NewGuid(), Huella, Ahora, new byte[4]));

    [Fact]
    public void Issue_ClavesAleatoriasDistintas_ProducenFirmasDistintas()
    {
        var user = Guid.NewGuid();
        var a = SessionToken.Issue(user, Huella, Ahora, RandomNumberGenerator.GetBytes(SessionToken.KeySize));
        var b = SessionToken.Issue(user, Huella, Ahora, RandomNumberGenerator.GetBytes(SessionToken.KeySize));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Issue_HuellaNula_SigueSiendoValidable()
    {
        // Usuario sin contraseña (Vendedor): la huella es la de "sin hash".
        var user = Guid.NewGuid();
        var token = SessionToken.Issue(user, null, Ahora, Key);
        Assert.True(SessionToken.TryValidate(token, Key, Ahora, TimeSpan.FromDays(30),
            out var userId, out var huella, out _));
        Assert.Equal(user, userId);
        Assert.Equal(string.Empty, huella);
    }
}
