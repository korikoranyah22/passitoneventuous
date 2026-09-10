using CursoAgentes.Engine.Incidents;
using MiyuAgents.Examples.FixedNodeWorkflow;
using MiyuAgents.Examples.IncidentResponse;
using MiyuAgents.Examples.RoutingWorkflow;
using MiyuAgents.Workflows;
using DomainAnalysis = CursoAgentes.Domain.Incidents.IncidentAnalysis;
using DomainCritique = CursoAgentes.Domain.Incidents.IncidentCritique;
using DomainDecision = CursoAgentes.Domain.Incidents.IncidentDecision;
using DomainInput = CursoAgentes.Engine.Incidents.IncidentInvestigationInput;
using DomainPolicy = CursoAgentes.Domain.Incidents.IncidentPolicy;
using DomainRouteAudit = CursoAgentes.Domain.Incidents.IncidentRouteAudit;
using DomainSignal = CursoAgentes.Domain.Incidents.IncidentSignal;

namespace CursoAgentes.MiyuAgents;

/// <summary>
/// Anti-corruption mapper between orchestration artifacts and the event-sourced
/// application contract. Eventuous does not depend on PipelineContext or NodeResult;
/// MiyuAgents does not depend on aggregate commands or events.
/// </summary>
public static class MiyuIncidentInvestigationMapper
{
    public static DomainInput FromPipeline(
        string investigationId,
        IncidentPipelineRun run)
    {
        if (run.Result.WasAborted)
            throw new InvalidOperationException("Cannot persist an aborted incident pipeline.");

        var signal = Required<StatusSignal>(run, IncidentPipelineKeys.Signal);
        var draft = Required<IncidentDraft>(run, IncidentPipelineKeys.Draft);
        var critique = Required<IncidentCritique>(run, IncidentPipelineKeys.Critique);
        var decision = Required<IncidentDecision>(run, IncidentPipelineKeys.Decision);
        return Map(
            investigationId,
            signal,
            draft,
            critique,
            decision,
            StageAudit(run, "draft-incident"),
            StageAudit(run, "critique-incident"));
    }

    public static DomainInput FromFixedNodes(
        string investigationId,
        IncidentNodeWorkflowRun run)
    {
        if (run.Result.Signal != NodeSignal.Done)
            throw new InvalidOperationException(
                $"Cannot persist a node workflow that ended with {run.Result.Signal}.");

        var signal = Required<StatusSignal>(run, IncidentArtifactKinds.Signal);
        var draft = Required<IncidentDraft>(run, IncidentArtifactKinds.Draft);
        var critique = Required<IncidentCritique>(run, IncidentArtifactKinds.Critique);
        var decision = Required<IncidentDecision>(run, IncidentArtifactKinds.Decision);
        return Map(
            investigationId,
            signal,
            draft,
            critique,
            decision,
            AuditArtifact(run, "analyst-route"),
            AuditArtifact(run, "critic-route"));
    }

    static DomainInput Map(
        string investigationId,
        StatusSignal signal,
        IncidentDraft draft,
        IncidentCritique critique,
        IncidentDecision miyuDecision,
        IncidentLlmAudit analystAudit,
        IncidentLlmAudit criticAudit)
    {
        var domainSignal = new DomainSignal(
            signal.Service,
            signal.TotalChecks,
            signal.FailedChecks,
            signal.AffectedRegions,
            signal.CollectedAt.ToString("O"));
        var domainAnalysis = new DomainAnalysis(
            draft.Summary,
            draft.Severity,
            [.. draft.Evidence],
            Map(analystAudit));
        var domainCritique = new DomainCritique(
            critique.Verdict,
            critique.Confidence,
            [.. critique.Issues],
            Map(criticAudit));
        EnsureEquivalentDecision(
            miyuDecision,
            DomainPolicy.Decide(domainSignal, domainCritique));
        return new DomainInput(
            investigationId,
            domainSignal.Service,
            domainSignal,
            domainAnalysis,
            domainCritique);
    }

