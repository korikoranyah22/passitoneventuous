using System.Net;
using System.Text;
using System.Text.Json;
using CursoAgentes.Engine.Llm;
using CursoAgentes.Infrastructure.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CursoAgentes.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Tests del gateway LLM real (OpenAI-compatible) SIN red: un HttpMessageHandler
// falso que responde como si fuera el provider. Así verificamos que el gateway
// arma el request correcto (endpoint, headers, body) y parsea la respuesta.
// ─────────────────────────────────────────────────────────────────────────────

public class OpenAiCompatibleGatewayTests
{
    [Fact]
    public async Task CompleteAsync_SendsChatCompletionsRequest_AndParsesResponse()
    {
        string? capturedUrl = null;
        string? capturedBody = null;
        string? capturedAuth = null;

        var handler = new StubHandler(async (request, ct) =>
        {
            capturedUrl = request.RequestUri?.ToString();
            capturedAuth = request.Headers.Authorization?.ToString();
            capturedBody = await request.Content!.ReadAsStringAsync(ct);

            var json = """
                {
                  "choices": [
                    { "message": { "role": "assistant", "content": "  respuesta del modelo  " },
                      "finish_reason": "stop" }
                  ],
                  "usage": { "prompt_tokens": 25, "completion_tokens": 7 }
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var options = Options.Create(new LlmGatewayOptions
        {
            BaseUrl = "https://api.ejemplo.dev/v1",
            ApiKey = "clave-secreta",
            Model = "modelo-de-prueba"
        });

        var gateway = new OpenAiCompatibleGateway(
            new HttpClient(handler), options, NullLogger<OpenAiCompatibleGateway>.Instance);

        var response = await gateway.CompleteAsync(
            new LlmRequest(
                "system prompt",
                new[] { new LlmMessage("user", "hola") },
                Temperature: 0.3f,
                MaxTokens: 128),
            CancellationToken.None);

        // Request bien armado
        Assert.Equal("https://api.ejemplo.dev/v1/chat/completions", capturedUrl);
        Assert.Equal("Bearer clave-secreta", capturedAuth);
        Assert.NotNull(capturedBody);

        using var bodyDoc = JsonDocument.Parse(capturedBody!);
        var root = bodyDoc.RootElement;
        Assert.Equal("modelo-de-prueba", root.GetProperty("model").GetString());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("hola", root.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(0.3, root.GetProperty("temperature").GetDouble());

        // Respuesta bien parseada (trim incluido)
        Assert.Equal("respuesta del modelo", response.Text);
        Assert.Equal(25, response.InputTokens);
        Assert.Equal(7, response.OutputTokens);
        Assert.Equal("stop", response.FinishReason);
    }

    [Fact]
    public async Task CompleteAsync_OnProviderError_ThrowsHttpRequestException()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\": \"boom\"}")
            }));

        var gateway = new OpenAiCompatibleGateway(
            new HttpClient(handler),
            Options.Create(new LlmGatewayOptions { BaseUrl = "http://localhost:9999/v1" }),
            NullLogger<OpenAiCompatibleGateway>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            gateway.CompleteAsync(
                new LlmRequest("sys", new[] { new LlmMessage("user", "hola") }),
                CancellationToken.None));
    }

    /// <summary>HttpMessageHandler de prueba: responde con el delegado dado.</summary>
    sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => responder(request, ct);
    }
}
