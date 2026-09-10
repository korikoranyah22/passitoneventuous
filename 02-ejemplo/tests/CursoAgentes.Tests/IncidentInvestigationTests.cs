using CursoAgentes.Domain.Incidents;
using CursoAgentes.Engine.Incidents;
using CursoAgentes.Tests.Testing;
using Eventuous;

namespace CursoAgentes.Tests;

public sealed class IncidentInvestigationStateTests
{
    const string Now = "2026-08-15T12:00:00Z";

    [Fact]
    public void Replay_ReconstructsCompletedInvestigationWithoutExecutingEffects()
    {
        var actionPort = new InMemoryIncidentActionPort();
        var signal = Fixtures.Signal();
        var analysis = Fixtures.Analysis();
        var critique = Fixtures.Critique();
        var decision = IncidentPolicy.Decide(signal, critique);

        var state = new IncidentInvestigationState()
            .When(new IncidentInvestigationEvents.V1.IncidentInvestigationStarted(
                "inv-1", "payments-api", Now))
            .When(new IncidentInvestigationEvents.V1.IncidentSignalRecorded(
                "inv-1", signal, Now))
            .When(new IncidentInvestigationEvents.V1.IncidentAnalysisRecorded(
                "inv-1", analysis, Now))
            .When(new IncidentInvestigationEvents.V1.IncidentCritiqueRecorded(
                "inv-1", critique, Now))
            .When(new IncidentInvestigationEvents.V1.IncidentActionDecided(
                "inv-1", decision, Now))
            .When(new IncidentInvestigationEvents.V1.IncidentOpened(
                "inv-1", "incident-inv-1", "incident-investigation:inv-1", Now));

        Assert.Equal(IncidentInvestigationStatus.Completed, state.Status);
        Assert.Equal(IncidentActions.OpenP1, state.Decision?.Action);
        Assert.Equal("cloud-reasoner", state.Critique?.Route.Model);
        Assert.Equal("incident-inv-1", state.ExternalId);
        Assert.Equal(0, actionPort.ExecutionCount);
    }

    [Fact]
    public void Policy_RequiresObservableThresholdsAndConfirmedCritique()
    {
        var insufficientSignal = Fixtures.Signal(failedChecks: 2, affectedRegions: 2);
        var lowConfidence = Fixtures.Critique(confidence: 0.79);

        Assert.Equal(
            IncidentActions.RequestHumanReview,
            IncidentPolicy.Decide(insufficientSignal, Fixtures.Critique()).Action);
        Assert.Equal(
            IncidentActions.RequestHumanReview,
            IncidentPolicy.Decide(Fixtures.Signal(), lowConfidence).Action);
        Assert.Equal(
            IncidentActions.OpenP1,
            IncidentPolicy.Decide(Fixtures.Signal(), Fixtures.Critique()).Action);
    }
}

public sealed class IncidentInvestigationCommandServiceTests
{
    readonly InMemoryEventStore _store = new();
    readonly IncidentInvestigationCommandService _commands;

    public IncidentInvestigationCommandServiceTests()
    {
        EventTypes.EnsureRegistered();
        _commands = new IncidentInvestigationCommandService(_store);
    }

    [Fact]
    public async Task DecisionProcess_RecordsArtifactsAndStopsBeforeTheExternalEffect()
    {
        var port = new InMemoryIncidentActionPort();
        var process = new IncidentInvestigationProcess(_commands);

        var run = await process.RunAsync(Fixtures.Input("inv-open"));

        Assert.Equal(IncidentInvestigationStatus.ActionDecided, run.State.Status);
        Assert.Equal(IncidentActions.OpenP1, run.State.Decision?.Action);
        Assert.Equal(IncidentPolicy.Version, run.State.Decision?.PolicyVersion);
        Assert.Equal("fast-private-analysis", run.State.Analysis?.Route.Profile);
        Assert.Equal("critical-judge", run.State.Critique?.Route.Profile);
        Assert.Null(run.State.IdempotencyKey);
        Assert.Null(run.State.ExternalId);
        Assert.Equal(0, port.ExecutionCount);
        var events = await _store.ReadEvents(
            new StreamName("incident-investigation-inv-open"),
            StreamReadPosition.Start,
            int.MaxValue,
            fromEnd: false,
            CancellationToken.None);
        Assert.Equal(5, events.Length);
    }

