using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CursoAgentes.Engine.Incidents;

namespace CursoAgentes.Infrastructure.Incidents;

public sealed class IncidentActionHttpOptions
{
    public string Provider { get; init; } = "InMemory";
    public string BaseUrl { get; init; } = "http://localhost:5080/";
    public string Path { get; init; } = "v1/incident-actions";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed record IncidentActionHttpRequest(string Action);
public sealed record IncidentActionHttpResponse(
    string ExternalId,
    bool WasAlreadyApplied);

/// <summary>
/// Adapter HTTP cuyo contrato exige idempotencia del proveedor. La misma clave
/// debe devolver el mismo ExternalId incluso si la aplicación fue reiniciada.
/// </summary>
public sealed class HttpIncidentActionPort(
    HttpClient client,
    IncidentActionHttpOptions options) : IIncidentActionPort
{
    public async Task<IncidentActionReceipt> ExecuteOnceAsync(
        string idempotencyKey,
        string action,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Path)
        {
            Content = JsonContent.Create(new IncidentActionHttpRequest(action)),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new IncidentActionPortException(
                "http-timeout",
                isTransient: true,
                "The incident action provider timed out.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new IncidentActionPortException(
                "http-network",
                isTransient: true,
                "The incident action provider could not be reached.",
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                throw new IncidentActionPortException(
                    $"http-{status}",
                    IsTransient(response.StatusCode),
                    $"The incident action provider returned HTTP {status}.");
            }

            IncidentActionHttpResponse? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<IncidentActionHttpResponse>(
                    cancellationToken: ct);
            }
            catch (JsonException exception)
            {
                throw InvalidResponse(exception);
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.ExternalId))
                throw InvalidResponse();

            return new IncidentActionReceipt(
                idempotencyKey,
                action,
                payload.ExternalId.Trim(),
                payload.WasAlreadyApplied);
        }
    }

    static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
        || (int)status == 425
        || (int)status == 429
        || (int)status >= 500;

    static IncidentActionPortException InvalidResponse(Exception? inner = null) => new(
        "http-invalid-response",
        isTransient: false,
        "The incident action provider returned an invalid response.",
        inner);
}
