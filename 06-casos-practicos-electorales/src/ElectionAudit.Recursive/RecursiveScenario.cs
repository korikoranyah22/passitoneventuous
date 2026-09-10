using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MiyuAgents.Core;
using MiyuAgents.Workflows;
using PassItOn.ElectionAudit.Shared;

namespace PassItOn.ElectionAudit.Recursive;

public sealed record IncrementalObservationState
{
    public ElectionCalendar? Calendar { get; init; }
    public IReadOnlyList<SoftwareBaseline>? Baselines { get; init; }
    public IReadOnlyList<RuntimeMeasurement>? Measurements { get; init; }
    public IReadOnlyList<TransmissionObservation>? Transmissions { get; init; }
    public IReadOnlyList<DigitizationAuditObservation>? Digitization { get; init; }
    public IReadOnlyList<AccessAuditObservation>? Accesses { get; init; }
    public IReadOnlyList<PublicationCheckpoint>? Publication { get; init; }
    public int Refinements { get; init; }

    public IReadOnlyList<string> MissingCriteria()
    {
        var missing = new List<string>();
        if (Calendar is null) missing.Add("calendar");
        if (Baselines is null || Measurements is null) missing.Add("software-baseline-and-runtime");
        if (Transmissions is null) missing.Add("transmission-receipts");
        if (Digitization is null) missing.Add("digitization-audit");
        if (Accesses is null) missing.Add("privileged-access-audit");
        if (Publication is null) missing.Add("publication-checkpoints");
        return missing;
    }

    public ElectionObservationBundle CompleteBundle() =>
        new(
            Calendar ?? throw new ElectionAuditContractException("calendar was not collected"),
            Baselines ?? throw new ElectionAuditContractException("baselines were not collected"),
            Measurements ?? throw new ElectionAuditContractException("measurements were not collected"),
            Transmissions ?? throw new ElectionAuditContractException("transmissions were not collected"),
            Digitization ?? throw new ElectionAuditContractException("digitization was not collected"),
            Accesses ?? throw new ElectionAuditContractException("accesses were not collected"),
            Publication ?? throw new ElectionAuditContractException("publication was not collected"));

    public string CycleKey() =>
        $"{Refinements}:"
        + $"{(Calendar is null ? 0 : 1)}"
        + $"{(Baselines is null || Measurements is null ? 0 : 1)}"
        + $"{(Transmissions is null ? 0 : 1)}"
        + $"{(Digitization is null ? 0 : 1)}"
        + $"{(Accesses is null ? 0 : 1)}"
        + $"{(Publication is null ? 0 : 1)}";
}

public sealed record AssessmentObjectiveState(
    ElectionAuditSignal Signal,
    AuditAssessment? Candidate = null,
    int Refinements = 0,
    string? LastFailure = null);

public sealed record CritiqueObjectiveState(
    ElectionAuditSignal Signal,
    AuditAssessment Assessment,
    AuditCritique? Candidate = null,
    int Refinements = 0,
    string? LastFailure = null);

