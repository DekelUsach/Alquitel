using Alquitel.Core.Entities;
using Alquitel.Core.Search;

namespace Alquitel.Core.Tests;

public class ProductMatcherTests
{
    private static readonly string[] StopWords = { "para", "con", "por", "los", "las", "una", "necesito" };

    private static List<Product> Catalogo() => new()
    {
        new Product { Description = "Pantalla LED 2.6mm P2", Category = "Visuales", BasePrice = 1200 },
        new Product { Description = "Notebook i9 Business Edition", Category = "Computación", BasePrice = 300 },
        new Product { Description = "Logitech MeetUp 4K", Category = "Cámaras", BasePrice = 150 },
        new Product { Description = "Servicio Técnico Plus (x Hora)", Category = "Servicios", BasePrice = 60 },
    };

    private static ProductMatcher CrearMatcher(double threshold = 3.0, double margin = 0.35)
        => new(Catalogo(), StopWords, threshold, margin);

    [Fact]
    public void BuildSegments_SeparaPorPuntuacionYConjuncion()
    {
        var segs = ProductMatcher.BuildSegments("2 pantallas de leds y 1 notebook i9, un servicio técnico");
        Assert.Equal(3, segs.Count);
        Assert.Contains("2 pantallas de leds", segs);
        Assert.Contains("1 notebook i9", segs);
        Assert.Contains("un servicio técnico", segs);
    }

    [Theory]
    [InlineData("2 pantallas de led", 2)]
    [InlineData("pantalla x 3", 3)]
    [InlineData("4 u de cable", 4)]
    [InlineData("pantalla de leds", 1)]      // sin cantidad → 1
    [InlineData("notebook i9", 1)]
    [InlineData("5 notebooks", 5)]
    public void ExtractQuantity_DetectaCantidades(string segmento, int esperado)
        => Assert.Equal(esperado, ProductMatcher.ExtractQuantity(segmento));

    [Fact]
    public void FindMatches_DetectaProductoYCantidad()
    {
        // Sin punto decimal en el texto: el segmentador corta por '.' (comportamiento
        // histórico del motor) y "2.6mm" partiría el segmento en dos.
        var matcher = CrearMatcher();
        var matches = matcher.FindMatches("Necesito 2 pantallas de led y 1 notebook i9 para un evento");

        Assert.Contains(matches, m => m.Product.Description.StartsWith("Pantalla LED") && m.Quantity == 2);
        Assert.Contains(matches, m => m.Product.Description.StartsWith("Notebook i9") && m.Quantity == 1);
    }

    [Fact]
    public void FindMatches_TextoSinProductos_DevuelveVacio()
    {
        var matcher = CrearMatcher();
        var matches = matcher.FindMatches("hola quería consultar por disponibilidad para el sábado");
        Assert.Empty(matches);
    }

    [Fact]
    public void FindMatches_UmbralAlto_DescartaCoincidenciasDebiles()
    {
        var estricto = CrearMatcher(threshold: 100);
        Assert.Empty(estricto.FindMatches("2 pantallas led"));
    }

    [Fact]
    public void SelectAiCandidates_CatalogoChico_DevuelveTodo()
    {
        var matcher = CrearMatcher();
        var candidatos = matcher.SelectAiCandidates("2 pantallas", fullCatalogLimit: 60);
        Assert.Equal(Catalogo().Count, candidatos.Count);
    }

    [Fact]
    public void SelectAiCandidates_CatalogoGrande_PreseleccionaRelevantes()
    {
        var productos = Catalogo();
        for (int i = 0; i < 100; i++)
            productos.Add(new Product { Description = $"Relleno genérico {i}", Category = "Otros" });

        var matcher = new ProductMatcher(productos, StopWords, 3.0, 0.35);
        var candidatos = matcher.SelectAiCandidates("2 pantallas led y 1 notebook i9",
            fullCatalogLimit: 60, candidatesPerSegment: 8, maxProducts: 80);

        Assert.True(candidatos.Count <= 80);
        Assert.Contains(candidatos, p => p.Description.StartsWith("Pantalla LED"));
        Assert.Contains(candidatos, p => p.Description.StartsWith("Notebook i9"));
    }

    [Theory]
    [InlineData("Pantalla LED 2.6mm", "pantalla led 2 6mm")]
    [InlineData("  CÁMARA  réflex ", "camara reflex")]
    [InlineData("", "")]
    public void NormalizeText_MinusculasSinDiacriticosNiSimbolos(string input, string expected)
        => Assert.Equal(expected, ProductMatcher.NormalizeText(input));

    [Fact]
    public void DiceCoefficient_IdenticosDaUno()
    {
        var a = ProductMatcher.Trigrams("pantalla led");
        Assert.Equal(1.0, ProductMatcher.DiceCoefficient(a, a), 3);
    }
}
