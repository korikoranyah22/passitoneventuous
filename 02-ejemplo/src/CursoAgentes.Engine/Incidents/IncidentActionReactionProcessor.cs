using CursoAgentes.Domain.Incidents;
using Eventuous;

namespace CursoAgentes.Engine.Incidents;

public sealed class IncidentActionReactionOptions
{
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(2);
}

public sealed record IncidentActionReactionResult(
    IncidentInvestigationState State,
    IncidentActionReceipt? Receipt,
    int Attempts,
    bool WasParked,
    bool WasAlreadyResolved);

public sealed class IncidentInvestigationStateReader(IEventStore store)
{
    public async Task<IncidentInvestigationState?> ReadAsync(
        string investigationId,
        CancellationToken ct = default)
    {
        var events = await store.ReadEvents(
            new StreamName($"incident-investigation-{investigationId}"),
            StreamReadPosition.Start,
            int.MaxValue,
            false,
            ct);
        if (events.Length == 0) return null;

        var state = new IncidentInvestigationState();
        foreach (var @event in events) state = Apply(state, @event.Payload);
        return state;
    }

    static IncidentInvestigationState Apply(
        IncidentInvestigationState state,
        object? payload) => payload switch
        {
            IncidentInvestigationEvents.V1.IncidentInvestigationStarted e => state.When(e),
            IncidentInvestigationEvents.V1.IncidentSignalRecorded e => state.When(e),
            IncidentInvestigationEvents.V1.IncidentAnalysisRecorded e => state.When(e),
            IncidentInvestigationEvents.V1.IncidentCritiqueRecorded e => state.When(e),
            IncidentInvestigationEvents.V1.IncidentActionDecided e => state.When(e),
            IncidentInvestigationEvents.V1.IncidentActionParked e => state.When(e),
            IncidentInvestigationEvents.V1.IncidentActionRetryRequested e => state.When(e),
            IncidentInvestigationEvents.V1.IncidentOpened e => state.When(e),
            IncidentInvestigationEvents.V1.IncidentHumanReviewRequested e => state.When(e),
            null => throw new InvalidOperationException("Incident stream contains a null event."),
            _ => throw new InvalidOperationException(
                $"Unexpected event '{payload.GetType().Name}' in incident stream."),
        };
}

/// <summary>
/// Entrada de aplicación para recuperar una acción estacionada. El RequestId
/// pertenece al solicitante y hace idempotente la operación; el aggregate
/// decide si el reintento es válido y conserva su auditoría.
/// </summary>
public sealed class IncidentActionRetryProcess(
    IncidentInvestigationCommandService commands)
{
    public async Task<IncidentInvestigationState> RequestAsync(
        string investigationId,
        string requestId,
        string requestedBy,
        string reason,
        CancellationToken ct = default)
    {
        var result = await commands.Handle(
            new RetryParkedIncidentAction(
                investigationId,
                requestId,
                requestedBy,
                reason),
            ct);
        if (result.TryGet(out var ok)) return ok.State;
        throw new InvalidOperationException(
            $"Incident action retry was rejected: {result.Exception?.Message ?? "unknown error"}");
    }
}

public sealed class IncidentActionParkingHandler(
    IncidentInvestigationCommandService commands)
{
    public async Task<IncidentInvestigationState> ParkAsync(
        IncidentInvestigationEvents.V1.IncidentActionDecided decision,
        IncidentActionPortException failure,
        int attempts,
        CancellationToken ct = default)
    {
        var pending = commands.Handle(
            new ParkIncidentAction(
                decision.InvestigationId,
                new IncidentActionFailure(
                    failure.Code,
                    failure.IsTransient,
                    attempts)),
            ct);
        var result = await pending;
        if (result.TryGet(out var ok)) return ok.State;
        throw new InvalidOperationException(
            $"Incident action parking was rejected: {result.Exception?.Message ?? "unknown error"}");
    }
}

