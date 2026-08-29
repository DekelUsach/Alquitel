using Alquitel.Core.Helpers;

namespace Alquitel.Core.Tests;

public class ContactValidatorTests
{
    [Theory]
    [InlineData(null)]         // campo opcional
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("juan@empresa.com.ar")]
    [InlineData("j.perez+eventos@sub.dominio.com")]
    [InlineData("PRODUCCION@ALQUITEL.COM.AR")]
    public void EmailsAceptables(string? email)
        => Assert.Null(ContactValidator.ValidateEmail(email));

    [Theory]
    [InlineData("sin-arroba.com")]
    [InlineData("@sindestinatario.com")]
    [InlineData("doble@@arroba.com")]
    [InlineData("dos@arrobas@com.ar")]
    [InlineData("juan@sinpunto")]
    [InlineData("juan@.empieza-con-punto.com")]
    [InlineData("juan@termina-con-punto.")]
    [InlineData("juan@doble..punto.com")]
    [InlineData("con espacio@dominio.com")]
    public void EmailsRechazados(string email)
        => Assert.NotNull(ContactValidator.ValidateEmail(email));

    [Fact]
    public void EmailDemasiadoLargo_SeRechaza()
        => Assert.NotNull(ContactValidator.ValidateEmail(new string('a', 250) + "@dominio.com"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+54 9 11 4567-8900")]
    [InlineData("(011) 4567-8900")]
    [InlineData("11 4567 8900")]
    [InlineData("4567-8900")]
    public void TelefonosAceptables(string? phone)
        => Assert.Null(ContactValidator.ValidatePhone(phone));

    [Theory]
    [InlineData("12345")]                      // pocos dígitos
    [InlineData("1234567890123456789")]        // demasiados dígitos
    [InlineData("llamar al fijo")]             // letras
    public void TelefonosRechazados(string phone)
        => Assert.NotNull(ContactValidator.ValidatePhone(phone));
}
