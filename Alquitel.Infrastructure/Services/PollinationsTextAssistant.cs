using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alquitel.Core.Interfaces;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Implementación de <see cref="IAiTextAssistant"/> sobre la API OpenAI-compatible de
    /// Pollinations.ai — misma key y modelo barato que <see cref="PollinationsOrderParser"/>.
    /// Cualquier fallo devuelve null: los quick-wins de IA son siempre opcionales.
    /// </summary>
    public class PollinationsTextAssistant : IAiTextAssistant
    {
        private const string Endpoint = "https://gen.pollinations.ai/v1/chat/completions";
        private const string DefaultModel = "nova-fast";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

        private readonly string? _apiKey;
        private readonly string _model;

        public PollinationsTextAssistant(string? apiKey, string? model = null)
        {
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
            _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        }

        public bool IsConfigured => _apiKey != null;

        public async Task<string?> CompleteAsync(string systemPrompt, string userText, CancellationToken ct = default)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(userText)) return null;

            try
            {
                var body = new
                {
                    model = _model,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userText },
                    },
                    temperature = 0.2,
                    max_tokens = 1200,
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
                    AppLog.Warning("Pollinations assistant HTTP {Status}: {Body}",
                        (int)response.StatusCode, responseText.Length <= 300 ? responseText : responseText[..300]);
                    return null;
                }

                using var doc = JsonDocument.Parse(responseText);
                return doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Pollinations assistant request failed");
                return null;
            }
        }

        public async Task<string?> CompleteToJsonAsync(string systemPrompt, string userText, CancellationToken ct = default)
        {
            var content = await CompleteAsync(systemPrompt, userText, ct);
            return ExtractJsonObject(content);
        }

        /// <summary>Primer objeto JSON balanceado del texto (misma lógica que el parser de pedidos).</summary>
        internal static string? ExtractJsonObject(string? content)
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
}
