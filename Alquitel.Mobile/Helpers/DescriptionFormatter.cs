using Alquitel.Core.Parsing;

namespace Alquitel.Mobile.Helpers;

/// <summary>
/// Renderiza la descripción segmentada BBCode-style ([red], [b], [i], [u]) de un
/// producto como FormattedString para Labels de MAUI, con colores adaptados al tema.
/// </summary>
public static class DescriptionFormatter
{
    public static FormattedString ToFormatted(string? description, bool bold = false, double fontSize = 15)
    {
        var fs = new FormattedString();
        bool dark = Application.Current?.RequestedTheme == AppTheme.Dark;

        foreach (var seg in TagParser.Parse(description, defaultColorHex: dark ? "#F1F5F9" : "#0F172A"))
        {
            if (string.IsNullOrEmpty(seg.Text)) continue;

            var attrs = FontAttributes.None;
            if (seg.Bold || bold) attrs |= FontAttributes.Bold;
            if (seg.Italic) attrs |= FontAttributes.Italic;

            fs.Spans.Add(new Span
            {
                Text = seg.Text,
                TextColor = ResolveColor(seg.ColorHex, dark),
                FontAttributes = attrs,
                TextDecorations = seg.Underline ? TextDecorations.Underline : TextDecorations.None,
                FontSize = fontSize,
            });
        }
        return fs;
    }

    private static Color ResolveColor(string hex, bool darkTheme)
    {
        // El negro por defecto del catálogo se adapta al tema; los colores de marca
        // (rojo, verde, rojo oscuro) se mantienen legibles en dark aclarándolos.
        if (!Color.TryParse(hex, out var color))
            return darkTheme ? Colors.White : Colors.Black;

        if (!darkTheme) return color;

        return hex.ToUpperInvariant() switch
        {
            "#000000" => Color.FromArgb("#F1F5F9"),
            "#FF0000" => Color.FromArgb("#FF6B6B"),
            "#006600" => Color.FromArgb("#4ADE80"),
            "#C00000" => Color.FromArgb("#F87171"),
            _ => color,
        };
    }
}