/// <summary>
/// Política operativa para una decisión ya persistida. Reintenta únicamente
/// fallos externos clasificados como transitorios. Al recibir un fallo
/// permanente o agotar el presupuesto, registra un evento ActionParked.
/// </summary>
public sealed class IncidentActionReactionProcessor
{
    readonly IncidentActionHandler _actions;
    readonly IncidentActionParkingHandler _parking;
    readonly IncidentInvestigationStateReader _states;
    readonly IncidentActionReactionOptions _options;

    public IncidentActionReactionProcessor(
        IncidentActionHandler actions,
        IncidentActionParkingHandler parking,
        IncidentInvestigationStateReader states,
        IncidentActionReactionOptions options)
    {
        if (options.MaxAttempts <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxAttempts),
                "MaxAttempts must be positive.");
        if (options.FirstRetryDelay < TimeSpan.Zero || options.MaxRetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options.FirstRetryDelay),
                "Retry delays cannot be negative.");

        _actions = actions;
        _parking = parking;
        _states = states;
        _options = options;
    }

    public async Task<IncidentActionReactionResult> HandleAsync(
        IncidentInvestigationEvents.V1.IncidentActionDecided decision,
        CancellationToken ct = default)
    {
        var current = await _states.ReadAsync(decision.InvestigationId, ct)
            ?? throw new InvalidOperationException(
                $"Incident investigation '{decision.InvestigationId}' was not found.");
        if (current.Status == IncidentInvestigationStatus.Completed)
        {
            return new IncidentActionReactionResult(
                current,
                new IncidentActionReceipt(
                    current.IdempotencyKey!,
                    current.Decision!.Action,
                    current.ExternalId!,
                    WasAlreadyApplied: true),
                Attempts: 0,
                WasParked: false,
                WasAlreadyResolved: true);
        }
        if (current.Status == IncidentInvestigationStatus.ActionParked)
        {
            return new IncidentActionReactionResult(
                current,
                Receipt: null,
                current.Failure?.Attempts ?? 0,
                WasParked: true,
                WasAlreadyResolved: true);
        }
        if (current.Status != IncidentInvestigationStatus.ActionDecided)
        {
            throw new InvalidOperationException(
                $"Incident action delivery expected ActionDecided but was {current.Status}.");
        }

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            try
            {
                var handled = await _actions.HandleAsync(decision, ct);
                return new IncidentActionReactionResult(
                    handled.State,
                    handled.Receipt,
                    attempt,
                    WasParked: false,
                    WasAlreadyResolved: false);
            }
            catch (IncidentActionPortException failure)
            {
                if (failure.IsTransient && attempt < _options.MaxAttempts)
                {
                    var delay = RetryDelay(attempt);
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                    continue;
                }

                var parked = await _parking.ParkAsync(decision, failure, attempt, ct);
                return new IncidentActionReactionResult(
                    parked,
                    Receipt: null,
                    attempt,
                    WasParked: true,
                    WasAlreadyResolved: false);
            }
        }

        throw new InvalidOperationException("Incident action retry loop ended unexpectedly.");
    }

    public Task<IncidentActionReactionResult> HandleAsync(
        IncidentInvestigationEvents.V1.IncidentActionRetryRequested retry,
        CancellationToken ct = default) =>
        HandleAsync(
            new IncidentInvestigationEvents.V1.IncidentActionDecided(
                retry.InvestigationId,
                retry.Decision,
                retry.RequestedAt),
            ct);

    TimeSpan RetryDelay(int failedAttempt)
    {
        if (_options.FirstRetryDelay == TimeSpan.Zero) return TimeSpan.Zero;
        var multiplier = Math.Pow(2, failedAttempt - 1);
        var ticks = Math.Min(
            _options.MaxRetryDelay.Ticks,
            _options.FirstRetryDelay.Ticks * multiplier);
        return TimeSpan.FromTicks((long)ticks);
    }
}
