using Alquitel.Core.Helpers;

namespace Alquitel.Core.Tests;

public class LocationNameNormalizerTests
{
    // ── Equivalencias: son los duplicados que el padrón tiene que juntar ─────

    [Theory]
    [InlineData("La Rural ", "la rural")]
    [InlineData("LA RURAL", "la rural")]
    [InlineData("  La Rural  ", "La Rural")]
    [InlineData("Pabellón", "pabellon")]
    [InlineData("Costa   Salguero", "Costa Salguero")]
    [InlineData("costa\tsalguero", "Costa Salguero")]
    [InlineData("Predio de La Rural", "PREDIO DE LA RURAL")]
    public void NombresEquivalentes_CompartenClave(string a, string b)
        => Assert.Equal(LocationNameNormalizer.Normalize(a), LocationNameNormalizer.Normalize(b));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void VacioONulo_DaClaveVacia(string? entrada)
        => Assert.Equal(string.Empty, LocationNameNormalizer.Normalize(entrada));

    [Fact]
    public void Normalize_EsIdempotente()
    {
        var una = LocationNameNormalizer.Normalize("  Predio  de La RURAL ");
        Assert.Equal(una, LocationNameNormalizer.Normalize(una));
    }

    // ── Regresión anti-fuzzy ────────────────────────────────────────────────
    // El jurado vetó la heurística difusa (Dice + stop-words) justamente porque
    // juntaba pabellones distintos, y la fusión mueve presupuestos y no se deshace.

    [Theory]
    [InlineData("Costa Salguero Pabellón 1", "Costa Salguero Pabellón 4")]
    [InlineData("La Rural", "Predio La Rural")]
    [InlineData("Salón Norte", "Salón Sur")]
    [InlineData("Centro de Convenciones", "Centro Cultural")]
    public void LugaresDistintos_NoSeConfunden(string a, string b)
        => Assert.NotEqual(LocationNameNormalizer.Normalize(a), LocationNameNormalizer.Normalize(b));

    // ── Centinela ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("(Sin ubicación)")]
    [InlineData("(sin ubicación)")]
    [InlineData("(Sin Ubicacion)")]
    [InlineData("  (SIN UBICACIÓN)  ")]
    public void IsSentinel_ReconoceLasVariantes(string nombre)
        => Assert.True(LocationNameNormalizer.IsSentinel(nombre));

    [Theory]
    [InlineData("La Rural")]
    [InlineData("Sin ubicación")]   // sin paréntesis: es un lugar real mal nombrado
    [InlineData("")]
    [InlineData(null)]
    public void IsSentinel_NoSeExcedeConOtrosNombres(string? nombre)
        => Assert.False(LocationNameNormalizer.IsSentinel(nombre));

    [Fact]
    public void IsSentinel_UsaLaConstantePublica()
        => Assert.True(LocationNameNormalizer.IsSentinel(LocationNameNormalizer.SentinelName));

    // ── DuplicateKeys ───────────────────────────────────────────────────────

    [Fact]
    public void DuplicateKeys_TresVariantesDelMismoLugar_DanUnaSolaClave()
    {
        var claves = LocationNameNormalizer.DuplicateKeys(
            new[] { "La Rural", "la rural", "LA RURAL ", "Costa Salguero" });

        Assert.Single(claves);
        Assert.Contains("la rural", claves);
    }

    [Fact]
    public void DuplicateKeys_SinRepetidos_DevuelveVacio()
    {
        var claves = LocationNameNormalizer.DuplicateKeys(
            new[] { "La Rural", "Costa Salguero", "Parque Norte" });

        Assert.Empty(claves);
    }

    [Fact]
    public void DuplicateKeys_VariosGruposALaVez()
    {
        var claves = LocationNameNormalizer.DuplicateKeys(
            new[] { "La Rural", "la rural", "Costa Salguero", "COSTA SALGUERO", "Parque Norte" });

        Assert.Equal(2, claves.Count);
        Assert.Contains("la rural", claves);
        Assert.Contains("costa salguero", claves);
    }

    [Fact]
    public void DuplicateKeys_LosSinNombreNoCuentanComoDuplicadosEntreSi()
    {
        // Dos lugares con nombre vacío son "sin nombre", que la UI marca aparte:
        // fusionarlos entre sí no tiene sentido.
        var claves = LocationNameNormalizer.DuplicateKeys(new[] { "", "   ", null, "La Rural" });
        Assert.Empty(claves);
    }

    [Fact]
    public void DuplicateKeys_ColeccionVacia_NoRompe()
        => Assert.Empty(LocationNameNormalizer.DuplicateKeys(Array.Empty<string>()));
}
