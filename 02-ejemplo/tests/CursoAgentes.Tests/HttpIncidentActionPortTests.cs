using System.Net;
using System.Text;
using System.Text.Json;
using CursoAgentes.Engine.Incidents;
using CursoAgentes.Infrastructure.Incidents;

namespace CursoAgentes.Tests;

public sealed class HttpIncidentActionPortTests
{
    [Fact]
    public async Task ExecuteOnceAsync_SendsIdempotencyContractAndParsesReceipt()
    {
        HttpMethod? capturedMethod = null;
        string? capturedUrl = null;
        string? capturedKey = null;
        string? capturedBody = null;
        var handler = new StubHandler(async (request, ct) =>
        {
            capturedMethod = request.Method;
            capturedUrl = request.RequestUri?.ToString();
            capturedKey = request.Headers.GetValues("Idempotency-Key").Single();
            capturedBody = await request.Content!.ReadAsStringAsync(ct);
            return Json(HttpStatusCode.OK, """
                {"externalId":"incident-42","wasAlreadyApplied":true}
                """);
        });
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://actions.example/"),
        };
        var port = new HttpIncidentActionPort(client, new IncidentActionHttpOptions());

        var receipt = await port.ExecuteOnceAsync(
            "incident-investigation:inv-42",
            "OPEN_INCIDENT:P1",
            CancellationToken.None);

        Assert.Equal(HttpMethod.Post, capturedMethod);
        Assert.Equal("https://actions.example/v1/incident-actions", capturedUrl);
        Assert.Equal("incident-investigation:inv-42", capturedKey);
        using var document = JsonDocument.Parse(capturedBody!);
        Assert.Equal("OPEN_INCIDENT:P1", document.RootElement.GetProperty("action").GetString());
        Assert.Equal("incident-42", receipt.ExternalId);
        Assert.True(receipt.WasAlreadyApplied);
        Assert.Equal("incident-investigation:inv-42", receipt.IdempotencyKey);
    }

    [Fact]
    public async Task ExecuteOnceAsync_ClassifiesHttpFailuresForTheReactionPolicy()
    {
        var cases = new[]
        {
            (HttpStatusCode.RequestTimeout, true),
            ((HttpStatusCode)429, true),
            (HttpStatusCode.ServiceUnavailable, true),
            (HttpStatusCode.BadRequest, false),
            (HttpStatusCode.Conflict, false),
        };

        foreach (var (status, expectedTransient) in cases)
        {
            var port = new HttpIncidentActionPort(
                new HttpClient(new StubHandler((_, _) =>
                    Task.FromResult(new HttpResponseMessage(status))))
                {
                    BaseAddress = new Uri("https://actions.example/"),
                },
                new IncidentActionHttpOptions());

            var failure = await Assert.ThrowsAsync<IncidentActionPortException>(() =>
                port.ExecuteOnceAsync("key", "OPEN_INCIDENT:P1", CancellationToken.None));

            Assert.Equal($"http-{(int)status}", failure.Code);
            Assert.Equal(expectedTransient, failure.IsTransient);
        }
    }

    static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct) => respond(request, ct);
    }
}
