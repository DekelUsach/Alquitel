using System.Text.RegularExpressions;

namespace Alquitel.Core.Privacy;

public sealed record AiRedactionResult(
    string Text,
    int RedactionCount,
    bool WasTruncated)
{
    public bool ContainsSensitiveData => RedactionCount > 0;
}

/// <summary>
/// Redacción local, determinista y conservadora para textos que podrían enviarse a un
/// proveedor externo. No intenta identificar nombres propios porque hacerlo con reglas
/// heurísticas degradaría el pedido; sí elimina identificadores estructurados.
/// </summary>
public static class AiPrivacyRedactor
{
    private const int MinimumSafeLength = 32;
    private const string TruncationMarker = "[CONTENIDO TRUNCADO]";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
        RegexOptions.Compiled | RegexOptions.Multiline;

    private static readonly Regex EmailPattern = new(
        @"\b[A-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Z0-9-]+(?:\.[A-Z0-9-]+)+\b",
        Options, RegexTimeout);

    private static readonly Regex BankAccountPattern = new(
        @"\b(?:CBU|CVU)\s*[:#-]?\s*\d(?:[\s-]?\d){21}\b",
        Options, RegexTimeout);

    private static readonly Regex CuitPattern = new(
        @"\b(?:CUIT|CUIL)?\s*[:#-]?\s*(?:\d{2}[\s.-]?\d{8}[\s.-]?\d)\b",
        Options, RegexTimeout);

    private static readonly Regex DniPattern = new(
        @"\b(?:DNI|DOCUMENTO)\s*[:#-]?\s*\d{1,2}(?:[.\s]?\d{3}){2}\b",
        Options, RegexTimeout);

    private static readonly Regex AddressPattern = new(
        @"\b(?:(?:domicilio|direcci[oó]n)\s*[:#-]?\s*(?:(?:calle|avenida|av\.)\s*)?|(?:calle|avenida|av\.)\s*)[^,;\r\n.]{3,80}(?=[,;\r\n.]|$)",
        Options, RegexTimeout);

    private static readonly Regex LabeledNamePattern = new(
        @"\b(?:contacto|responsable|remitente|nombre|cliente|de|from|para|to)\s*[:#-]\s*[^,;\r\n.]{2,100}(?=[,;\r\n.]|$)",
        Options, RegexTimeout);

    private static readonly Regex LabeledCompanyPattern = new(
        @"^\s*(?:empresa|raz[oó]n\s+social)\s*[:#-]\s*[^;\r\n]{2,120}\s*$",
        Options, RegexTimeout);

    private static readonly Regex ContextualStreetAddressPattern = new(
        @"\b(?:entregar|retirar|retiro|env[ií]o|evento|ubicaci[oó]n)\s+(?:en|a|:)\s*[\p{L}][\p{L}'-]+(?:\s+[\p{L}][\p{L}'-]+){0,3}\s+\d{1,5}\b",
        Options, RegexTimeout);

    private static readonly Regex SignaturePattern = new(
        @"^\s*(?:saludos(?:\s+cordiales)?|atte\.?|atentamente)\s*[,.:;-]?\s*\r?\n\s*[\p{L}][\p{L}'.-]+(?:\s+[\p{L}][\p{L}'.-]+){1,4}\s*$",
        Options, RegexTimeout);

    private static readonly Regex PhoneCandidatePattern = new(
        @"(?<![\w$])(?:\+?\d[\s().-]*){7,15}(?!\w)",
        Options, RegexTimeout);

    public static AiRedactionResult Redact(string? input, int maxLength = 12_000)
    {
        if (maxLength < MinimumSafeLength)
            throw new ArgumentOutOfRangeException(nameof(maxLength));

        var original = input ?? string.Empty;
        var wasTruncated = original.Length > maxLength;
        var scanLength = Math.Min(original.Length, checked(maxLength + 512));
        var text = original[..scanLength];
        var count = 0;
        text = Replace(EmailPattern, text, "[EMAIL REDACTADO]", ref count);
        text = Replace(BankAccountPattern, text, "[CUENTA BANCARIA REDACTADA]", ref count);
        text = Replace(CuitPattern, text, "[CUIT REDACTADO]", ref count);
        text = Replace(DniPattern, text, "[DNI REDACTADO]", ref count);
        text = Replace(AddressPattern, text, "[DOMICILIO REDACTADO]", ref count);
        text = Replace(LabeledNamePattern, text, "[NOMBRE REDACTADO]", ref count);
        text = Replace(LabeledCompanyPattern, text, "[EMPRESA REDACTADA]", ref count);
        text = Replace(ContextualStreetAddressPattern, text, "[DOMICILIO REDACTADO]", ref count);
        text = Replace(SignaturePattern, text, "[FIRMA REDACTADA]", ref count);
        text = PhoneCandidatePattern.Replace(text, match =>
        {
            var digitCount = match.Value.Count(char.IsDigit);
            if (digitCount < 7) return match.Value;
            count++;
            return "[TELÉFONO REDACTADO]";
        });

        if (wasTruncated || text.Length > maxLength)
        {
            wasTruncated = true;
            var contentLength = maxLength - TruncationMarker.Length - 1;
            text = text[..Math.Min(text.Length, contentLength)].TrimEnd() + " " + TruncationMarker;
        }

        return new AiRedactionResult(text, count, wasTruncated);
    }

    private static string Replace(Regex pattern, string input, string replacement, ref int count)
    {
        var matches = pattern.Matches(input).Count;
        if (matches == 0) return input;
        count += matches;
        return pattern.Replace(input, replacement);
    }
}
