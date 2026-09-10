using Eventuous;

namespace CursoAgentes.Domain.Incidents;

// El aggregate persiste hechos y protege transiciones. No recolecta métricas,
// no llama LLMs y no ejecuta HTTP: esos efectos ocurren antes o después de sus
// comandos, en la capa de aplicación.

public enum IncidentInvestigationStatus
{
    None,
    Started,
    SignalRecorded,
    AnalysisRecorded,
    CritiqueRecorded,
    ActionDecided,
    ActionParked,
    Completed,
}

public static class IncidentActions
{
    public const string OpenP1 = "OPEN_INCIDENT:P1";
    public const string RequestHumanReview = "REQUEST_HUMAN_REVIEW";
}

public sealed record IncidentSignal(
    string Service,
    int TotalChecks,
    int FailedChecks,
    int AffectedRegions,
    string CollectedAt);

public sealed record IncidentRouteAudit(
    string Profile,
    string RouteId,
    string Provider,
    string Model,
    int Attempts);

public sealed record IncidentAnalysis(
    string Summary,
    string Severity,
    string[] Evidence,
    IncidentRouteAudit Route);

public sealed record IncidentCritique(
    string Verdict,
    double Confidence,
    string[] Issues,
    IncidentRouteAudit Route);

public sealed record IncidentDecision(
    string Action,
    string PolicyVersion,
    string[] Reasons);

public sealed record IncidentActionFailure(
    string Code,
    bool WasTransient,
    int Attempts);

public static class IncidentPolicy
{
    public const string Version = "incident-policy/v1";

    public static IncidentDecision Decide(IncidentSignal signal, IncidentCritique critique)
    {
        var reasons = new List<string>();
        if (signal.FailedChecks >= 3) reasons.Add("at least three checks failed");
        if (signal.AffectedRegions >= 2) reasons.Add("at least two regions are affected");
        if (string.Equals(critique.Verdict, "confirmed", StringComparison.OrdinalIgnoreCase))
            reasons.Add("independent critique confirmed the diagnosis");
        if (critique.Confidence >= 0.80) reasons.Add("critic confidence is at least 0.80");

        return new IncidentDecision(
            reasons.Count == 4 ? IncidentActions.OpenP1 : IncidentActions.RequestHumanReview,
            Version,
            [.. reasons]);
    }
}

public static class IncidentInvestigationEvents
{
    public static class V1
    {
        [EventType("V1.IncidentInvestigationStarted")]
        public sealed record IncidentInvestigationStarted(
            string InvestigationId,
            string Service,
            string StartedAt);

        [EventType("V1.IncidentSignalRecorded")]
        public sealed record IncidentSignalRecorded(
            string InvestigationId,
            IncidentSignal Signal,
            string RecordedAt);

        [EventType("V1.IncidentAnalysisRecorded")]
        public sealed record IncidentAnalysisRecorded(
            string InvestigationId,
            IncidentAnalysis Analysis,
            string RecordedAt);

        [EventType("V1.IncidentCritiqueRecorded")]
        public sealed record IncidentCritiqueRecorded(
            string InvestigationId,
            IncidentCritique Critique,
            string RecordedAt);

        [EventType("V1.IncidentActionDecided")]
        public sealed record IncidentActionDecided(
            string InvestigationId,
            IncidentDecision Decision,
            string DecidedAt);

        [EventType("V1.IncidentActionParked")]
        public sealed record IncidentActionParked(
            string InvestigationId,
            IncidentActionFailure Failure,
            string ParkedAt);

        [EventType("V1.IncidentActionRetryRequested")]
        public sealed record IncidentActionRetryRequested(
            string InvestigationId,
            IncidentDecision Decision,
            string RequestId,
            string RequestedBy,
            string Reason,
            int RetryNumber,
            string RequestedAt);

        [EventType("V1.IncidentOpened")]
        public sealed record IncidentOpened(
            string InvestigationId,
            string ExternalId,
            string IdempotencyKey,
            string OpenedAt);

        [EventType("V1.IncidentHumanReviewRequested")]
        public sealed record IncidentHumanReviewRequested(
            string InvestigationId,
            string ExternalId,
            string IdempotencyKey,
            string RequestedAt);
    }
}