public static class RecursiveElectionAuditNodes
{
    public static RecursiveObjectiveNode<IncrementalObservationState> CollectFeeds(
        IElectionCalendarPort calendar,
        ISoftwareBaselinePort software,
        ITransmissionObservationPort transmission,
        IDigitizationAuditPort digitization,
        IAccessAuditPort access,
        IPublicationCheckpointPort publication) =>
        new(
            "collect-complete-feed-set",
            _ => new IncrementalObservationState(),
            (frame, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var missing = frame.State.MissingCriteria();
                return ValueTask.FromResult(missing.Count == 0
                    ? RecursiveObjectiveAssessment.Satisfied("all required read-only feeds are present")
                    : RecursiveObjectiveAssessment.NeedsRefinement(
                        "one authorized feed is collected per refinement",
                        missing.ToArray()));
            },
            async (frame, assessment, ct) =>
            {
                var state = frame.State;
                if (state.Calendar is null)
                    return state with
                    {
                        Calendar = await calendar.ReadAsync(ct),
                        Refinements = state.Refinements + 1
                    };

                if (state.Baselines is null || state.Measurements is null)
                {
                    var baselinesTask = software.ReadBaselinesAsync(ct);
                    var measurementsTask = software.ReadMeasurementsAsync(ct);
                    await Task.WhenAll(baselinesTask, measurementsTask);
                    return state with
                    {
                        Baselines = await baselinesTask,
                        Measurements = await measurementsTask,
                        Refinements = state.Refinements + 1
                    };
                }

                if (state.Transmissions is null)
                    return state with
                    {
                        Transmissions = await transmission.ReadAsync(ct),
                        Refinements = state.Refinements + 1
                    };

                if (state.Digitization is null)
                    return state with
                    {
                        Digitization = await digitization.ReadAsync(ct),
                        Refinements = state.Refinements + 1
                    };

                if (state.Accesses is null)
                    return state with
                    {
                        Accesses = await access.ReadAsync(ct),
                        Refinements = state.Refinements + 1
                    };

                if (state.Publication is null)
                    return state with
                    {
                        Publication = await publication.ReadAsync(ct),
                        Refinements = state.Refinements + 1
                    };

                throw new ElectionAuditContractException(
                    $"collection cannot refine: {string.Join(", ", assessment.UnmetCriteria)}");
            },
            (frame, assessment, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return ValueTask.FromResult(ElectionAuditNodeResults.Done(
                    "collect-complete-feed-set",
                    "Collect complete feed set",
                    new Artifact(
                        ElectionAuditArtifacts.Observations,
                        "complete-read-only-observations",
                        frame.State.CompleteBundle()),
                    new Artifact(
                        ElectionAuditArtifacts.RecursionAudit,
                        "feed-collection-recursion",
                        new RecursionAudit("complete-read-only-feed-set", frame.State.Refinements))));
            },
            new RecursionPolicy
            {
                MaxDepth = 6,
                MaxCalls = 7,
                MaxDuration = TimeSpan.FromSeconds(10),
                DetectCycles = true
            },
            frame => frame.State.CycleKey(),
            name: "Collect all authorized feeds recursively");

    public static RecursiveObjectiveNode<AssessmentObjectiveState> AssessUntilGrounded(
        ElectionAuditLlmService llm) =>
        new(
            "assess-until-grounded",
            state => new AssessmentObjectiveState(
                ElectionAuditNodeResults.Required<ElectionAuditSignal>(
                    state,
                    ElectionAuditArtifacts.Signal)),
            (frame, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (frame.State.Candidate is null)
                    return ValueTask.FromResult(RecursiveObjectiveAssessment.NeedsRefinement(
                        "the assessment has not been produced",
                        "structured-assessment"));

                try
                {
                    ElectionAuditContracts.Validate(frame.State.Signal, frame.State.Candidate);
                    return ValueTask.FromResult(RecursiveObjectiveAssessment.Satisfied(
                        "assessment schema and complete citations are valid"));
                }
                catch (ElectionAuditContractException exception)
                {
                    return ValueTask.FromResult(RecursiveObjectiveAssessment.NeedsRefinement(
                        exception.Message,
                        "complete-known-finding-citations"));
                }
            },
            async (frame, assessment, ct) =>
            {
                var output = await llm.AssessAsync(frame.State.Signal, ct);
                return frame.State with
                {
                    Candidate = output.Value,
                    Refinements = frame.State.Refinements + 1,
                    LastFailure = assessment.Reason
                };
            },
            (frame, assessment, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var accepted = ElectionAuditContracts.Validate(
                    frame.State.Signal,
                    frame.State.Candidate!);
                return ValueTask.FromResult(ElectionAuditNodeResults.Done(
                    "assess-until-grounded",
                    "Assess until grounded",
                    new Artifact(ElectionAuditArtifacts.Assessment, "grounded-assessment", accepted),
                    new Artifact(
                        ElectionAuditArtifacts.RecursionAudit,
                        "assessment-recursion",
                        new RecursionAudit("grounded-assessment", frame.State.Refinements))));
            },
            new RecursionPolicy
            {
                MaxDepth = 3,
                MaxCalls = 4,
                MaxDuration = TimeSpan.FromSeconds(20),
                DetectCycles = true
            },
            frame => $"{frame.State.Refinements}:{frame.State.Candidate?.CitedFindingIds.Count ?? 0}",
            name: "Assess until evidence-grounded");