    [Fact]
    public async Task InsufficientSignal_RequestsHumanReviewInsteadOfOpeningP1()
    {
        var process = new IncidentInvestigationProcess(_commands);
        var input = Fixtures.Input("inv-review") with
        {
            Signal = Fixtures.Signal(failedChecks: 2, affectedRegions: 1),
        };

        var run = await process.RunAsync(input);

        Assert.Equal(IncidentActions.RequestHumanReview, run.State.Decision?.Action);
        Assert.Equal(IncidentInvestigationStatus.ActionDecided, run.State.Status);
        Assert.Null(run.State.ExternalId);
    }

    [Fact]
    public async Task PersistedDecision_CanBeHandledAfterTheDecisionProcessStops()
    {
        var process = new IncidentInvestigationProcess(_commands);
        var decided = await process.RunAsync(Fixtures.Input("inv-recover"));
        var port = new InMemoryIncidentActionPort();
        var reaction = new IncidentActionHandler(_commands, port);

        Assert.Equal(IncidentInvestigationStatus.ActionDecided, decided.State.Status);
        Assert.Equal(5, await EventCount("inv-recover"));
        Assert.Equal(0, port.ExecutionCount);

        var handled = await reaction.HandleAsync(DecisionEvent(decided.State));

        Assert.Equal(IncidentInvestigationStatus.Completed, handled.State.Status);
        Assert.Equal(1, port.ExecutionCount);
        Assert.Equal(6, await EventCount("inv-recover"));
    }

    [Fact]
    public async Task FailedEffect_LeavesTheDecisionPendingAndCanBeRetried()
    {
        var process = new IncidentInvestigationProcess(_commands);
        var decided = await process.RunAsync(Fixtures.Input("inv-retry"));
        var port = new FailOnceIncidentActionPort();
        var reaction = new IncidentActionHandler(_commands, port);
        var decision = DecisionEvent(decided.State);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reaction.HandleAsync(decision));
        Assert.Equal(5, await EventCount("inv-retry"));

        var retried = await reaction.HandleAsync(decision);

