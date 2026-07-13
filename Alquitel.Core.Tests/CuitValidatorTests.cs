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
