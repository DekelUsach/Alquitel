using Alquitel.Core.Privacy;

namespace Alquitel.Core.Tests;

public sealed class ClientContactExtractorTests
{
    [Fact]
    public void ExtraeDatosEtiquetadosLocalmente()
    {
        const string text = """
            Empresa: Eventos del Sur SA
            Contacto: Ana Pérez
            Tel: +54 9 11 4567-8901
            Email: ana@eventosdelsur.com.ar
            CUIT: 30-71654321-9
            Necesitamos dos pantallas.
            """;

        var result = ClientContactExtractor.Extract(text);

        Assert.Equal("Eventos del Sur SA", result.CompanyName);
        Assert.Equal("Ana Pérez", result.ContactName);
        Assert.Equal("+54 9 11 4567-8901", result.Phone);
        Assert.Equal("ana@eventosdelsur.com.ar", result.Email);
        Assert.Equal("30-71654321-9", result.Cuit);
    }

    [Fact]
    public void NoInventaDatosNiConfundeCantidadesConTelefonos()
    {
        var result = ClientContactExtractor.Extract(
            "Necesito 2 pantallas de 8 x 3 metros durante 5 días.");

        Assert.Null(result.CompanyName);
        Assert.Null(result.ContactName);
        Assert.Null(result.Phone);
        Assert.Null(result.Email);
        Assert.Null(result.Cuit);
    }
}
