using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MiyuAgents.Llm;
using MiyuAgents.Routing;

namespace PassItOn.ElectionAudit.Shared;

public static class ElectionAuditDetector
{
    public static ElectionAuditSignal Inspect(ElectionObservationBundle bundle)
    {
        Validate(bundle);
        var findings = new List<AuditFinding>();

        var baselines = bundle.Baselines.ToDictionary(
            item => item.Component,
            StringComparer.OrdinalIgnoreCase);
        foreach (var measurement in bundle.Measurements)
        {
            if (!baselines.TryGetValue(measurement.Component, out var baseline))
            {
                findings.Add(Finding(
                    $"release-unapproved-{measurement.Id}",
                    AuditFindingCategories.ReleaseIntegrity,
                    "critical",
                    "A running component has no approved release baseline.",
                    measurement.Id));
                continue;
            }

            if (!measurement.RunningVersion.Equals(
                    baseline.ApprovedVersion,
                    StringComparison.Ordinal)
                || !measurement.ObservedDigest.Equals(
                    baseline.ApprovedDigest,
                    StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding(
                    $"release-mismatch-{measurement.Id}",
                    AuditFindingCategories.ReleaseIntegrity,
                    "critical",
                    "Runtime version or digest differs from the approved release manifest.",
                    measurement.Id,
                    $"baseline:{baseline.Component}"));
            }
        }

        foreach (var transmission in bundle.Transmissions)
        {
            if (transmission.ReceivedAt is null
                || transmission.ReceivedAt - transmission.SentAt > TimeSpan.FromMinutes(2))
            {
                findings.Add(Finding(
                    $"transmission-timeout-{transmission.Id}",
                    AuditFindingCategories.TransmissionAvailability,
                    "high",
                    "A transmission has no timely receipt inside the synthetic SLO.",
                    transmission.Id));
            }

            if (transmission.ReceivedEnvelopeDigest is not null
                && !transmission.SentEnvelopeDigest.Equals(
                    transmission.ReceivedEnvelopeDigest,
                    StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding(
                    $"transmission-digest-{transmission.Id}",
                    AuditFindingCategories.TransmissionIntegrity,
                    "critical",
                    "Sent and received envelope digests differ.",
                    transmission.Id));
            }
        }

        foreach (var digitization in bundle.Digitization)
        {
            if (!digitization.FirstEntryDigest.Equals(
                digitization.SecondEntryDigest,
                StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding(
                    $"digitization-disagreement-{digitization.Id}",
                    AuditFindingCategories.DigitizationConsistency,
                    "high",
                    "Independent digitization entries do not agree.",
                    digitization.Id,
                    $"telegram:{digitization.TelegramId}"));
            }

            if (!digitization.VisibleToFiscalization)
            {
                findings.Add(Finding(
                    $"fiscalization-visibility-{digitization.Id}",
                    AuditFindingCategories.FiscalizationVisibility,
                    "high",
                    "A processed telegram is not visible through the fiscalization view.",
                    digitization.Id,
                    $"telegram:{digitization.TelegramId}"));
            }
        }

        var privilegedAccesses = bundle.Accesses
            .Where(item => item.Succeeded)
            .Where(item => item.Role.Contains("admin", StringComparison.OrdinalIgnoreCase))
            .Where(item => !item.SourceZone.Equals(
                "administration-segment",
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Id)
            .ToArray();
        if (privilegedAccesses.Length > 0)
        {
            findings.Add(new AuditFinding(
                "privileged-access-outside-zone",
                AuditFindingCategories.PrivilegedAccess,
                "high",
                "A successful administrative access originated outside the approved zone.",
                privilegedAccesses));
        }

        var orderedPublication = bundle.Publication.OrderBy(item => item.Sequence).ToArray();
        for (var index = 1; index < orderedPublication.Length; index++)
        {
            var previous = orderedPublication[index - 1];
            var current = orderedPublication[index];
            if (current.Sequence != previous.Sequence + 1
                || current.IncludedTelegrams < previous.IncludedTelegrams)
            {
                findings.Add(Finding(
                    $"publication-monotonicity-{current.Id}",
                    AuditFindingCategories.PublicationMonotonicity,
                    "critical",
                    "Publication sequence skipped or the included-telegram count regressed.",
                    previous.Id,
                    current.Id));
            }
        }

        return new ElectionAuditSignal(
            bundle.Calendar.ElectionId,
            bundle.Calendar.GeneralElectionDate,
            bundle.Calendar.Authority,
            findings);
    }

    private static AuditFinding Finding(
        string id,
        string category,
        string severity,
        string summary,
        params string[] evidence) =>
        new(id, category, severity, summary, evidence);

    private static void Validate(ElectionObservationBundle bundle)
    {
        if (string.IsNullOrWhiteSpace(bundle.Calendar.ElectionId)
            || string.IsNullOrWhiteSpace(bundle.Calendar.SourceReference)
            || string.IsNullOrWhiteSpace(bundle.Calendar.VotingInstrument)
            || bundle.Calendar.ObservablePhases.Count == 0
            || bundle.Calendar.Contests.Count == 0)
        {
            throw new ElectionAuditContractException("election calendar is incomplete");
        }

        var ids = bundle.Measurements.Select(item => item.Id)
            .Concat(bundle.Transmissions.Select(item => item.Id))
            .Concat(bundle.Digitization.Select(item => item.Id))
            .Concat(bundle.Accesses.Select(item => item.Id))
            .Concat(bundle.Publication.Select(item => item.Id))
            .ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace)
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
        {
            throw new ElectionAuditContractException(
                "observation ids must be non-empty and globally unique");
        }

        if (bundle.Baselines.Count == 0
            || bundle.Measurements.Count == 0
            || bundle.Transmissions.Count == 0
            || bundle.Digitization.Count == 0
            || bundle.Accesses.Count == 0
            || bundle.Publication.Count < 2)
        {
            throw new ElectionAuditContractException(
                "one or more required read-only observation feeds are empty");
        }
    }
}

