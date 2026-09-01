using Alquitel.Core.Privacy;

namespace Alquitel.Core.Tests;

public sealed class AiTechnicalNoteValidatorTests
{
    [Fact]
    public void ValidaTodasLasNotasAntesDeDevolverPropuestas()
    {
        const string json = """{"notas":[{"idx":1,"nota":"Incluir cable HDMI y soporte."},{"idx":3,"nota":"Verificar alimentación antes del montaje."}]}""";

        var valid = AiTechnicalNoteValidator.TryParse(
            json, new HashSet<int> { 1, 3 }, out var notes);

        Assert.True(valid);
        Assert.Equal(2, notes.Count);
        Assert.Equal(3, notes[1].Index);
    }

    [Theory]
    [InlineData("{\"notas\":[{\"idx\":1,\"nota\":\"válida\"},{\"idx\":9,\"nota\":\"fuera\"}]}")]
    [InlineData("{\"notas\":[{\"idx\":1,\"nota\":\"una\"},{\"idx\":1,\"nota\":\"otra\"}]}")]
    [InlineData("{\"notas\":[{\"idx\":1,\"nota\":\"uno dos tres cuatro cinco seis siete ocho nueve diez once doce trece catorce quince dieciséis\"}]}")]
    public void RechazaTodaLaRespuestaSiUnaNotaEsInvalida(string json)
    {
        var valid = AiTechnicalNoteValidator.TryParse(
            json, new HashSet<int> { 1, 3 }, out var notes);

        Assert.False(valid);
        Assert.Empty(notes);
    }
}
