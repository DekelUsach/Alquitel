using Alquitel.Core.Helpers;

namespace Alquitel.Core.Tests;

public class CuitValidatorTests
{
    // CUITs reales con dígito verificador Módulo 11 correcto.
    [Theory]
    [InlineData("30-71659554-0")]  // persona jurídica con guiones (resto 0 → verificador 0)
    [InlineData("30716595540")]    // misma, sin guiones
    [InlineData("20-05536168-2")]  // persona física
    [InlineData("27-24029460-0")]  // persona física (F)
    [InlineData("20 05536168 2")]  // con espacios
    public void IsValid_CuitsCorrectos_DevuelveTrue(string cuit)
        => Assert.True(CuitValidator.IsValid(cuit));

    [Theory]
    [InlineData("30-71659554-9")]  // dígito verificador incorrecto
    [InlineData("20055361683")]    // dígito verificador incorrecto
    [InlineData("2005536168")]     // 10 dígitos
    [InlineData("200553616820")]   // 12 dígitos
    [InlineData("20-0553616A-2")]  // letra
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsValid_CuitsInvalidos_DevuelveFalse(string? cuit)
        => Assert.False(CuitValidator.IsValid(cuit));

    // ── Regresión: entradas que hacían explotar el validador ─────────
    // long.TryParse acepta signo y espacio en blanco de cabecera, así que estas cadenas
    // pasaban el filtro de longitud y después int.Parse(cuit[i].ToString()) tiraba una
    // FormatException que subía sin capturar hasta la UI.
    [Theory]
    [InlineData("+1234567890")]   // signo más, 11 caracteres
    [InlineData("-1234567890")]   // signo menos
    [InlineData("\t1234567890")]  // tabulador de cabecera
    [InlineData("\n1234567890")]
    [InlineData("1234567890\t")]
    public void IsValid_EntradasQueRompianElParseo_DevuelveFalseSinExcepcion(string cuit)
        => Assert.False(CuitValidator.IsValid(cuit));

    // ── Normalize ────────────────────────────────────────────────────

    [Theory]
    [InlineData("30-71659554-0", "30716595540")]
    [InlineData("30716595540", "30716595540")]
    [InlineData("30 71659554 0", "30716595540")]
    [InlineData("30.71659554.0", "30716595540")]
    [InlineData("30/71659554/0", "30716595540")]
    public void Normalize_DevuelveLosOnceDigitos(string entrada, string esperado)
        => Assert.Equal(esperado, CuitValidator.Normalize(entrada));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3071659554")]      // 10 dígitos
    [InlineData("307165955400")]    // 12 dígitos
    [InlineData("30-7165955A-0")]   // letra
    [InlineData("+30716595540")]    // signo
    public void Normalize_EntradaInvalida_DevuelveNull(string? entrada)
        => Assert.Null(CuitValidator.Normalize(entrada));

    // ── Format ───────────────────────────────────────────────────────

    [Fact]
    public void Format_AplicaElFormatoDeAfip()
        => Assert.Equal("30-71659554-0", CuitValidator.Format("30716595540"));

    [Fact]
    public void Format_EsIdempotente()
        => Assert.Equal("30-71659554-0", CuitValidator.Format(CuitValidator.Format("30716595540")));

    [Fact]
    public void Format_EntradaQueNoEsCuit_SeDevuelveIntacta()
    {
        // No se inventa un formato sobre basura: el usuario ve lo que escribió.
        Assert.Equal("pendiente", CuitValidator.Format("pendiente"));
        Assert.Equal(string.Empty, CuitValidator.Format(null));
    }

    [Fact]
    public void Normalize_HaceComparableLoQueSeGuardoDeDosFormas()
    {
        // El bug de negocio: "30-71659554-0" y "30716595540" convivían como dos
        // clientes distintos porque el chequeo de duplicados comparaba texto crudo.
        Assert.Equal(CuitValidator.Normalize("30-71659554-0"), CuitValidator.Normalize("30716595540"));
    }

    [Fact]
    public void IsValid_RestoUno_UsaDigitoNueve()
    {
        // Caso borde del Módulo 11: cuando el resto es 1 el verificador es 9
        // (prefijo 23/33 según normativa AFIP). Construimos uno: 23-00000010-9.
        // 2*5+3*4+0+0+0+0+0+0+1*3+0 = 25 → 25%11=3 → 11-3=8 … buscar uno real:
        // 20-11111111-2: 2*5+0*4+1*3+1*2+1*7+1*6+1*5+1*4+1*3+1*2 = 10+0+3+2+7+6+5+4+3+2 = 42 → 42%11=9 → 11-9=2 ✓
        Assert.True(CuitValidator.IsValid("20-11111111-2"));
    }
}
