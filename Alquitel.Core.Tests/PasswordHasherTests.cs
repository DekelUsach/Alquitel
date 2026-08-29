using Alquitel.Core.Helpers;

namespace Alquitel.Core.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_YVerify_HacenRoundTrip()
    {
        var hash = PasswordHasher.Hash("clave-secreta-123");
        Assert.True(PasswordHasher.Verify("clave-secreta-123", hash));
    }

    [Fact]
    public void Verify_ContraseñaDistinta_DevuelveFalse()
    {
        var hash = PasswordHasher.Hash("clave-secreta-123");
        Assert.False(PasswordHasher.Verify("clave-secreta-124", hash));
    }

    [Fact]
    public void Hash_MismaClaveDosVeces_ProduceHashesDistintos()
    {
        // Salt aleatorio por hash: dos usuarios con la misma contraseña no deben
        // compartir el hash (permitiría deducir uno del otro con solo mirar la tabla).
        Assert.NotEqual(PasswordHasher.Hash("igual"), PasswordHasher.Hash("igual"));
    }

    [Fact]
    public void Hash_UsaLasIteracionesVigentes()
    {
        var hash = PasswordHasher.Hash("x");
        Assert.StartsWith(PasswordHasher.CurrentIterations + ".", hash);
    }

    // ── Robustez ante hashes corruptos o manipulados ─────────────────
    // El Verify anterior solo atrapaba FormatException: un conteo de iteraciones
    // desbordado o negativo tiraba la excepción hacia arriba y volteaba el login.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sin-puntos")]
    [InlineData("solo.dos")]
    [InlineData("uno.dos.tres.cuatro")]
    [InlineData("abc.c2FsdA==.aGFzaA==")]              // iteraciones no numéricas
    [InlineData("99999999999999999999.c2FsdA==.aGFzaA==")] // overflow de int
    [InlineData("-5.c2FsdA==.aGFzaA==")]               // iteraciones negativas
    [InlineData("0.c2FsdA==.aGFzaA==")]                // cero iteraciones
    [InlineData("100000.no-es-base64.aGFzaA==")]       // salt inválido
    [InlineData("100000.c2FsdA==.$$$$")]               // hash inválido
    public void Verify_HashCorrupto_DevuelveFalseSinExcepcion(string? storedHash)
    {
        Assert.False(PasswordHasher.Verify("cualquiera", storedHash));
    }

    [Fact]
    public void Verify_IteracionesAbsurdamenteAltas_SeRechazaSinColgarse()
    {
        // Defensa de disponibilidad: 500 millones de iteraciones bloquearían el login
        // por minutos. Se rechaza por fuera de rango en vez de ejecutarse.
        Assert.False(PasswordHasher.Verify("x", "500000000.c2FsdHNhbHRzYWx0c2E=.aGFzaGhhc2hoYXNoaGFzaGhhc2g="));
    }

    [Fact]
    public void Verify_SaltODemasiadoCorto_SeRechaza()
    {
        // "c2E=" = "sa" (2 bytes): un salt así no aporta entropía real.
        Assert.False(PasswordHasher.Verify("x", "100000.c2E=.aGFzaGhhc2hoYXNoaGFzaGhhc2g="));
    }

    // ── NeedsRehash ──────────────────────────────────────────────────

    [Fact]
    public void NeedsRehash_HashViejoConMenosIteraciones_DevuelveTrue()
    {
        Assert.True(PasswordHasher.NeedsRehash("100000.c2FsdHNhbHRzYWx0c2E=.aGFzaGhhc2hoYXNoaGFzaGhhc2g="));
    }

    [Fact]
    public void NeedsRehash_HashRecien_DevuelveFalse()
    {
        Assert.False(PasswordHasher.NeedsRehash(PasswordHasher.Hash("x")));
    }

    [Fact]
    public void NeedsRehash_HashInvalido_DevuelveFalse()
    {
        Assert.False(PasswordHasher.NeedsRehash("basura"));
    }

    // ── Fingerprint ──────────────────────────────────────────────────

    [Fact]
    public void Fingerprint_EsEstableParaElMismoHash()
    {
        var hash = PasswordHasher.Hash("x");
        Assert.Equal(PasswordHasher.Fingerprint(hash), PasswordHasher.Fingerprint(hash));
    }

    [Fact]
    public void Fingerprint_CambiaAlCambiarLaContraseña()
    {
        // Es lo que hace que una sesión guardada muera cuando el Admin cambia la clave.
        var antes = PasswordHasher.Fingerprint(PasswordHasher.Hash("vieja"));
        var despues = PasswordHasher.Fingerprint(PasswordHasher.Hash("nueva"));
        Assert.NotEqual(antes, despues);
    }

    [Fact]
    public void Fingerprint_NoFiltraElHash()
    {
        var hash = PasswordHasher.Hash("x");
        Assert.DoesNotContain(hash, PasswordHasher.Fingerprint(hash));
    }

    [Fact]
    public void Fingerprint_UsuarioSinContraseña_TieneHuellaEstable()
    {
        Assert.Equal(PasswordHasher.Fingerprint(null), PasswordHasher.Fingerprint(null));
        Assert.NotEqual(PasswordHasher.Fingerprint(null), PasswordHasher.Fingerprint(PasswordHasher.Hash("x")));
    }

    // ── ValidateNewPassword ──────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corta")]
    [InlineData(" conespacios ")]
    public void ValidateNewPassword_Rechaza(string? password)
        => Assert.NotNull(PasswordHasher.ValidateNewPassword(password));

    [Fact]
    public void ValidateNewPassword_Acepta()
        => Assert.Null(PasswordHasher.ValidateNewPassword("clave-larga-ok"));
}