public static class ElectionAuditContracts
{
    private static readonly HashSet<string> Verdicts = new(
        ["supported", "inconclusive", "rejected"],
        StringComparer.OrdinalIgnoreCase);

    public static AuditAssessment Validate(
        ElectionAuditSignal signal,
        AuditAssessment assessment)
    {
        if (string.IsNullOrWhiteSpace(assessment.Summary)
            || assessment.Hypotheses is null or { Count: 0 }
            || assessment.Hypotheses.Any(string.IsNullOrWhiteSpace)
            || assessment.Confidence is < 0 or > 1
            || assessment.CitedFindingIds is null
            || assessment.RequestedEvidence is null)
        {
            throw new ElectionAuditContractException("assessment contract is invalid");
        }

        var citations = Normalize(assessment.CitedFindingIds);
        RequireCompleteCitations(signal, citations, "assessment");
        return assessment with
        {
            Summary = assessment.Summary.Trim(),
            Hypotheses = Normalize(assessment.Hypotheses),
            CitedFindingIds = citations,
            RequestedEvidence = Normalize(assessment.RequestedEvidence)
        };
    }

    public static AuditCritique Validate(
        ElectionAuditSignal signal,
        AuditCritique critique)
    {
        if (!Verdicts.Contains(critique.Verdict)
            || critique.Confidence is < 0 or > 1
            || critique.UnsupportedClaims is null
            || critique.MissingEvidence is null
            || critique.CitedFindingIds is null)
        {
            throw new ElectionAuditContractException("critique contract is invalid");
        }

        var citations = Normalize(critique.CitedFindingIds);
        RequireCompleteCitations(signal, citations, "critique");
        return critique with
        {
            Verdict = critique.Verdict.Trim().ToLowerInvariant(),
            UnsupportedClaims = Normalize(critique.UnsupportedClaims),
            MissingEvidence = Normalize(critique.MissingEvidence),
            CitedFindingIds = citations
        };
    }

    private static void RequireCompleteCitations(
        ElectionAuditSignal signal,
        IReadOnlyCollection<string> citations,
        string document)
    {
        var known = signal.Findings.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = citations.FirstOrDefault(item => !known.Contains(item));
        if (unknown is not null)
            throw new ElectionAuditContractException(
                $"{document} cites unknown finding '{unknown}'");

        var cited = citations.ToHashSet(StringComparer.Ordinal);
        var missing = known.FirstOrDefault(item => !cited.Contains(item));
        if (missing is not null)
            throw new ElectionAuditContractException(
                $"{document} does not cite finding '{missing}'");
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> source) =>
        source
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

public static class ElectionAuditPolicy
{
    public const string Version = "argentina-provisional-audit/v1";
    public const string LegalScope =
        "Defensive observation of provisional-count support systems; not definitive scrutiny.";