    public static RecursiveObjectiveNode<CritiqueObjectiveState> CritiqueUntilSupported(
        ElectionAuditLlmService llm) =>
        new(
            "critique-until-supported",
            state => new CritiqueObjectiveState(
                ElectionAuditNodeResults.Required<ElectionAuditSignal>(
                    state,
                    ElectionAuditArtifacts.Signal),
                ElectionAuditNodeResults.Required<AuditAssessment>(
                    state,
                    ElectionAuditArtifacts.Assessment)),
            (frame, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (frame.State.Candidate is null)
                    return ValueTask.FromResult(RecursiveObjectiveAssessment.NeedsRefinement(
                        "the independent critique has not been produced",
                        "structured-critique"));

                try
                {
                    var accepted = ElectionAuditContracts.Validate(
                        frame.State.Signal,
                        frame.State.Candidate);
                    var supported = accepted.Verdict == "supported"
                        && accepted.Confidence >= 0.85
                        && accepted.UnsupportedClaims.Count == 0;
                    return ValueTask.FromResult(supported
                        ? RecursiveObjectiveAssessment.Satisfied(
                            "critic supports the assessment with complete evidence coverage")
                        : RecursiveObjectiveAssessment.NeedsRefinement(
                            "critic has not supported the assessment at the required confidence",
                            "supported-verdict",
                            "confidence-at-least-0.85",
                            "no-unsupported-claims"));
                }
                catch (ElectionAuditContractException exception)
                {
                    return ValueTask.FromResult(RecursiveObjectiveAssessment.NeedsRefinement(
                        exception.Message,
                        "complete-known-finding-citations"));
                }
            },
            async (frame, assessment, ct) =>
            {
                var output = await llm.CritiqueAsync(
                    frame.State.Signal,
                    frame.State.Assessment,
                    ct);
                return frame.State with
                {
                    Candidate = output.Value,
                    Refinements = frame.State.Refinements + 1,
                    LastFailure = assessment.Reason
                };
            },
            (frame, assessment, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var accepted = ElectionAuditContracts.Validate(
                    frame.State.Signal,
                    frame.State.Candidate!);
                return ValueTask.FromResult(ElectionAuditNodeResults.Done(
                    "critique-until-supported",
                    "Critique until supported",
                    new Artifact(ElectionAuditArtifacts.Critique, "supported-critique", accepted),
                    new Artifact(
                        ElectionAuditArtifacts.RecursionAudit,
                        "critique-recursion",
                        new RecursionAudit("supported-independent-critique", frame.State.Refinements))));
            },
            new RecursionPolicy
            {
                MaxDepth = 3,
                MaxCalls = 4,
                MaxDuration = TimeSpan.FromSeconds(20),
                DetectCycles = true
            },
            frame => $"{frame.State.Refinements}:{frame.State.Candidate?.Verdict}:"
                + $"{frame.State.Candidate?.CitedFindingIds.Count ?? 0}",
            name: "Critique until independently supported");
}

public sealed class DetectRecursiveFindingsNode()
    : ElectionAuditNodeBase("detect-findings", "Detect reproducible findings")
{
    public override Task<NodeResult> RunNodeAsync(
        NodeState state,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var observations = ElectionAuditNodeResults.Required<ElectionObservationBundle>(
                state,
                ElectionAuditArtifacts.Observations);
            return Task.FromResult(Done(Output(
                ElectionAuditArtifacts.Signal,
                ElectionAuditDetector.Inspect(observations))));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Task.FromResult(Failed(exception));
        }
    }
}

