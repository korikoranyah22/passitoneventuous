using Microsoft.Extensions.Logging.Abstractions;
using MiyuAgents.Core;
using MiyuAgents.Workflows;
using PassItOn.ElectionAudit.Shared;

namespace PassItOn.ElectionAudit.FixedNodes;

public sealed class CollectObservationsNode(ElectionObservationCollector collector)
    : ElectionAuditNodeBase("collect-observations", "Collect read-only observations")
{
    public override async Task<NodeResult> RunNodeAsync(
        NodeState state,
        CancellationToken ct = default)
    {
        try
        {
            return Done(Output(
                ElectionAuditArtifacts.Observations,
                await collector.CollectAsync(ct)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(exception);
        }
    }
}

public sealed class DetectFindingsNode()
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

public sealed class AssessFindingsNode(ElectionAuditLlmService llm)
    : ElectionAuditNodeBase("assess-findings", "Assess findings with routed LLM")
{
    public override async Task<NodeResult> RunNodeAsync(
        NodeState state,
        CancellationToken ct = default)
    {
        try
        {
            var signal = ElectionAuditNodeResults.Required<ElectionAuditSignal>(
                state,
                ElectionAuditArtifacts.Signal);
            var output = await llm.AssessAsync(signal, ct);
            return Done(Output(ElectionAuditArtifacts.Assessment, output.Value));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(exception);
        }
    }
}

public sealed class ValidateAssessmentNode()
    : ElectionAuditNodeBase("validate-assessment", "Validate assessment contract")
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
            var assessment = ElectionAuditNodeResults.Required<AuditAssessment>(
                state,
                ElectionAuditArtifacts.Assessment);
            return Task.FromResult(Done(Output(
                ElectionAuditArtifacts.Assessment,
                ElectionAuditContracts.Validate(signal, assessment))));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Task.FromResult(Failed(exception));
        }
    }
}

public sealed class CritiqueAssessmentNode(ElectionAuditLlmService llm)
    : ElectionAuditNodeBase("critique-assessment", "Critique assessment independently")
{
    public override async Task<NodeResult> RunNodeAsync(
        NodeState state,
        CancellationToken ct = default)
    {
        try
        {
            var signal = ElectionAuditNodeResults.Required<ElectionAuditSignal>(
                state,
                ElectionAuditArtifacts.Signal);
            var assessment = ElectionAuditNodeResults.Required<AuditAssessment>(
                state,
                ElectionAuditArtifacts.Assessment);
            var output = await llm.CritiqueAsync(signal, assessment, ct);
            return Done(Output(ElectionAuditArtifacts.Critique, output.Value));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(exception);
        }
    }
}

public sealed class ValidateCritiqueNode()
    : ElectionAuditNodeBase("validate-critique", "Validate critic contract")
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
                ElectionAuditArtifacts.Critique,
                ElectionAuditContracts.Validate(signal, critique))));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Task.FromResult(Failed(exception));
        }
    }
}

public sealed class ApplyPolicyNode()
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

public sealed class OpenHumanReviewNode(IHumanReviewCasePort cases)
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

public sealed class CompleteFixedNodeScenario(
    WorkflowNode workflow,
    InMemoryHumanReviewCasePort cases)
{
    public InMemoryHumanReviewCasePort Cases { get; } = cases;

    public Task<NodeResult> RunAsync(string runId, CancellationToken ct = default) =>
        workflow.RunNodeAsync(new NodeState { Input = runId }, ct);

    public static CompleteFixedNodeScenario CreateOffline()
    {
        var llm = ElectionAuditFixture.CreateLlm();
        var cases = new InMemoryHumanReviewCasePort();
        IAgent[] ordered =
        [
            new CollectObservationsNode(ElectionAuditFixture.CreateCollector()),
            new DetectFindingsNode(),
            new AssessFindingsNode(llm),
            new ValidateAssessmentNode(),
            new CritiqueAssessmentNode(llm),
            new ValidateCritiqueNode(),
            new ApplyPolicyNode(),
            new OpenHumanReviewNode(cases)
        ];
        var children = ordered.ToDictionary(agent => agent.AgentId, StringComparer.Ordinal);
        var workflow = new WorkflowNode(
            "fixed-election-audit",
            new SequenceStrategy(ordered.Select(agent => agent.AgentId).ToArray()),
            children,
            NullLogger<AgentBase<NodeResult>>.Instance,
            new ResiliencePolicy { MaxSteps = ordered.Length + 1, MaxRetries = 0 });
        return new CompleteFixedNodeScenario(workflow, cases);
    }
}