    public static AuditDecision Decide(ElectionAuditSignal signal, AuditCritique critique)
    {
        var categories = signal.Findings.Select(item => item.Category)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reasons = new List<string>();
        if (categories.Contains(AuditFindingCategories.ReleaseIntegrity))
            reasons.Add("runtime release differs from approved manifest");
        if (categories.Contains(AuditFindingCategories.TransmissionIntegrity))
            reasons.Add("transmission envelope integrity mismatch");
        if (categories.Contains(AuditFindingCategories.PublicationMonotonicity))
            reasons.Add("publication checkpoints are not monotonic");

        var completeCitations = signal.Findings.All(finding =>
            critique.CitedFindingIds.Contains(finding.Id, StringComparer.Ordinal));
        var supported = critique.Verdict.Equals("supported", StringComparison.OrdinalIgnoreCase)
            && critique.Confidence >= 0.85
            && critique.UnsupportedClaims.Count == 0
            && completeCitations;
        if (supported) reasons.Add("independent critique supports every cited finding");

        var action = categories.Contains(AuditFindingCategories.ReleaseIntegrity)
            && categories.Contains(AuditFindingCategories.TransmissionIntegrity)
                ? "OPEN_URGENT_HUMAN_REVIEW"
                : signal.Findings.Any(item => item.Severity is "critical" or "high")
                    ? "OPEN_PRIORITY_HUMAN_REVIEW"
                    : "CONTINUE_ENHANCED_MONITORING";

        return new AuditDecision(
            action,
            Version,
            reasons,
            RequiresHumanApproval: true,
            AutomaticMutationAllowed: false,
            LegalScope: LegalScope);
    }
}

public sealed record RoutedAuditOutput<T>(T Value, LlmExecutionResult Execution);

public sealed class ElectionAuditLlmService(ILlmCallExecutor executor)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<RoutedAuditOutput<AuditAssessment>> AssessAsync(
        ElectionAuditSignal signal,
        CancellationToken ct = default)
    {
        var result = await executor.CompleteAsync(
            new LlmRequest
            {
                Model = "default",
                SystemPrompt =
                    "Analyze only supplied defensive findings about provisional-count support systems. "
                    + "Do not infer vote validity, propose intrusion, or authorize containment. "
                    + "Return JSON: summary, hypotheses[], confidence, citedFindingIds[], "
                    + "requestedEvidence[]. Cite every finding id.",
                Messages = [new ConversationMessage("user", JsonSerializer.Serialize(signal))]
            },
            "private-provisional-triage",
            ct);
        return new RoutedAuditOutput<AuditAssessment>(Parse<AuditAssessment>(result, "assessment"), result);
    }

    public async Task<RoutedAuditOutput<AuditCritique>> CritiqueAsync(
        ElectionAuditSignal signal,
        AuditAssessment assessment,
        CancellationToken ct = default)
    {
        var result = await executor.CompleteAsync(
            new LlmRequest
            {
                Model = "default",
                SystemPrompt =
                    "Independently critique the assessment and reject unsupported claims. "
                    + "Return JSON: verdict (supported|inconclusive|rejected), confidence, "
                    + "unsupportedClaims[], missingEvidence[], citedFindingIds[]. Cite every finding id.",
                Messages =
                [
                    new ConversationMessage(
                        "user",
                        JsonSerializer.Serialize(new { signal, assessment }))
                ]
            },
            "private-provisional-critic",
            ct);
        return new RoutedAuditOutput<AuditCritique>(Parse<AuditCritique>(result, "critique"), result);
    }

    private static T Parse<T>(LlmExecutionResult result, string name)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(result.Response.Content, Json)
                ?? throw new ElectionAuditContractException($"empty {name}");
        }
        catch (JsonException)
        {
            throw new ElectionAuditContractException($"malformed {name} JSON");
        }
    }
}

