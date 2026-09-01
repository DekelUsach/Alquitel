using Alquitel.Core.Privacy;

namespace Alquitel.Core.Tests;

public sealed class AiPrivacyRedactorTests
{
    [Fact]
    public void RedactaIdentificadoresPersonalesSinAlterarElPedido()
    {
        const string input =
            "Contacto: Juan Pérez; DNI 30.123.456, CUIT 20-30123456-7, email juan.perez+eventos@empresa.com, " +
            "teléfono +54 9 11 4567-8901, domicilio Av. Corrientes 1234, CABA. " +
            "Necesito 2 pantallas por 3 días a $ 150000.";

        var result = AiPrivacyRedactor.Redact(input);

        Assert.DoesNotContain("30.123.456", result.Text);
        Assert.DoesNotContain("20-30123456-7", result.Text);
        Assert.DoesNotContain("juan.perez", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4567-8901", result.Text);
        Assert.DoesNotContain("Corrientes 1234", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Juan Pérez", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[DNI REDACTADO]", result.Text);
        Assert.Contains("[CUIT REDACTADO]", result.Text);
        Assert.Contains("[EMAIL REDACTADO]", result.Text);
        Assert.Contains("[TELÉFONO REDACTADO]", result.Text);
        Assert.Contains("[DOMICILIO REDACTADO]", result.Text);
        Assert.Contains("[NOMBRE REDACTADO]", result.Text);
        Assert.Contains("2 pantallas por 3 días", result.Text);
        Assert.Contains("$ 150000", result.Text);
        Assert.True(result.ContainsSensitiveData);
    }

    [Fact]
    public void RedactaCbuCvuYAgrupaReemplazosRepetidos()
    {
        const string input =
            "CBU 2850590940090418135201; CVU 0000003100098765432109; " +
            "contacto uno@empresa.com o dos@empresa.com";

        var result = AiPrivacyRedactor.Redact(input);

        Assert.DoesNotContain("2850590940090418135201", result.Text);
        Assert.DoesNotContain("0000003100098765432109", result.Text);
        Assert.Equal(4, result.RedactionCount);
    }

    [Fact]
    public void RedactaEmpresaDomicilioSinPrefijoYFirmaConvencional()
    {
        const string input = """
            Empresa: Juan Pérez Producciones
            Entregar en Corrientes 1234, CABA.
            Saludos,
            Juan Pérez
            Producción general
            """;

        var result = AiPrivacyRedactor.Redact(input);

        Assert.DoesNotContain("Juan Pérez Producciones", result.Text);
        Assert.DoesNotContain("Corrientes 1234", result.Text);
        Assert.DoesNotContain("Juan Pérez", result.Text);
        Assert.Contains("[EMPRESA REDACTADA]", result.Text);
        Assert.Contains("[DOMICILIO REDACTADO]", result.Text);
        Assert.Contains("[FIRMA REDACTADA]", result.Text);
    }

    [Fact]
    public void RedactaNombresEnEncabezadosDeClienteYCorreo()
    {
        var result = AiPrivacyRedactor.Redact(
            "Cliente: Ana Pérez; De: Juan Pérez <juan@empresa.com>\nNecesito sonido.");

        Assert.DoesNotContain("Ana Pérez", result.Text);
        Assert.DoesNotContain("Juan Pérez", result.Text);
        Assert.DoesNotContain("juan@empresa.com", result.Text);
    }

    [Fact]
    public void NoConfundeModelosNumericosConDomicilios()
    {
        const string input = "Necesito un Proyector Epson 5000 lúmenes y 2 pantallas.";

        var result = AiPrivacyRedactor.Redact(input);

        Assert.Contains("Proyector Epson 5000 lúmenes", result.Text);
        Assert.DoesNotContain("[DOMICILIO REDACTADO]", result.Text);
    }

    [Fact]
    public void LimitaElTextoAntesDeEnviarloYLoMarcaComoTruncado()
    {
        var result = AiPrivacyRedactor.Redact(new string('x', 200), maxLength: 80);

        Assert.True(result.WasTruncated);
        Assert.True(result.Text.Length <= 80);
        Assert.EndsWith("[CONTENIDO TRUNCADO]", result.Text);
    }

    [Fact]
    public void RechazaLimitesQueNoPermitenUnaRedaccionSegura()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AiPrivacyRedactor.Redact("texto", maxLength: 16));
    }
}