public record IncidentInvestigationState : State<IncidentInvestigationState>
{
    public string InvestigationId { get; init; } = "";
    public string Service { get; init; } = "";
    public IncidentInvestigationStatus Status { get; init; } = IncidentInvestigationStatus.None;
    public IncidentSignal? Signal { get; init; }
    public IncidentAnalysis? Analysis { get; init; }
    public IncidentCritique? Critique { get; init; }
    public IncidentDecision? Decision { get; init; }
    public IncidentActionFailure? Failure { get; init; }
    public int ManualRetryCount { get; init; }
    public string? LastRetryRequestId { get; init; }
    public string? LastRetryRequestedBy { get; init; }
    public string? LastRetryReason { get; init; }
    public string[] AppliedRetryRequestIds { get; init; } = [];
    public string? ExternalId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string StartedAt { get; init; } = "";
    public string? CompletedAt { get; init; }

    public IncidentInvestigationState()
    {
        On<IncidentInvestigationEvents.V1.IncidentInvestigationStarted>((state, e) => state with
        {
            InvestigationId = e.InvestigationId,
            Service = e.Service,
            Status = IncidentInvestigationStatus.Started,
            StartedAt = e.StartedAt,
        });
        On<IncidentInvestigationEvents.V1.IncidentSignalRecorded>((state, e) => state with
        {
            Signal = e.Signal,
            Status = IncidentInvestigationStatus.SignalRecorded,
        });
        On<IncidentInvestigationEvents.V1.IncidentAnalysisRecorded>((state, e) => state with
        {
            Analysis = e.Analysis,
            Status = IncidentInvestigationStatus.AnalysisRecorded,
        });
        On<IncidentInvestigationEvents.V1.IncidentCritiqueRecorded>((state, e) => state with
        {
            Critique = e.Critique,
            Status = IncidentInvestigationStatus.CritiqueRecorded,
        });
        On<IncidentInvestigationEvents.V1.IncidentActionDecided>((state, e) => state with
        {
            Decision = e.Decision,
            Status = IncidentInvestigationStatus.ActionDecided,
        });
        On<IncidentInvestigationEvents.V1.IncidentActionParked>((state, e) => state with
        {
            Failure = e.Failure,
            Status = IncidentInvestigationStatus.ActionParked,
        });
        On<IncidentInvestigationEvents.V1.IncidentActionRetryRequested>((state, e) => state with
        {
            Decision = e.Decision,
            Failure = null,
            ManualRetryCount = e.RetryNumber,
            LastRetryRequestId = e.RequestId,
            LastRetryRequestedBy = e.RequestedBy,
            LastRetryReason = e.Reason,
            AppliedRetryRequestIds = state.AppliedRetryRequestIds.Contains(
                e.RequestId,
                StringComparer.Ordinal)
                    ? state.AppliedRetryRequestIds
                    : [.. state.AppliedRetryRequestIds, e.RequestId],
            Status = IncidentInvestigationStatus.ActionDecided,
        });
        On<IncidentInvestigationEvents.V1.IncidentOpened>((state, e) => state with
        {
            ExternalId = e.ExternalId,
            IdempotencyKey = e.IdempotencyKey,
            Status = IncidentInvestigationStatus.Completed,
            CompletedAt = e.OpenedAt,
        });
        On<IncidentInvestigationEvents.V1.IncidentHumanReviewRequested>((state, e) => state with
        {
            ExternalId = e.ExternalId,
            IdempotencyKey = e.IdempotencyKey,
            Status = IncidentInvestigationStatus.Completed,
            CompletedAt = e.RequestedAt,
        });
    }
}

public sealed record StartIncidentInvestigation(string InvestigationId, string Service);
public sealed record RecordIncidentSignal(string InvestigationId, IncidentSignal Signal);
public sealed record RecordIncidentAnalysis(string InvestigationId, IncidentAnalysis Analysis);
public sealed record RecordIncidentCritique(string InvestigationId, IncidentCritique Critique);
public sealed record DecideIncidentAction(string InvestigationId);
public sealed record ParkIncidentAction(
    string InvestigationId,
    IncidentActionFailure Failure);