public sealed class SyntheticElectionEnvironment :
    IElectionCalendarPort,
    ISoftwareBaselinePort,
    ITransmissionObservationPort,
    IDigitizationAuditPort,
    IAccessAuditPort,
    IPublicationCheckpointPort
{
    private static readonly DateTimeOffset BaseTime =
        DateTimeOffset.Parse("2027-10-24T21:00:00Z");

    Task<ElectionCalendar> IElectionCalendarPort.ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ElectionCalendar(
            "national-general-2027-synthetic",
            new DateOnly(2027, 10, 24),
            CalendarAuthority.ProjectedFromCurrentLaw,
            "Código Electoral Nacional, Constitución Nacional and Ley 27.781; official 2027 call still required",
            "paper-single-ballot",
            [
                new ElectionPhaseWindow(
                    "pre-election-simulation",
                    BaseTime.AddDays(-21),
                    BaseTime.AddDays(-14)),
                new ElectionPhaseWindow(
                    "provisional-count-observation",
                    BaseTime,
                    BaseTime.AddHours(10)),
                new ElectionPhaseWindow(
                    "post-election-audit",
                    BaseTime.AddHours(10),
                    BaseTime.AddDays(2))
            ],
            [
                new ElectionContest(
                    "president-and-vice-president",
                    "national-single-district",
                    "four-year constitutional term",
                    "expected from the constitutional cycle; official call required"),
                new ElectionContest(
                    "national-deputies",
                    "provincial-and-CABA-districts",
                    "one half of the chamber",
                    "constitutional periodic renewal; official seat allocation required"),
                new ElectionContest(
                    "national-senators",
                    "rotating provincial-and-CABA-districts",
                    "one third of the districts",
                    "constitutional periodic renewal; official district list required")
            ]));
    }

    public Task<IReadOnlyList<SoftwareBaseline>> ReadBaselinesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SoftwareBaseline>>(
        [
            new("transmission-client", "7.4.2", "sha256:approved-transmission", BaseTime.AddDays(-30)),
            new("provisional-totalizer", "5.1.0", "sha256:approved-totalizer", BaseTime.AddDays(-30))
        ]);
    }

    public Task<IReadOnlyList<RuntimeMeasurement>> ReadMeasurementsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<RuntimeMeasurement>>(
        [
            new(
                "measurement-cte-1042",
                "transmission-client",
                "cte-1042",
                "7.4.2",
                "sha256:unexpected-transmission",
                BaseTime.AddMinutes(1)),
            new(
                "measurement-totalizer-a",
                "provisional-totalizer",
                "totalizer-a",
                "5.1.0",
                "sha256:approved-totalizer",
                BaseTime.AddMinutes(1))
        ]);
    }

    Task<IReadOnlyList<TransmissionObservation>> ITransmissionObservationPort.ReadAsync(
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<TransmissionObservation>>(
        [
            new(
                "transmission-1",
                "telegram-synthetic-0001",
                "polling-place-1042",
                "CTE",
                BaseTime.AddMinutes(2),
                BaseTime.AddMinutes(3),
                "sha256:sent-1",
                "sha256:received-different"),
            new(
                "transmission-2",
                "telegram-synthetic-0002",
                "polling-place-1043",
                "SED",
                BaseTime.AddMinutes(4),
                null,
                "sha256:sent-2",
                null)
        ]);
    }

    Task<IReadOnlyList<DigitizationAuditObservation>> IDigitizationAuditPort.ReadAsync(
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DigitizationAuditObservation>>(
        [
            new(
                "digitization-1",
                "telegram-synthetic-0001",
                "sha256:image-1",
                "sha256:entry-a",
                "sha256:entry-b",
                VisibleToFiscalization: true,
                BaseTime.AddMinutes(5)),
            new(
                "digitization-2",
                "telegram-synthetic-0002",
                "sha256:image-2",
                "sha256:entry-c",
                "sha256:entry-c",
                VisibleToFiscalization: false,
                BaseTime.AddMinutes(6))
        ]);
    }

    Task<IReadOnlyList<AccessAuditObservation>> IAccessAuditPort.ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AccessAuditObservation>>(
        [
            new(
                "access-1",
                "principal:7c91",
                "platform-admin",
                "provisional-totalizer",
                "vendor-support-segment",
                Succeeded: true,
                BaseTime.AddMinutes(7))
        ]);
    }

    Task<IReadOnlyList<PublicationCheckpoint>> IPublicationCheckpointPort.ReadAsync(
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PublicationCheckpoint>>(
        [
            new("publication-41", 41, 1000, "sha256:dataset-41", BaseTime.AddMinutes(8)),
            new("publication-43", 43, 950, "sha256:dataset-43", BaseTime.AddMinutes(9))
        ]);
    }
}