public sealed class ApplyRecursivePolicyNode()
    : ElectionAuditNodeBase("apply-policy", "Apply versioned deterministic policy")
{
    public override Task<NodeResult> RunNodeAsync(
        NodeState state,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var signal = ElectionAuditNodeResults.Required<ElectionAuditSignal>(
                state,
                ElectionAuditArtifacts.Signal);
            var critique = ElectionAuditNodeResults.Required<AuditCritique>(
                state,
                ElectionAuditArtifacts.Critique);
            return Task.FromResult(Done(Output(
                ElectionAuditArtifacts.Decision,
                ElectionAuditPolicy.Decide(signal, critique))));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Task.FromResult(Failed(exception));
        }
    }
}

public sealed class OpenRecursiveHumanReviewNode(IHumanReviewCasePort cases)
    : ElectionAuditNodeBase("open-human-review", "Open idempotent human review")
{
    public override async Task<NodeResult> RunNodeAsync(
        NodeState state,
        CancellationToken ct = default)
    {
        try
        {
            var decision = ElectionAuditNodeResults.Required<AuditDecision>(
                state,
                ElectionAuditArtifacts.Decision);
            var receipt = await cases.OpenOnceAsync(
                $"provisional-audit:{state.Input}",
                decision,
                ct);
            return Done(Output(ElectionAuditArtifacts.Receipt, receipt));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(exception);
        }
    }
}

public sealed class CompleteRecursiveScenario(
    WorkflowNode workflow,
    InMemoryHumanReviewCasePort cases)
{
    public InMemoryHumanReviewCasePort Cases { get; } = cases;

    public Task<NodeResult> RunAsync(string runId, CancellationToken ct = default) =>
        workflow.RunNodeAsync(new NodeState { Input = runId }, ct);

    public static CompleteRecursiveScenario CreateOffline()
    {
        var environment = new SyntheticElectionEnvironment();
        var signal = ElectionAuditDetector.Inspect(
            ElectionAuditFixture.CreateCollector()
                .CollectAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult());
        var ids = signal.Findings.Select(item => item.Id).ToArray();
        var assessmentResponses = new[]
        {
            JsonSerializer.Serialize(new AuditAssessment(
                "Initial assessment with incomplete evidence coverage.",
                ["One control may have failed."],
                0.72,
                ids.Take(1).ToArray(),
                ["remaining finding evidence"])),
            JsonSerializer.Serialize(new AuditAssessment(
                "Correlated controls require authorized human investigation.",
                ["A failed release control could explain several observations."],
                0.89,
                ids,
                ["approved change record", "independent runtime measurement"]))
        };
        var critiqueResponses = new[]
        {
            JsonSerializer.Serialize(new AuditCritique(
                "inconclusive",
                0.64,
                ["causality is not demonstrated"],
                ["complete finding coverage"],
                ids.Take(1).ToArray())),
            JsonSerializer.Serialize(new AuditCritique(
                "supported",
                0.92,
                [],
                [],
                ids))
        };
        var llm = ElectionAuditFixture.CreateLlm(assessmentResponses, critiqueResponses);
        var cases = new InMemoryHumanReviewCasePort();

        IAgent[] ordered =
        [
            RecursiveElectionAuditNodes.CollectFeeds(
                environment,
                environment,
                environment,
                environment,
                environment,
                environment),
            new DetectRecursiveFindingsNode(),
            RecursiveElectionAuditNodes.AssessUntilGrounded(llm),
            RecursiveElectionAuditNodes.CritiqueUntilSupported(llm),
            new ApplyRecursivePolicyNode(),
            new OpenRecursiveHumanReviewNode(cases)
        ];
        var children = ordered.ToDictionary(agent => agent.AgentId, StringComparer.Ordinal);
        var workflow = new WorkflowNode(
            "recursive-election-audit",
            new SequenceStrategy(ordered.Select(agent => agent.AgentId).ToArray()),
            children,
            NullLogger<AgentBase<NodeResult>>.Instance,
            new ResiliencePolicy { MaxSteps = ordered.Length + 1, MaxRetries = 0 });
        return new CompleteRecursiveScenario(workflow, cases);
    }
}
