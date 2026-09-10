using Microsoft.Extensions.Logging.Abstractions;
using MiyuAgents.Core;
using MiyuAgents.Llm;
using MiyuAgents.Pipeline;
using PassItOn.ElectionAudit.Shared;

namespace PassItOn.ElectionAudit.Pipeline;

public sealed class CollectObservationsStage(ElectionObservationCollector collector)
    : IPipelineStage
{
    public string StageName => "collect-read-only-observations";
    public int Priority => 100;

    public async Task<PipelineStageResult> ExecuteAsync(
        AgentContext ctx,
        PipelineContext pipeline,
        CancellationToken ct)
    {
        pipeline.SharedData[ElectionAuditArtifacts.Observations] =
            await collector.CollectAsync(ct);
        return PipelineStageResult.Continue(StageName, "all hypothetical feeds collected");
    }
}

public sealed class DetectFindingsStage : IPipelineStage
{
    public string StageName => "detect-findings";
    public int Priority => 200;

    public Task<PipelineStageResult> ExecuteAsync(
        AgentContext ctx,
        PipelineContext pipeline,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var observations = (ElectionObservationBundle)
                pipeline.SharedData[ElectionAuditArtifacts.Observations];
            var signal = ElectionAuditDetector.Inspect(observations);
            pipeline.SharedData[ElectionAuditArtifacts.Signal] = signal;
            return Task.FromResult(PipelineStageResult.Continue(
                StageName,
                $"{signal.Findings.Count} reproducible findings"));
        }
        catch (ElectionAuditContractException exception)
        {
            return Task.FromResult(PipelineStageResult.Abort(
                StageName,
                exception.Message,
                exception));
        }
    }
}

public sealed class AssessFindingsStage(ElectionAuditLlmService llm) : IPipelineStage
{
    public string StageName => "assess-findings";
    public int Priority => 300;

    public async Task<PipelineStageResult> ExecuteAsync(
        AgentContext ctx,
        PipelineContext pipeline,
        CancellationToken ct)
    {
        try
        {
            var signal = (ElectionAuditSignal)pipeline.SharedData[ElectionAuditArtifacts.Signal];
            var output = await llm.AssessAsync(signal, ct);
            pipeline.SharedData[ElectionAuditArtifacts.Assessment] = output.Value;
            return PipelineStageResult.Continue(
                StageName,
                output.Execution.RoutingDecision.Reason,
                output.Execution);
        }
        catch (Exception exception) when (
            exception is ElectionAuditContractException or LlmExecutionException)
        {
            return PipelineStageResult.Abort(StageName, exception.Message, exception);
        }
    }
}

public sealed class ValidateAssessmentStage : IPipelineStage
{
    public string StageName => "validate-assessment";
    public int Priority => 350;

    public Task<PipelineStageResult> ExecuteAsync(
        AgentContext ctx,
        PipelineContext pipeline,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var signal = (ElectionAuditSignal)pipeline.SharedData[ElectionAuditArtifacts.Signal];
            var assessment = (AuditAssessment)
                pipeline.SharedData[ElectionAuditArtifacts.Assessment];
            pipeline.SharedData[ElectionAuditArtifacts.Assessment] =
                ElectionAuditContracts.Validate(signal, assessment);
            return Task.FromResult(PipelineStageResult.Continue(
                StageName,
                "schema and complete finding citations accepted"));
        }
        catch (ElectionAuditContractException exception)
        {
            return Task.FromResult(PipelineStageResult.Abort(
                StageName,
                exception.Message,
                exception));
        }
    }
}

public sealed class CritiqueAssessmentStage(ElectionAuditLlmService llm) : IPipelineStage
{
    public string StageName => "critique-assessment";
    public int Priority => 400;

    public async Task<PipelineStageResult> ExecuteAsync(
        AgentContext ctx,
        PipelineContext pipeline,
        CancellationToken ct)
    {
        try
        {
            var signal = (ElectionAuditSignal)pipeline.SharedData[ElectionAuditArtifacts.Signal];
            var assessment = (AuditAssessment)
                pipeline.SharedData[ElectionAuditArtifacts.Assessment];
            var output = await llm.CritiqueAsync(signal, assessment, ct);
            pipeline.SharedData[ElectionAuditArtifacts.Critique] = output.Value;
            return PipelineStageResult.Continue(
                StageName,
                output.Execution.RoutingDecision.Reason,
                output.Execution);
        }
        catch (Exception exception) when (
            exception is ElectionAuditContractException or LlmExecutionException)
        {
            return PipelineStageResult.Abort(StageName, exception.Message, exception);
        }
    }
}

