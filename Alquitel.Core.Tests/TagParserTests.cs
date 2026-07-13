using Alquitel.Core.Parsing;

namespace Alquitel.Core.Tests;

public class TagParserTests
{
    [Fact]
    public void Parse_TextoPlano_UnSegmentoConDefaults()
    {
        var segs = TagParser.Parse("Pantalla LED 3x2");
        var s = Assert.Single(segs);
        Assert.Equal("Pantalla LED 3x2", s.Text);
        Assert.Equal("#000000", s.ColorHex);
        Assert.False(s.Bold);
        Assert.False(s.Italic);
        Assert.False(s.Underline);
    }

    [Fact]
    public void Parse_TextoVacioONull_DevuelveSegmentoVacioConDefaults()
    {
        foreach (var input in new[] { null, "" })
        {
            var segs = TagParser.Parse(input, defaultColorHex: "#123456", defaultBold: true, defaultUnderline: true);
            var s = Assert.Single(segs);
            Assert.Equal(string.Empty, s.Text);
            Assert.Equal("#123456", s.ColorHex);
            Assert.True(s.Bold);
            Assert.True(s.Underline);
        }
    }

    [Fact]
    public void Parse_ColorRojo_AplicaYRestaura()
    {
        var segs = TagParser.Parse("normal [red]rojo[/red] final");
        Assert.Equal(3, segs.Count);
        Assert.Equal("#000000", segs[0].ColorHex);
        Assert.Equal("rojo", segs[1].Text);
        Assert.Equal("#FF0000", segs[1].ColorHex);
        Assert.Equal("#000000", segs[2].ColorHex);
    }

    [Fact]
    public void Parse_EstilosAnidados_SeCombinanYDesapilan()
    {
        var segs = TagParser.Parse("[b]negrita [u]sub[/u] solo-negrita[/b]");
        Assert.Equal(3, segs.Count);
        Assert.True(segs[0].Bold);
        Assert.False(segs[0].Underline);
        Assert.True(segs[1].Bold);
        Assert.True(segs[1].Underline);
        Assert.True(segs[2].Bold);
        Assert.False(segs[2].Underline);
    }

    [Theory]
    [InlineData("[red]", "#FF0000")]
    [InlineData("[green]", "#006600")]
    [InlineData("[darkred]", "#C00000")]
    [InlineData("[blue]", "#1F68C7")]
    [InlineData("[white]", "#FFFFFF")]
    [InlineData("[black]", "#000000")]
    public void Parse_MapeoDeColores(string tag, string hexEsperado)
    {
        var segs = TagParser.Parse($"{tag}x[/{tag.Trim('[', ']')}]");
        Assert.Equal(hexEsperado, segs[0].ColorHex);
    }

    [Fact]
    public void Parse_TagDesconocido_QuedaComoTextoLiteral()
    {
        var segs = TagParser.Parse("medida [3x2] literal");
        var s = Assert.Single(segs);
        Assert.Equal("medida [3x2] literal", s.Text);
    }

    [Fact]
    public void Parse_CierreSinApertura_NoRompe()
    {
        var segs = TagParser.Parse("[/red]texto");
        var s = Assert.Single(segs);
        Assert.Equal("texto", s.Text);
        Assert.Equal("#000000", s.ColorHex);
    }

    [Fact]
    public void Parse_MayusculasEnTag_SeNormalizan()
    {
        var segs = TagParser.Parse("[RED]x[/RED]");
        Assert.Equal("#FF0000", segs[0].ColorHex);
    }

    [Fact]
    public void Parse_ItalicaSoportada()
    {
        var segs = TagParser.Parse("[i]cursiva[/i]");
        Assert.True(segs[0].Italic);
    }

    [Theory]
    [InlineData("hola [red]rojo[/red]", "hola rojo")]
    [InlineData("[b][u]x[/u][/b]", "x")]
    [InlineData("sin tags", "sin tags")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void StripTags_EliminaSoloTags(string? input, string? expected)
        => Assert.Equal(expected, TagParser.StripTags(input));
}
