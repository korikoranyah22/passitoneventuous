using CursoAgentes.Domain.Incidents;
using Eventuous;

namespace CursoAgentes.Engine.Incidents;

public sealed record IncidentActionReceipt(
    string IdempotencyKey,
    string Action,
    string ExternalId,
    bool WasAlreadyApplied);

public interface IIncidentActionPort
{
    Task<IncidentActionReceipt> ExecuteOnceAsync(
        string idempotencyKey,
        string action,
        CancellationToken ct);
}

/// <summary>
/// Error clasificado por el adapter externo. Sólo esta excepción participa de
/// la política de retry/parking; errores desconocidos permanecen sin capturar
/// para que la suscripción no avance su checkpoint.
/// </summary>
public sealed class IncidentActionPortException(
    string code,
    bool isTransient,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool IsTransient { get; } = isTransient;
}

/// <summary>
/// Adaptador sin red para la demo y los tests. Un adaptador HTTP real debe
/// conservar el mismo contrato de idempotencia usando la clave recibida.
/// </summary>
public sealed class InMemoryIncidentActionPort : IIncidentActionPort
{
    readonly object _gate = new();
    readonly Dictionary<string, IncidentActionReceipt> _receipts = new(StringComparer.Ordinal);

    public int ExecutionCount { get; private set; }

    public Task<IncidentActionReceipt> ExecuteOnceAsync(
        string idempotencyKey,
        string action,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_receipts.TryGetValue(idempotencyKey, out var existing))
            {
                if (existing.Action != action)
                    throw new InvalidOperationException(
                        $"Idempotency key '{idempotencyKey}' was already used for another action.");
                return Task.FromResult(existing with { WasAlreadyApplied = true });
            }

            var prefix = action == IncidentActions.OpenP1 ? "incident" : "review";
            var suffix = idempotencyKey[(idempotencyKey.LastIndexOf(':') + 1)..];
            var receipt = new IncidentActionReceipt(
                idempotencyKey,
                action,
                $"{prefix}-{suffix}",
                WasAlreadyApplied: false);
            _receipts[idempotencyKey] = receipt;
            ExecutionCount++;
            return Task.FromResult(receipt);
        }
    }
}

public sealed record IncidentActionHandlingResult(
    IncidentActionReceipt Receipt,
    IncidentInvestigationState State);

/// <summary>
/// Frontera de efectos para una decisión ya persistida. Está diseñada para
/// recibir el mismo ActionDecided más de una vez: el puerto ejecuta una vez y
/// el comando de confirmación acepta la misma constancia como replay seguro.
/// </summary>
public sealed class IncidentActionHandler(
    IncidentInvestigationCommandService commands,
    IIncidentActionPort actionPort)
{
    public async Task<IncidentActionHandlingResult> HandleAsync(
        IncidentInvestigationEvents.V1.IncidentActionDecided decision,
        CancellationToken ct = default)
    {
        if (decision.Decision.Action is not (
            IncidentActions.OpenP1 or IncidentActions.RequestHumanReview))
        {
            throw new InvalidOperationException(
                $"Unknown incident action '{decision.Decision.Action}'.");
        }

        var idempotencyKey = $"incident-investigation:{decision.InvestigationId}";
        var receipt = await actionPort.ExecuteOnceAsync(
            idempotencyKey,
            decision.Decision.Action,
            ct);
        var confirmation = decision.Decision.Action == IncidentActions.OpenP1
            ? commands.Handle(
                new ConfirmIncidentOpened(
                    decision.InvestigationId,
                    receipt.ExternalId,
                    idempotencyKey),
                ct)
            : commands.Handle(
                new ConfirmHumanReviewRequested(
                    decision.InvestigationId,
                    receipt.ExternalId,
                    idempotencyKey),
                ct);
        var state = await Require(confirmation);
        return new IncidentActionHandlingResult(receipt, state);
    }

    static async Task<IncidentInvestigationState> Require(
        Task<Result<IncidentInvestigationState>> pending)
    {
        var result = await pending;
        if (result.TryGet(out var ok)) return ok.State;
        throw new InvalidOperationException(
            $"Incident action confirmation was rejected: {result.Exception?.Message ?? "unknown error"}");
    }
}

public sealed record IncidentInvestigationInput(
    string InvestigationId,
    string Service,
    IncidentSignal Signal,
    IncidentAnalysis Analysis,
    IncidentCritique Critique);

public sealed record IncidentInvestigationRun(
    IncidentInvestigationState State);

/// <summary>
/// Caso de uso lineal que registra artefactos obtenidos fuera del aggregate.
/// En producción esos artefactos pueden venir de un PipelineRunner o de nodos;
/// esta capa no necesita saber cuál fue la orquestación usada. Termina al
/// persistir ActionDecided; una suscripción durable ejecuta el efecto después.
/// </summary>
public sealed class IncidentInvestigationProcess(
    IncidentInvestigationCommandService commands)
{
    public async Task<IncidentInvestigationRun> RunAsync(
        IncidentInvestigationInput input,
        CancellationToken ct = default)
    {
        await Require(commands.Handle(
            new StartIncidentInvestigation(input.InvestigationId, input.Service), ct));
        await Require(commands.Handle(
            new RecordIncidentSignal(input.InvestigationId, input.Signal), ct));
        await Require(commands.Handle(
            new RecordIncidentAnalysis(input.InvestigationId, input.Analysis), ct));
        await Require(commands.Handle(
            new RecordIncidentCritique(input.InvestigationId, input.Critique), ct));
        var decided = await Require(commands.Handle(
            new DecideIncidentAction(input.InvestigationId), ct));
        return new IncidentInvestigationRun(decided);
    }

    static async Task<IncidentInvestigationState> Require(
        Task<Result<IncidentInvestigationState>> pending)
    {
        var result = await pending;
        if (result.TryGet(out var ok)) return ok.State;
        throw new InvalidOperationException(
            $"Incident command was rejected: {result.Exception?.Message ?? "unknown error"}");
    }
}
