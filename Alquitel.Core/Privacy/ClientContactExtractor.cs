using System.Text.RegularExpressions;

namespace Alquitel.Core.Privacy;

public sealed record ExtractedClientContact(
    string? CompanyName,
    string? ContactName,
    string? Phone,
    string? Email,
    string? Cuit);

/// <summary>
/// Extrae únicamente datos explícitamente etiquetados. Se ejecuta en el equipo y evita
/// enviar información de contacto a la IA externa para una tarea determinista.
/// </summary>
public static class ClientContactExtractor
{
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    private static readonly Regex CompanyPattern = new(
        @"^\s*(?:empresa|raz[oó]n\s+social)\s*:\s*(?<value>[^\r\n;]{2,120})\s*$",
        Options, Timeout);
    private static readonly Regex ContactPattern = new(
        @"^\s*(?:contacto|responsable)\s*:\s*(?<value>[^\r\n;]{2,100})\s*$",
        Options, Timeout);
    private static readonly Regex PhonePattern = new(
        @"^\s*(?:tel[eé]fono|tel\.?|celular|whatsapp)\s*:\s*(?<value>\+?[\d\s().-]{7,30})\s*$",
        Options, Timeout);
    private static readonly Regex EmailPattern = new(
        @"\b(?<value>[\w.!#$%&'*+/=?^`{|}~-]+@[\w-]+(?:\.[\w-]+)+)\b",
        Options, Timeout);
    private static readonly Regex CuitPattern = new(
        @"\b(?:CUIT|CUIL)\s*:\s*(?<value>\d{2}[.-]?\d{8}[.-]?\d)\b",
        Options, Timeout);

    public static ExtractedClientContact Extract(string? input)
    {
        var text = input ?? string.Empty;
        if (text.Length > 12_000) text = text[..12_000];
        return new ExtractedClientContact(
            MatchValue(CompanyPattern, text),
            MatchValue(ContactPattern, text),
            MatchValue(PhonePattern, text),
            MatchValue(EmailPattern, text),
            MatchValue(CuitPattern, text));
    }

    private static string? MatchValue(Regex pattern, string input)
    {
        var match = pattern.Match(input);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }
}