    static DomainRouteAudit Map(IncidentLlmAudit audit)
    {
        var execution = audit.Execution;
        var successful = execution.Attempts.LastOrDefault(attempt => attempt.Succeeded);
        var routeId = execution.RoutingDecision.Selected?.Id
            ?? successful?.RouteId
            ?? throw new InvalidOperationException("The LLM audit has no selected route id.");
        return new DomainRouteAudit(
            audit.ProfileName,
            routeId,
            successful?.ProviderName ?? execution.Gateway.ProviderName,
            successful?.Model ?? execution.Model,
            execution.Attempts.Count);
    }

    static void EnsureEquivalentDecision(
        IncidentDecision miyu,
        DomainDecision domain)
    {
        if (miyu.Action != domain.Action
            || miyu.PolicyVersion != domain.PolicyVersion
            || !miyu.Reasons.SequenceEqual(domain.Reasons, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "MiyuAgents and the event-sourced domain produced different incident decisions.");
        }
    }

    static T Required<T>(IncidentPipelineRun run, string key) =>
        run.Pipeline.SharedData.TryGetValue(key, out var value) && value is T typed
            ? typed
            : throw new InvalidOperationException($"Pipeline output '{key}' is missing.");

    static IncidentLlmAudit StageAudit(IncidentPipelineRun run, string stageName) =>
        run.Result.StageHistory.LastOrDefault(stage => stage.StageName == stageName)?.StageData
            is IncidentLlmAudit audit
                ? audit
                : throw new InvalidOperationException(
                    $"Pipeline routing audit for '{stageName}' is missing.");

    static T Required<T>(IncidentNodeWorkflowRun run, string kind) =>
        run.Result.Artifacts.LastOrDefault(artifact => artifact.Kind == kind)?.Payload is T typed
            ? typed
            : throw new InvalidOperationException($"Node artifact '{kind}' is missing.");

    static IncidentLlmAudit AuditArtifact(
        IncidentNodeWorkflowRun run,
        string name) =>
        run.Result.Artifacts.LastOrDefault(artifact =>
            artifact.Kind == IncidentArtifactKinds.LlmExecution
            && artifact.Name == name)?.Payload is IncidentLlmAudit audit
                ? audit
                : throw new InvalidOperationException(
                    $"Node routing audit '{name}' is missing.");
}

public sealed record MiyuEventuousIncidentRun(
    string Orchestration,
    DomainInput Input,
    IncidentInvestigationRun Eventuous,
    int MiyuActionExecutions);

public sealed class MiyuIncidentInvestigationBridge(IncidentInvestigationProcess process)
{
    public async Task<MiyuEventuousIncidentRun> RunPipelineAsync(
        string investigationId,
        string request,
        CancellationToken ct = default)
    {
        var scenario = IncidentPipelineScenario.CreateOfflineForEventSourcing();
        var miyuRun = await scenario.RunAsync(investigationId, request, ct);
        var input = MiyuIncidentInvestigationMapper.FromPipeline(investigationId, miyuRun);
        var persisted = await process.RunAsync(input, ct);
        return new MiyuEventuousIncidentRun(
            "pipeline",
            input,
            persisted,
            scenario.ActionPort.ExecutionCount);
    }

    public async Task<MiyuEventuousIncidentRun> RunFixedNodesAsync(
        string investigationId,
        string request,
        CancellationToken ct = default)
    {
        var scenario = IncidentNodeWorkflowScenario.CreateOfflineForEventSourcing();
        var miyuRun = await scenario.RunAsync(investigationId, request, ct);
        var input = MiyuIncidentInvestigationMapper.FromFixedNodes(investigationId, miyuRun);
        var persisted = await process.RunAsync(input, ct);
        return new MiyuEventuousIncidentRun(
            "fixed-nodes",
            input,
            persisted,
            scenario.ActionPort.ExecutionCount);
    }
}
