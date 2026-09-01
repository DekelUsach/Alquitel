using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Alquitel.Core.Interfaces;
using Alquitel.Core.Privacy;

namespace Alquitel.Infrastructure.Services;

/// <summary>
/// Asistente opcional sobre Pollinations. Solo queda habilitado con API key y
/// consentimiento local explícito; redacta identificadores antes de cada envío.
/// </summary>
public class PollinationsTextAssistant : IAiTextAssistant
{
    private const string Endpoint = "https://gen.pollinations.ai/v1/chat/completions";
    private const string DefaultModel = "nova-fast";
    private const int MaxPlainTextResultLength = 8_000;
    private const int MaxJsonResultLength = 32_000;

    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly string? _apiKey;
    private readonly string _model;
    private readonly Func<bool> _externalProcessingEnabled;
    private readonly HttpClient _http;

    public PollinationsTextAssistant(string? apiKey, string? model = null)
        : this(apiKey, model, () => false, SharedHttp)
    {
    }

    public PollinationsTextAssistant(
        string? apiKey,
        string? model,
        Func<bool> externalProcessingEnabled,
        HttpClient? httpClient = null)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        _externalProcessingEnabled = externalProcessingEnabled
            ?? throw new ArgumentNullException(nameof(externalProcessingEnabled));
        _http = httpClient ?? SharedHttp;
    }

    public bool IsConfigured => _apiKey != null && _externalProcessingEnabled();

    public async Task<string?> CompleteAsync(
        string systemPrompt,
        string userText,
        CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(userText)) return null;

        var redacted = AiPrivacyRedactor.Redact(
            userText, AiHttpSafety.MaxCustomerTextLength).Text;
        var safeSystemPrompt = AiHttpSafety.HardenSystemPrompt(systemPrompt);
        var wrappedUserText = AiHttpSafety.WrapUntrusted(redacted, "USER_TEXT");

        try
        {
            var body = new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = safeSystemPrompt },
                    new { role = "user", content = wrappedUserText },
                },
                temperature = 0.2,
                max_tokens = 1200,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            if (!IsConfigured) return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AiHttpSafety.RequestTimeout);
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var responseText = await AiHttpSafety.ReadLimitedTextAsync(
                response.Content, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warning("Pollinations assistant HTTP {Status}",
                    (int)response.StatusCode);
                return null;
            }

            using var document = JsonDocument.Parse(
                responseText, new JsonDocumentOptions { MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1 ||
                !choices[0].TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String)
                return null;

            var value = content.GetString();
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > MaxPlainTextResultLength ||
                value.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
                return null;
            return value;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            AppLog.Warning("Pollinations assistant timeout for model {Model}", _model);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warning("Pollinations assistant request failed ({ErrorType})",
                ex.GetType().Name);
            return null;
        }
    }

    public async Task<string?> CompleteToJsonAsync(
        string systemPrompt,
        string userText,
        CancellationToken ct = default)
    {
        var content = await CompleteAsync(systemPrompt, userText, ct);
        var json = ExtractJsonObject(content);
        if (json == null || json.Length > MaxJsonResultLength) return null;
        try
        {
            using var document = JsonDocument.Parse(
                json, new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Serialize(document.RootElement)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Primer objeto JSON balanceado, sin aceptar texto ilimitado.</summary>
    internal static string? ExtractJsonObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > MaxJsonResultLength)
            return null;

        var start = content.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        for (var i = start; i < content.Length; i++)
        {
            var c = content[i];
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