        Assert.Equal(IncidentInvestigationStatus.Completed, retried.State.Status);
        Assert.Equal(2, port.Attempts);
        Assert.Equal(1, port.SuccessfulExecutions);
        Assert.Equal(6, await EventCount("inv-retry"));
    }

    [Fact]
    public async Task TransientExternalFailures_AreRetriedAndEventuallyConfirmed()
    {
        var decided = await new IncidentInvestigationProcess(_commands)
            .RunAsync(Fixtures.Input("inv-transient"));
        var port = new ScriptedIncidentActionPort(
            Transient("timeout"),
            Transient("http-503"));
        var processor = Processor(port, maxAttempts: 3);

        var result = await processor.HandleAsync(DecisionEvent(decided.State));

        Assert.False(result.WasParked);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, port.Attempts);
        Assert.Equal(1, port.SuccessfulExecutions);
        Assert.Equal(IncidentInvestigationStatus.Completed, result.State.Status);
        Assert.Equal(6, await EventCount("inv-transient"));
    }

    [Fact]
    public async Task PermanentExternalFailure_IsParkedWithoutRetry()
    {
        var decided = await new IncidentInvestigationProcess(_commands)
            .RunAsync(Fixtures.Input("inv-permanent"));
        var port = new ScriptedIncidentActionPort(Permanent("http-400"));
        var processor = Processor(port, maxAttempts: 3);

        var result = await processor.HandleAsync(DecisionEvent(decided.State));

        Assert.True(result.WasParked);
        Assert.Null(result.Receipt);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, port.Attempts);
        Assert.Equal(0, port.SuccessfulExecutions);
        Assert.Equal(IncidentInvestigationStatus.ActionParked, result.State.Status);
        Assert.Equal(new IncidentActionFailure("http-400", false, 1), result.State.Failure);
        Assert.Equal(6, await EventCount("inv-permanent"));

        var recoveredPort = new ScriptedIncidentActionPort();
        var redelivery = await Processor(recoveredPort, maxAttempts: 3)
            .HandleAsync(DecisionEvent(decided.State));

        Assert.True(redelivery.WasAlreadyResolved);
        Assert.True(redelivery.WasParked);
        Assert.Equal(0, recoveredPort.Attempts);
        Assert.Equal(6, await EventCount("inv-permanent"));
    }

    [Fact]
    public async Task ParkedAction_CanBeManuallyRetriedAndCompleted()
    {
        var decided = await new IncidentInvestigationProcess(_commands)
            .RunAsync(Fixtures.Input("inv-manual-recovery"));
        var parked = await Processor(
                new ScriptedIncidentActionPort(Permanent("http-400")),
                maxAttempts: 3)
            .HandleAsync(DecisionEvent(decided.State));
        Assert.Equal(IncidentInvestigationStatus.ActionParked, parked.State.Status);
        Assert.Equal(6, await EventCount("inv-manual-recovery"));

        var retryState = await new IncidentActionRetryProcess(_commands).RequestAsync(
            "inv-manual-recovery",
            "retry-001",
            "course-operator",
            "The provider configuration was repaired.");

        Assert.Equal(IncidentInvestigationStatus.ActionDecided, retryState.Status);
        Assert.Null(retryState.Failure);
        Assert.Equal(1, retryState.ManualRetryCount);
        Assert.Equal("retry-001", retryState.LastRetryRequestId);
        Assert.Equal("course-operator", retryState.LastRetryRequestedBy);
        Assert.Contains("retry-001", retryState.AppliedRetryRequestIds);
        Assert.Equal(7, await EventCount("inv-manual-recovery"));

        var recoveredPort = new ScriptedIncidentActionPort();
        var retryEvent = RetryEvent(retryState);
        var recovered = await Processor(recoveredPort, maxAttempts: 3)
            .HandleAsync(retryEvent);

        Assert.Equal(IncidentInvestigationStatus.Completed, recovered.State.Status);
        Assert.False(recovered.WasAlreadyResolved);
        Assert.Equal(1, recoveredPort.Attempts);
        Assert.Equal(8, await EventCount("inv-manual-recovery"));

        var redeliveryPort = new ScriptedIncidentActionPort();
        var redelivery = await Processor(redeliveryPort, maxAttempts: 3)
            .HandleAsync(retryEvent);
        Assert.True(redelivery.WasAlreadyResolved);
        Assert.Equal(0, redeliveryPort.Attempts);
        Assert.Equal(8, await EventCount("inv-manual-recovery"));
    }

    [Fact]
    public async Task ManualRetryRequestId_IsIdempotentAcrossLaterStates()
    {
        var decided = await new IncidentInvestigationProcess(_commands)
            .RunAsync(Fixtures.Input("inv-retry-idempotency"));
        await Processor(
                new ScriptedIncidentActionPort(Permanent("http-400")),
                maxAttempts: 1)
            .HandleAsync(DecisionEvent(decided.State));
        var retry = new IncidentActionRetryProcess(_commands);

        var first = await retry.RequestAsync(
            "inv-retry-idempotency",
            "operator-request-42",
            "course-operator",
            "Provider recovered.");
        var duplicateBeforeCompletion = await retry.RequestAsync(
            "inv-retry-idempotency",
            "operator-request-42",
            "course-operator",
            "Provider recovered.");

        Assert.Equal(IncidentInvestigationStatus.ActionDecided, duplicateBeforeCompletion.Status);
        Assert.Equal(1, duplicateBeforeCompletion.ManualRetryCount);
        Assert.Equal(7, await EventCount("inv-retry-idempotency"));

        await Processor(new ScriptedIncidentActionPort(), maxAttempts: 1)
            .HandleAsync(RetryEvent(first));
        var duplicateAfterCompletion = await retry.RequestAsync(
            "inv-retry-idempotency",
            "operator-request-42",
            "course-operator",
            "Provider recovered.");

        Assert.Equal(IncidentInvestigationStatus.Completed, duplicateAfterCompletion.Status);
        Assert.Equal(8, await EventCount("inv-retry-idempotency"));

        var conflicting = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            retry.RequestAsync(
                "inv-retry-idempotency",
                "operator-request-43",
                "course-operator",
                "Try it once more."));
        Assert.Contains("expected ActionParked", conflicting.Message);
        Assert.Equal(8, await EventCount("inv-retry-idempotency"));
    }

    [Fact]
    public async Task ExhaustedTransientFailure_IsParkedWithTheAttemptCount()
    {
        var decided = await new IncidentInvestigationProcess(_commands)
            .RunAsync(Fixtures.Input("inv-exhausted"));
        var port = new ScriptedIncidentActionPort(
            Transient("http-503"),
            Transient("http-503"),
            Transient("http-503"));
        var processor = Processor(port, maxAttempts: 3);

        var result = await processor.HandleAsync(DecisionEvent(decided.State));

        Assert.True(result.WasParked);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, port.Attempts);
        Assert.Equal(new IncidentActionFailure("http-503", true, 3), result.State.Failure);
        Assert.Equal(6, await EventCount("inv-exhausted"));
    }

    [Fact]
    public async Task UnexpectedFailure_IsNotParkedAndLeavesTheCheckpointWorkPending()
    {
        var decided = await new IncidentInvestigationProcess(_commands)
            .RunAsync(Fixtures.Input("inv-unexpected"));
        var processor = Processor(new FailOnceIncidentActionPort(), maxAttempts: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.HandleAsync(DecisionEvent(decided.State)));

        Assert.Equal(5, await EventCount("inv-unexpected"));
    }

    [Fact]
    public async Task AnalysisCannotBeRecordedBeforeSignal()
    {
        await Success(_commands.Handle(
            new StartIncidentInvestigation("inv-order", "payments-api"),
            CancellationToken.None));

        var result = await _commands.Handle(
            new RecordIncidentAnalysis("inv-order", Fixtures.Analysis()),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("expected SignalRecorded", result.Exception?.Message);
    }

    [Fact]
    public async Task InvalidAnalysisContractStopsBeforeCritiqueAndDecision()
    {
        await Success(_commands.Handle(
            new StartIncidentInvestigation("inv-invalid", "payments-api"),
            CancellationToken.None));
        await Success(_commands.Handle(
            new RecordIncidentSignal("inv-invalid", Fixtures.Signal()),
            CancellationToken.None));

        var result = await _commands.Handle(
            new RecordIncidentAnalysis(
                "inv-invalid",
                Fixtures.Analysis() with { Evidence = [] }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Evidence required", result.Exception?.Message);
    }

    [Fact]
    public async Task PolicyDoesNotAllowOpeningWhenReviewWasDecided()
    {
        var decided = await PrepareDecision(
            "inv-guard",
            Fixtures.Signal(failedChecks: 2, affectedRegions: 1));
        Assert.Equal(IncidentActions.RequestHumanReview, decided.Decision?.Action);

        var result = await _commands.Handle(
            new ConfirmIncidentOpened(
                "inv-guard",
                "incident-wrong",
                "incident-investigation:inv-guard"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("did not authorize", result.Exception?.Message);
    }

    [Fact]
    public async Task RedeliveredActionDecision_IsSafeAndDoesNotRepeatExternalEffect()
    {
        var decided = await PrepareDecision("inv-redelivery", Fixtures.Signal());
        var port = new InMemoryIncidentActionPort();
        var handler = new IncidentActionHandler(_commands, port);
        var @event = new IncidentInvestigationEvents.V1.IncidentActionDecided(
            "inv-redelivery",
            decided.Decision!,
            "2026-08-15T12:00:00Z");

        var first = await handler.HandleAsync(@event);
        var redelivered = await handler.HandleAsync(@event);

        Assert.Equal(IncidentInvestigationStatus.Completed, first.State.Status);
        Assert.Equal(IncidentInvestigationStatus.Completed, redelivered.State.Status);
        Assert.False(first.Receipt.WasAlreadyApplied);
        Assert.True(redelivered.Receipt.WasAlreadyApplied);
        Assert.Equal(first.Receipt.ExternalId, redelivered.Receipt.ExternalId);
        Assert.Equal(1, port.ExecutionCount);
    }

    [Fact]
    public async Task RepeatingDecisionProcess_FailsWithoutAppendingMoreEvents()
    {
        var process = new IncidentInvestigationProcess(_commands);
        var input = Fixtures.Input("inv-repeat");

        await process.RunAsync(input);
        var repeated = await Assert.ThrowsAsync<InvalidOperationException>(
            () => process.RunAsync(input));

        Assert.Contains("Incident command was rejected", repeated.Message);
        Assert.Equal(5, await EventCount("inv-repeat"));
    }

    async Task<IncidentInvestigationState> PrepareDecision(
        string id,
        IncidentSignal signal)
    {
        await Success(_commands.Handle(
            new StartIncidentInvestigation(id, "payments-api"),
            CancellationToken.None));
        await Success(_commands.Handle(
            new RecordIncidentSignal(id, signal),
            CancellationToken.None));
        await Success(_commands.Handle(
            new RecordIncidentAnalysis(id, Fixtures.Analysis()),
            CancellationToken.None));
        await Success(_commands.Handle(
            new RecordIncidentCritique(id, Fixtures.Critique()),
            CancellationToken.None));
        return await Success(_commands.Handle(
            new DecideIncidentAction(id),
            CancellationToken.None));
    }

    async Task<int> EventCount(string id) =>
        (await _store.ReadEvents(
            new StreamName($"incident-investigation-{id}"),
            StreamReadPosition.Start,
            int.MaxValue,
            fromEnd: false,
            CancellationToken.None)).Length;

    static IncidentInvestigationEvents.V1.IncidentActionDecided DecisionEvent(
        IncidentInvestigationState state) => new(
            state.InvestigationId,
            state.Decision!,
            DateTimeOffset.UtcNow.ToString("O"));

    static IncidentInvestigationEvents.V1.IncidentActionRetryRequested RetryEvent(
        IncidentInvestigationState state) => new(
            state.InvestigationId,
            state.Decision!,
            state.LastRetryRequestId!,
            state.LastRetryRequestedBy!,
            state.LastRetryReason!,
            state.ManualRetryCount,
            DateTimeOffset.UtcNow.ToString("O"));

    IncidentActionReactionProcessor Processor(
        IIncidentActionPort port,
        int maxAttempts) => new(
            new IncidentActionHandler(_commands, port),
            new IncidentActionParkingHandler(_commands),
            new IncidentInvestigationStateReader(_store),
            new IncidentActionReactionOptions
            {
                MaxAttempts = maxAttempts,
                FirstRetryDelay = TimeSpan.Zero,
                MaxRetryDelay = TimeSpan.Zero,
            });

    static IncidentActionPortException Transient(string code) => new(
        code,
        isTransient: true,
        "Simulated transient failure.");

    static IncidentActionPortException Permanent(string code) => new(
        code,
        isTransient: false,
        "Simulated permanent failure.");

    sealed class FailOnceIncidentActionPort : IIncidentActionPort
    {
        readonly InMemoryIncidentActionPort _inner = new();

        public int Attempts { get; private set; }
        public int SuccessfulExecutions => _inner.ExecutionCount;

        public Task<IncidentActionReceipt> ExecuteOnceAsync(
            string idempotencyKey,
            string action,
            CancellationToken ct)
        {
            Attempts++;
            if (Attempts == 1)
                throw new InvalidOperationException("Simulated external outage.");
            return _inner.ExecuteOnceAsync(idempotencyKey, action, ct);
        }
    }

    sealed class ScriptedIncidentActionPort(
        params IncidentActionPortException[] failures) : IIncidentActionPort
    {
        readonly Queue<IncidentActionPortException> _failures = new(failures);
        readonly InMemoryIncidentActionPort _inner = new();

        public int Attempts { get; private set; }
        public int SuccessfulExecutions => _inner.ExecutionCount;

        public Task<IncidentActionReceipt> ExecuteOnceAsync(
            string idempotencyKey,
            string action,
            CancellationToken ct)
        {
            Attempts++;
            if (_failures.TryDequeue(out var failure)) throw failure;
            return _inner.ExecuteOnceAsync(idempotencyKey, action, ct);
        }
    }

    static async Task<IncidentInvestigationState> Success(
        Task<Result<IncidentInvestigationState>> pending)
    {
        var result = await pending;
        Assert.True(result.Success, result.Exception?.Message);
        Assert.True(result.TryGet(out var ok));
        return ok.State;
    }
}

static class Fixtures
{
    const string CollectedAt = "2026-08-15T12:00:00Z";

    public static IncidentRouteAudit AnalystRoute() => new(
        "fast-private-analysis", "local/local-fast", "local", "local-fast", Attempts: 1);

    public static IncidentRouteAudit CriticRoute() => new(
        "critical-judge", "cloud/cloud-reasoner", "cloud", "cloud-reasoner", Attempts: 1);

    public static IncidentSignal Signal(int failedChecks = 6, int affectedRegions = 2) => new(
        "payments-api", TotalChecks: 7, failedChecks, affectedRegions, CollectedAt);

    public static IncidentAnalysis Analysis() => new(
        "Payment checks are failing in two regions.",
        "high",
        ["6 failed checks", "2 affected regions"],
        AnalystRoute());

    public static IncidentCritique Critique(double confidence = 0.92) => new(
        "confirmed",
        confidence,
        [],
        CriticRoute());

    public static IncidentInvestigationInput Input(string id) => new(
        id,
        "payments-api",
        Signal(),
        Analysis(),
        Critique());
}