public sealed record RetryParkedIncidentAction(
    string InvestigationId,
    string RequestId,
    string RequestedBy,
    string Reason);
public sealed record ConfirmIncidentOpened(
    string InvestigationId,
    string ExternalId,
    string IdempotencyKey);
public sealed record ConfirmHumanReviewRequested(
    string InvestigationId,
    string ExternalId,
    string IdempotencyKey);

public sealed class IncidentInvestigationCommandService : CommandService<IncidentInvestigationState>
{
    public IncidentInvestigationCommandService(IEventStore store) : base(store)
    {
        On<StartIncidentInvestigation>().InState(ExpectedState.New)
            .GetStream(command => Stream(command.InvestigationId)).Act(Start);
        On<RecordIncidentSignal>().InState(ExpectedState.Existing)
            .GetStream(command => Stream(command.InvestigationId)).Act(RecordSignal);
        On<RecordIncidentAnalysis>().InState(ExpectedState.Existing)
            .GetStream(command => Stream(command.InvestigationId)).Act(RecordAnalysis);
        On<RecordIncidentCritique>().InState(ExpectedState.Existing)
            .GetStream(command => Stream(command.InvestigationId)).Act(RecordCritique);
        On<DecideIncidentAction>().InState(ExpectedState.Existing)
            .GetStream(command => Stream(command.InvestigationId)).Act(Decide);
        On<ParkIncidentAction>().InState(ExpectedState.Existing)
            .GetStream(command => Stream(command.InvestigationId)).Act(Park);
        On<RetryParkedIncidentAction>().InState(ExpectedState.Existing)
            .GetStream(command => Stream(command.InvestigationId)).Act(RetryParked);
        On<ConfirmIncidentOpened>().InState(ExpectedState.Existing)
            .GetStream(command => Stream(command.InvestigationId)).Act(ConfirmOpened);
        On<ConfirmHumanReviewRequested>().InState(ExpectedState.Existing)
            .GetStream(command => Stream(command.InvestigationId)).Act(ConfirmReview);
    }

    static IEnumerable<object> Start(StartIncidentInvestigation command)
    {
        Required(command.InvestigationId, "StartIncidentInvestigation: InvestigationId required.");
        Required(command.Service, "StartIncidentInvestigation: Service required.");
        yield return new IncidentInvestigationEvents.V1.IncidentInvestigationStarted(
            command.InvestigationId.Trim(),
            command.Service.Trim(),
            Now);
    }

