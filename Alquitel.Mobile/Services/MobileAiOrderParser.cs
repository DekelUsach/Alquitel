using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Alquitel.Core.Interfaces;

namespace Alquitel.Mobile.Services;

/// <summary>
/// Parser de pedidos con IA vía gen.pollinations.ai (modelo nova-fast, reintento con
/// openai-fast). Port del PollinationsOrderParser del desktop sin la dependencia de
/// Serilog (Infrastructure es net8.0-windows). Ante cualquier fallo devuelve null y
/// el llamador cae al ProductMatcher local.
/// </summary>
public class MobileAiOrderParser : IAiOrderParser
{
    private const string Endpoint = "https://gen.pollinations.ai/v1/chat/completions";
    private const string DefaultModel = "nova-fast";
    private const string RetryModel = "openai-fast";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AppConfig.PollinationsApiKey);

    public async Task<AiOrderParseResult?> ParseOrderAsync(
        string customerText,
        IReadOnlyList<AiCatalogProduct> catalog,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(customerText) || catalog.Count == 0)
            return null;

        var result = await TryParseWithModelAsync(DefaultModel, customerText, catalog, cancellationToken);
        result ??= await TryParseWithModelAsync(RetryModel, customerText, catalog, cancellationToken);
        return result;
    }

    private static async Task<AiOrderParseResult?> TryParseWithModelAsync(
        string model, string customerText, IReadOnlyList<AiCatalogProduct> catalog, CancellationToken ct)
    {
        try
        {
            var body = new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = BuildSystemPrompt(catalog) },
                    new { role = "user", content = customerText },
                },
                response_format = new { type = "json_object" },
                temperature = 0.1,
                max_tokens = 2000,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AppConfig.PollinationsApiKey);

            using var response = await Http.SendAsync(request, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"Pollinations {model} HTTP {(int)response.StatusCode}");
                return null;
            }

            using var doc = JsonDocument.Parse(responseText);
            string? content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return ParseModelJson(content, catalog.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Pollinations {model} failed: {ex.Message}");
            return null;
        }
    }

    private static string BuildSystemPrompt(IReadOnlyList<AiCatalogProduct> catalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sos el asistente de carga de pedidos de una empresa argentina de alquiler de equipamiento audiovisual.");
        sb.AppendLine("Recibís el texto libre de un cliente (mail o WhatsApp) y debés mapearlo contra el CATÁLOGO numerado de abajo.");
        sb.AppendLine("Respondé ÚNICAMENTE con un JSON válido, sin markdown, sin explicaciones, con exactamente esta forma:");
        sb.AppendLine("""{"items":[{"ref":0,"cantidad":1,"medida":null}],"dias":null,"no_encontrados":[]}""");
        sb.AppendLine("Reglas:");
        sb.AppendLine("- \"ref\" es el número de un producto del CATÁLOGO. Nunca inventes refs ni productos.");
        sb.AppendLine("- Elegí el producto MÁS específico que coincida con lo pedido. No agregues nada que el cliente no pidió.");
        sb.AppendLine("- \"cantidad\": entero >= 1; si el cliente no la dice, usá 1.");
        sb.AppendLine("- \"medida\": string solo si el cliente pide dimensiones (ej: \"8 x 3\"); si no, null.");
        sb.AppendLine("- \"dias\": entero solo si el texto menciona la duración en días del alquiler; si no, null.");
        sb.AppendLine("- Si piden algo que no matchea ningún producto del catálogo, agregá una descripción corta en \"no_encontrados\".");
        sb.AppendLine("- El CATÁLOGO puede ser una preselección parcial del inventario: si algo pedido no figura, va a \"no_encontrados\"; nunca lo fuerces a un ref que no corresponde.");
        sb.AppendLine();
        sb.AppendLine("CATÁLOGO (ref | descripción | categoría):");
        foreach (var p in catalog)
            sb.AppendLine($"{p.Ref} | {p.Description} | {p.Category}");
        return sb.ToString();
    }

    private sealed class ModelResponse
    {
        [JsonPropertyName("items")] public List<ModelItem>? Items { get; set; }
        [JsonPropertyName("dias")] public int? Dias { get; set; }
        [JsonPropertyName("no_encontrados")] public List<string>? NoEncontrados { get; set; }
    }

    private sealed class ModelItem
    {
        [JsonPropertyName("ref")] public int Ref { get; set; } = -1;
        [JsonPropertyName("cantidad")] public int Cantidad { get; set; } = 1;
        [JsonPropertyName("medida")] public string? Medida { get; set; }
    }

    private static AiOrderParseResult? ParseModelJson(string? content, int catalogCount)
    {
        var json = ExtractJsonObject(content);
        if (json == null) return null;

        ModelResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ModelResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
            });
        }
        catch (JsonException)
        {
            return null;
        }
        if (parsed == null) return null;

        var items = (parsed.Items ?? new List<ModelItem>())
            .Where(i => i.Ref >= 0 && i.Ref < catalogCount)
            .Select(i => new AiParsedItem(
                i.Ref,
                Math.Clamp(i.Cantidad, 1, 999),
                string.IsNullOrWhiteSpace(i.Medida) ? null : i.Medida.Trim()))
            .ToList();

        var unmatched = (parsed.NoEncontrados ?? new List<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();

        if (items.Count == 0 && unmatched.Count == 0) return null;

        int? days = parsed.Dias is >= 1 and <= 365 ? parsed.Dias : null;
        return new AiOrderParseResult(items, days, unmatched);
    }

    private static string? ExtractJsonObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        int start = content.IndexOf('{');
        if (start < 0) return null;

        int depth = 0;
        bool inString = false;
        for (int i = start; i < content.Length; i++)
        {
            char c = content[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
                return content.Substring(start, i - start + 1);
        }
        return null;
    }
}