public static class ElectionAuditFixture
{
    public static ElectionObservationCollector CreateCollector()
    {
        var environment = new SyntheticElectionEnvironment();
        return new ElectionObservationCollector(
            environment,
            environment,
            environment,
            environment,
            environment,
            environment);
    }

    public static ElectionAuditLlmService CreateLlm(
        IEnumerable<string>? assessmentResponses = null,
        IEnumerable<string>? critiqueResponses = null)
    {
        var defaultSignal = ElectionAuditDetector.Inspect(
            CreateCollector().CollectAsync(CancellationToken.None).GetAwaiter().GetResult());
        var ids = defaultSignal.Findings.Select(item => item.Id).ToArray();
        assessmentResponses ??=
        [
            JsonSerializer.Serialize(new AuditAssessment(
                "Several correlated controls require authorized investigation.",
                ["A failed release control could explain multiple observations."],
                0.88,
                ids,
                ["approved change record", "independent runtime measurement"]))
        ];
        critiqueResponses ??=
        [
            JsonSerializer.Serialize(new AuditCritique(
                "supported",
                0.91,
                [],
                [],
                ids))
        ];

        var router = new LlmGatewayRouter(
        [
            new ScriptedGateway(
                "offline-private",
                "local-triage",
                ["private", "local", "fast", "triage", "structured-output"],
                assessmentResponses),
            new ScriptedGateway(
                "offline-private",
                "local-critic",
                ["private", "local", "reasoning", "critic", "structured-output"],
                critiqueResponses)
        ]);
        var profiles = new LlmRouteProfileCatalog(
        [
            new LlmRouteProfile
            {
                Name = "private-provisional-triage",
                Route = new RouteRequest
                {
                    RequiredTags = ["private", "triage", "structured-output"],
                    PreferredTags = new Dictionary<string, int> { ["local"] = 50, ["fast"] = 20 },
                    AllowFallback = false
                },
                PreferredModels = ["local-triage"]
            },
            new LlmRouteProfile
            {
                Name = "private-provisional-critic",
                Route = new RouteRequest
                {
                    RequiredTags = ["private", "critic", "reasoning", "structured-output"],
                    AllowFallback = false
                },
                PreferredModels = ["local-critic"]
            }
        ]);
        return new ElectionAuditLlmService(new LlmCallExecutor(
            router,
            profiles,
            new DefaultLlmFailureClassifier(),
            [],
            NullLogger<LlmCallExecutor>.Instance));
    }

    private sealed class ScriptedGateway(
        string provider,
        string model,
        string[] tags,
        IEnumerable<string> responses) : ILlmGateway, ILlmGatewayRoutingMetadata
    {
        private readonly object _gate = new();
        private readonly Queue<string> _responses = new(responses);
        private readonly LlmGatewayStats _stats = new();
        private string? _last;

        public string ProviderName => provider;
        public IReadOnlyList<string> SupportedModels => [model];
        public IReadOnlyCollection<string> RoutingTags => tags;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            string content;
            lock (_gate)
            {
                if (_responses.Count > 0) _last = _responses.Dequeue();
                content = _last ?? throw new InvalidOperationException("scripted response is empty");
            }
            var usage = new LlmUsage(40, 25);
            _stats.Record(usage);
            return Task.FromResult(new LlmResponse(content, usage, "stop"));
        }

        public async IAsyncEnumerable<LlmChunk> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var response = await CompleteAsync(request, ct);
            yield return new LlmChunk(
                response.Content,
                IsComplete: true,
                IsError: false,
                response.Usage);
        }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public LlmGatewayStats GetStats() => _stats;
    }
}