    static IEnumerable<object> RecordSignal(
        IncidentInvestigationState state,
        object[] _,
        RecordIncidentSignal command)
    {
        RequireStatus(state, IncidentInvestigationStatus.Started, nameof(RecordIncidentSignal));
        var signal = Normalize(command.Signal);
        if (!string.Equals(signal.Service, state.Service, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("RecordIncidentSignal: signal service must match the investigation service.");
        yield return new IncidentInvestigationEvents.V1.IncidentSignalRecorded(
            command.InvestigationId,
            signal,
            Now);
    }

    static IEnumerable<object> RecordAnalysis(
        IncidentInvestigationState state,
        object[] _,
        RecordIncidentAnalysis command)
    {
        RequireStatus(state, IncidentInvestigationStatus.SignalRecorded, nameof(RecordIncidentAnalysis));
        yield return new IncidentInvestigationEvents.V1.IncidentAnalysisRecorded(
            command.InvestigationId,
            Normalize(command.Analysis),
            Now);
    }

    static IEnumerable<object> RecordCritique(
        IncidentInvestigationState state,
        object[] _,
        RecordIncidentCritique command)
    {
        RequireStatus(state, IncidentInvestigationStatus.AnalysisRecorded, nameof(RecordIncidentCritique));
        yield return new IncidentInvestigationEvents.V1.IncidentCritiqueRecorded(
            command.InvestigationId,
            Normalize(command.Critique),
            Now);
    }

    static IEnumerable<object> Decide(
        IncidentInvestigationState state,
        object[] _,
        DecideIncidentAction command)
    {
        RequireStatus(state, IncidentInvestigationStatus.CritiqueRecorded, nameof(DecideIncidentAction));
        yield return new IncidentInvestigationEvents.V1.IncidentActionDecided(
            command.InvestigationId,
            IncidentPolicy.Decide(state.Signal!, state.Critique!),
            Now);
    }

    static IEnumerable<object> ConfirmOpened(
        IncidentInvestigationState state,
        object[] _,
        ConfirmIncidentOpened command)
    {
        if (state.Status == IncidentInvestigationStatus.Completed)
        {
            if (state.Decision?.Action == IncidentActions.OpenP1
                && state.ExternalId == command.ExternalId
                && state.IdempotencyKey == command.IdempotencyKey)
            {
                yield break;
            }

            throw new DomainException("ConfirmIncidentOpened: conflicting completion already recorded.");
        }

        RequireStatus(state, IncidentInvestigationStatus.ActionDecided, nameof(ConfirmIncidentOpened));
        if (state.Decision?.Action != IncidentActions.OpenP1)
            throw new DomainException("ConfirmIncidentOpened: policy did not authorize OPEN_INCIDENT:P1.");
        Required(command.ExternalId, "ConfirmIncidentOpened: ExternalId required.");
        Required(command.IdempotencyKey, "ConfirmIncidentOpened: IdempotencyKey required.");
        yield return new IncidentInvestigationEvents.V1.IncidentOpened(
            command.InvestigationId,
            command.ExternalId.Trim(),
            command.IdempotencyKey.Trim(),
            Now);
    }

    static IEnumerable<object> Park(
        IncidentInvestigationState state,
        object[] _,
        ParkIncidentAction command)
    {
        var failure = Normalize(command.Failure);
        if (state.Status == IncidentInvestigationStatus.ActionParked)
        {
            if (state.Failure == failure) yield break;
            throw new DomainException("ParkIncidentAction: conflicting failure already recorded.");
        }

        RequireStatus(state, IncidentInvestigationStatus.ActionDecided, nameof(ParkIncidentAction));
        yield return new IncidentInvestigationEvents.V1.IncidentActionParked(
            command.InvestigationId,
            failure,
            Now);
    }

    static IEnumerable<object> ConfirmReview(
        IncidentInvestigationState state,
        object[] _,
        ConfirmHumanReviewRequested command)
    {
        if (state.Status == IncidentInvestigationStatus.Completed)
        {
            if (state.Decision?.Action == IncidentActions.RequestHumanReview
                && state.ExternalId == command.ExternalId
                && state.IdempotencyKey == command.IdempotencyKey)
            {
                yield break;
            }

            throw new DomainException("ConfirmHumanReviewRequested: conflicting completion already recorded.");
        }

        RequireStatus(state, IncidentInvestigationStatus.ActionDecided, nameof(ConfirmHumanReviewRequested));
        if (state.Decision?.Action != IncidentActions.RequestHumanReview)
            throw new DomainException("ConfirmHumanReviewRequested: policy did not request human review.");
        Required(command.ExternalId, "ConfirmHumanReviewRequested: ExternalId required.");
        Required(command.IdempotencyKey, "ConfirmHumanReviewRequested: IdempotencyKey required.");
        yield return new IncidentInvestigationEvents.V1.IncidentHumanReviewRequested(
            command.InvestigationId,
            command.ExternalId.Trim(),
            command.IdempotencyKey.Trim(),
            Now);
    }

    static IEnumerable<object> RetryParked(
        IncidentInvestigationState state,
        object[] _,
        RetryParkedIncidentAction command)
    {
        Required(command.RequestId, "RetryParkedIncidentAction: RequestId required.");
        Required(command.RequestedBy, "RetryParkedIncidentAction: RequestedBy required.");
        Required(command.Reason, "RetryParkedIncidentAction: Reason required.");
        var requestId = command.RequestId.Trim();
        if (state.AppliedRetryRequestIds.Contains(requestId, StringComparer.Ordinal))
            yield break;

        RequireStatus(
            state,
            IncidentInvestigationStatus.ActionParked,
            nameof(RetryParkedIncidentAction));
        yield return new IncidentInvestigationEvents.V1.IncidentActionRetryRequested(
            command.InvestigationId,
            state.Decision!,
            requestId,
            command.RequestedBy.Trim(),
            command.Reason.Trim(),
            state.ManualRetryCount + 1,
            Now);
    }

    static IncidentSignal Normalize(IncidentSignal signal)
    {
        Required(signal.Service, "RecordIncidentSignal: Service required.");
        if (signal.TotalChecks <= 0)
            throw new DomainException("RecordIncidentSignal: TotalChecks must be positive.");
        if (signal.FailedChecks < 0 || signal.FailedChecks > signal.TotalChecks)
            throw new DomainException("RecordIncidentSignal: FailedChecks is outside the valid range.");
        if (signal.AffectedRegions < 0 || signal.AffectedRegions > signal.FailedChecks)
            throw new DomainException("RecordIncidentSignal: AffectedRegions is outside the valid range.");
        if (!DateTimeOffset.TryParse(signal.CollectedAt, out _))
            throw new DomainException("RecordIncidentSignal: CollectedAt must be an ISO timestamp.");
        return signal with { Service = signal.Service.Trim() };
    }

    static IncidentAnalysis Normalize(IncidentAnalysis analysis)
    {
        Required(analysis.Summary, "RecordIncidentAnalysis: Summary required.");
        var severities = new[] { "low", "medium", "high", "critical" };
        if (string.IsNullOrWhiteSpace(analysis.Severity)
            || !severities.Contains(analysis.Severity.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new DomainException("RecordIncidentAnalysis: invalid Severity.");
        }

        var evidence = NormalizeItems(analysis.Evidence);
        if (evidence.Length == 0)
            throw new DomainException("RecordIncidentAnalysis: Evidence required.");
        Validate(analysis.Route, nameof(RecordIncidentAnalysis));
        return analysis with
        {
            Summary = analysis.Summary.Trim(),
            Severity = analysis.Severity.Trim().ToLowerInvariant(),
            Evidence = evidence,
        };
    }

    static IncidentCritique Normalize(IncidentCritique critique)
    {
        var verdicts = new[] { "confirmed", "rejected", "inconclusive" };
        if (string.IsNullOrWhiteSpace(critique.Verdict)
            || !verdicts.Contains(critique.Verdict.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new DomainException("RecordIncidentCritique: invalid Verdict.");
        }
        if (critique.Confidence is < 0 or > 1)
            throw new DomainException("RecordIncidentCritique: Confidence must be between 0 and 1.");
        if (critique.Issues is null)
            throw new DomainException("RecordIncidentCritique: Issues required.");
        Validate(critique.Route, nameof(RecordIncidentCritique));
        return critique with
        {
            Verdict = critique.Verdict.Trim().ToLowerInvariant(),
            Issues = NormalizeItems(critique.Issues),
        };
    }

    static IncidentActionFailure Normalize(IncidentActionFailure failure)
    {
        Required(failure.Code, "ParkIncidentAction: failure Code required.");
        if (failure.Attempts <= 0)
            throw new DomainException("ParkIncidentAction: Attempts must be positive.");
        return failure with { Code = failure.Code.Trim() };
    }

    static void Validate(IncidentRouteAudit route, string command)
    {
        if (route is null
            || string.IsNullOrWhiteSpace(route.Profile)
            || string.IsNullOrWhiteSpace(route.RouteId)
            || string.IsNullOrWhiteSpace(route.Provider)
            || string.IsNullOrWhiteSpace(route.Model)
            || route.Attempts <= 0)
        {
            throw new DomainException($"{command}: complete route audit required.");
        }
    }

    static string[] NormalizeItems(IEnumerable<string>? items) => items is null
        ? []
        : [.. items
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    static void RequireStatus(
        IncidentInvestigationState state,
        IncidentInvestigationStatus expected,
        string command)
    {
        if (state.Status != expected)
            throw new DomainException($"{command}: expected {expected} but was {state.Status}.");
    }

    static void Required(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DomainException(message);
    }

    static StreamName Stream(string id) => new($"incident-investigation-{id}");
    static string Now => DateTimeOffset.UtcNow.ToString("O");
}
