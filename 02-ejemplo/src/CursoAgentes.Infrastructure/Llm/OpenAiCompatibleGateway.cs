using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CursoAgentes.Engine.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CursoAgentes.Infrastructure.Llm;

// ─────────────────────────────────────────────────────────────────────────────
// Gateway LLM REAL: habla el protocolo /chat/completions de OpenAI (el estándar
// de facto; DeepSeek, Ollama, Groq, vLLM y muchos más lo implementan).
//
// Es un "adaptador" del puerto ILlmGateway: el workflow no sabe nada de HTTP ni
// de proveedores; solo le importa que CompleteAsync le devuelva un texto.
// Cambiar de proveedor = cambiar config, no código.
//
// Nota: en producción real agregarías reintentos con backoff (Polly), timeouts
// por proveedor y telemetría — mirá el patrón en proyectos grandes. Acá nos
// quedamos con lo mínimo para que el curso se entienda.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class OpenAiCompatibleGateway : ILlmGateway
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly LlmGatewayOptions _options;
    private readonly ILogger<OpenAiCompatibleGateway> _logger;

    public OpenAiCompatibleGateway(
        HttpClient http,
        IOptions<LlmGatewayOptions> options,
        ILogger<OpenAiCompatibleGateway> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/chat/completions";

        var body = new
        {
            model = _options.Model,
            messages = BuildMessages(request),
            temperature = request.Temperature ?? 0.7f,
            max_tokens = request.MaxTokens ?? 2048,
            stream = false
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        _logger.LogInformation("[Llm] POST {Url} model={Model} messages={N}",
            url, _options.Model, request.Messages.Count);

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[Llm] HTTP {(int)Response.StatusCode} — {Body}",
                (int)response.StatusCode,
                responseBody.Length > 500 ? responseBody[..500] + "…" : responseBody);
            throw new HttpRequestException(
                $"LLM provider responded {(int)response.StatusCode}: {responseBody}",
                null, response.StatusCode);
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        // choices[0].message.content
        var text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

        // usage.prompt_tokens / usage.completion_tokens (algunos providers los omiten)
        var input = root.TryGetProperty("usage", out var usage) &&
                    usage.TryGetProperty("prompt_tokens", out var pt)
            ? pt.GetInt32()
            : 0;
        var output = root.TryGetProperty("usage", out usage) &&
                     usage.TryGetProperty("completion_tokens", out var cto)
            ? cto.GetInt32()
            : 0;

        var finish = root.TryGetProperty("choices", out var choices) &&
                     choices[0].TryGetProperty("finish_reason", out var fr) &&
                     fr.ValueKind == JsonValueKind.String
            ? fr.GetString()!
            : "stop";

        return new LlmResponse(text.Trim(), input, output, finish);
    }

    private static List<object> BuildMessages(LlmRequest request)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new { role = "system", content = request.SystemPrompt });
        foreach (var msg in request.Messages)
            messages.Add(new { role = msg.Role, content = msg.Content });
        return messages;
    }
}
