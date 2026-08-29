using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Alquitel.Core.Helpers;
using Alquitel.Core.Interfaces;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Analiza pedidos en texto libre contra el catálogo usando la API OpenAI-compatible
    /// de Pollinations.ai. Modelo primario: "nova-fast" (Amazon Nova Micro, el más barato
    /// de la plataforma a julio 2026). Si la respuesta no trae JSON interpretable se
    /// reintenta una vez con "openai-fast" (GPT-5 Nano, casi el mismo costo y con
    /// response_format nativo). Ante cualquier fallo devuelve null y el llamador cae al
    /// motor de coincidencia local.
    /// </summary>
    public class PollinationsOrderParser : IAiOrderParser
    {
        private const string Endpoint = "https://gen.pollinations.ai/v1/chat/completions";
        private const string DefaultModel = "nova-fast";
        private const string RetryModel = "openai-fast";

        // HttpClient compartido: la app es un singleton de larga vida y así se evita
        // el socket exhaustion de crear clientes por request. PooledConnectionLifetime
        // acota cuánto vive cada conexión para que un cambio de DNS del proveedor no
        // deje la app pegada a una IP muerta hasta que se reinicie.
        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        { Timeout = TimeSpan.FromSeconds(45) };

        private readonly string? _apiKey;
        private readonly string _model;

        public PollinationsOrderParser(string? apiKey, string? model = null)
        {
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
            _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        }

        public bool IsConfigured => _apiKey != null;

        public async Task<AiOrderParseResult?> ParseOrderAsync(
            string customerText,
            IReadOnlyList<AiCatalogProduct> catalog,
            CancellationToken cancellationToken = default)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(customerText) || catalog.Count == 0)
                return null;

            var result = await TryModelWithRetriesAsync(_model, customerText, catalog, cancellationToken);
            if (result != null) return result;

            if (!string.Equals(_model, RetryModel, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warning("Pollinations: {Model} no devolvió JSON usable — reintento con {Retry}", _model, RetryModel);
                result = await TryModelWithRetriesAsync(RetryModel, customerText, catalog, cancellationToken);
            }
            return result;
        }

        /// <summary>
        /// Reintenta el mismo modelo ante fallos transitorios (429 de rate limit, 5xx del
        /// gateway, timeouts de red) con backoff exponencial. Antes, un único 429 —lo más
        /// común en un plan gratuito— tiraba directamente al motor local aunque el
        /// servicio hubiera respondido bien un segundo después.
        /// </summary>
        private async Task<AiOrderParseResult?> TryModelWithRetriesAsync(
            string model, string customerText, IReadOnlyList<AiCatalogProduct> catalog, CancellationToken ct)
        {
            for (int attempt = 1; attempt <= HttpRetryPolicy.MaxAttempts; attempt++)
            {
                var (result, transient, retryAfter) =
                    await TryParseWithModelAsync(model, customerText, catalog, ct);

                if (result != null) return result;
                if (!transient || attempt == HttpRetryPolicy.MaxAttempts) return null;

                var delay = HttpRetryPolicy.DelayFor(attempt, retryAfter);
                AppLog.Information("Pollinations {Model}: fallo transitorio, reintento {Attempt}/{Max} en {Delay}ms",
                    model, attempt, HttpRetryPolicy.MaxAttempts - 1, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
            return null;
        }

        /// <summary>
        /// Un intento contra un modelo. Además del resultado informa si el fallo es
        /// transitorio (para que el llamador decida reintentar) y el Retry-After que
        /// haya mandado el servidor.
        /// </summary>
        private async Task<(AiOrderParseResult? Result, bool Transient, TimeSpan? RetryAfter)> TryParseWithModelAsync(
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
                    // Los modelos nano/reasoning pueden gastar reasoning_tokens dentro de
                    // max_tokens: un tope chico corta la respuesta con contenido vacío.
                    max_tokens = 2000,
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

                using var response = await Http.SendAsync(request, ct);
                var responseText = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    int status = (int)response.StatusCode;
                    AppLog.Warning("Pollinations {Model} HTTP {Status}: {Body}",
                        model, status, Truncate(responseText, 400));
                    return (null, HttpRetryPolicy.IsTransient(status), response.Headers.RetryAfter?.Delta);
                }

                using var doc = JsonDocument.Parse(responseText);
                string? content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                // Una respuesta 200 con JSON ilegible no mejora reintentando el mismo
                // modelo: lo que corresponde es cambiar de modelo (lo hace el llamador).
                return (ParseModelJson(content, catalog.Count), false, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                // Sin cancelación pedida, un TaskCanceledException es el timeout del
                // HttpClient: transitorio, vale la pena reintentar.
                AppLog.Warning(ex, "Pollinations timeout for model {Model}", model);
                return (null, true, null);
            }
            catch (HttpRequestException ex)
            {
                AppLog.Warning(ex, "Pollinations network error for model {Model}", model);
                return (null, true, null);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Pollinations request failed for model {Model}", model);
                return (null, false, null);
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

        // ── Interpretación de la respuesta del modelo ────────────────────

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
            catch (JsonException ex)
            {
                AppLog.Warning(ex, "Pollinations: JSON de respuesta inválido: {Json}", Truncate(json, 400));
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

        /// <summary>
        /// Aísla el primer objeto JSON balanceado del texto: los modelos a veces envuelven
        /// la respuesta en ```json ... ``` o le agregan una frase antes/después.
        /// </summary>
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

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";
    }
}