public sealed class ValidateCritiqueStage : IPipelineStage
{
    public string StageName => "validate-critique";
    public int Priority => 450;

    public Task<PipelineStageResult> ExecuteAsync(
        AgentContext ctx,
        PipelineContext pipeline,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var signal = (ElectionAuditSignal)pipeline.SharedData[ElectionAuditArtifacts.Signal];
            var critique = (AuditCritique)pipeline.SharedData[ElectionAuditArtifacts.Critique];
            pipeline.SharedData[ElectionAuditArtifacts.Critique] =
                ElectionAuditContracts.Validate(signal, critique);
            return Task.FromResult(PipelineStageResult.Continue(
                StageName,
                "critic contract and evidence coverage accepted"));
        }
        catch (ElectionAuditContractException exception)
        {
            return Task.FromResult(PipelineStageResult.Abort(
                StageName,
                exception.Message,
                exception));
        }
    }
}

public sealed class DecideStage : IPipelineStage
{
    public string StageName => "apply-versioned-policy";
    public int Priority => 600;

    public Task<PipelineStageResult> ExecuteAsync(
        AgentContext ctx,
        PipelineContext pipeline,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var decision = ElectionAuditPolicy.Decide(
            (ElectionAuditSignal)pipeline.SharedData[ElectionAuditArtifacts.Signal],
            (AuditCritique)pipeline.SharedData[ElectionAuditArtifacts.Critique]);
        pipeline.SharedData[ElectionAuditArtifacts.Decision] = decision;
        return Task.FromResult(PipelineStageResult.Continue(
            StageName,
            $"{decision.PolicyVersion}: {decision.Action}",
            decision));
    }
}

public sealed class OpenHumanReviewStage(IHumanReviewCasePort cases) : IPipelineStage
{
    public string StageName => "open-human-review";
    public int Priority => 700;

    public async Task<PipelineStageResult> ExecuteAsync(
        AgentContext ctx,
        PipelineContext pipeline,
        CancellationToken ct)
    {
        var receipt = await cases.OpenOnceAsync(
            $"provisional-audit:{ctx.MessageId}",
            (AuditDecision)pipeline.SharedData[ElectionAuditArtifacts.Decision],
            ct);
        pipeline.SharedData[ElectionAuditArtifacts.Receipt] = receipt;
        return PipelineStageResult.Continue(
            StageName,
            receipt.WasAlreadyCreated ? "case replayed" : "case opened once",
            receipt);
    }
}

public sealed record PipelineAuditRun(TurnResult Turn, PipelineContext Context);

public sealed class CompletePipelineScenario(
    PipelineRunner runner,
    InMemoryHumanReviewCasePort cases)
{
    public InMemoryHumanReviewCasePort Cases { get; } = cases;

    public async Task<PipelineAuditRun> RunAsync(string runId, CancellationToken ct = default)
    {
        var context = AgentContext.For(
            "authorized-defensive-auditor",
            runId,
            "Observe the synthetic provisional-count support environment.");
        var pipeline = new PipelineContext
        {
            EventBus = NullAgentEventBus.Instance,
            Broadcaster = NullBroadcaster.Instance
        };
        return new PipelineAuditRun(await runner.RunAsync(context, pipeline, ct), pipeline);
    }

    public static CompletePipelineScenario CreateOffline()
    {
        var cases = new InMemoryHumanReviewCasePort();
        var llm = ElectionAuditFixture.CreateLlm();
        IPipelineStage[] stages =
        [
            new CollectObservationsStage(ElectionAuditFixture.CreateCollector()),
            new DetectFindingsStage(),
            new AssessFindingsStage(llm),
            new ValidateAssessmentStage(),
            new CritiqueAssessmentStage(llm),
            new ValidateCritiqueStage(),
            new DecideStage(),
            new OpenHumanReviewStage(cases)
        ];
        return new CompletePipelineScenario(
            new PipelineRunner(stages, NullLogger<PipelineRunner>.Instance),
            cases);
    }
}
