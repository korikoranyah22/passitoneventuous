using CursoAgentes.Domain.Incidents;
using CursoAgentes.Engine.Incidents;
using CursoAgentes.MiyuAgents;
using CursoAgentes.Tests.Testing;
using Eventuous;
using MiyuAgents.Examples.FixedNodeWorkflow;
using MiyuAgents.Examples.IncidentResponse;
using MiyuAgents.Examples.RoutingWorkflow;
using DomainActions = CursoAgentes.Domain.Incidents.IncidentActions;
using EventuousActionPort = CursoAgentes.Engine.Incidents.InMemoryIncidentActionPort;

namespace CursoAgentes.Tests;

public sealed class MiyuEventuousBridgeTests
{
    public MiyuEventuousBridgeTests() => EventTypes.EnsureRegistered();

    [Fact]
    public async Task PipelineAndFixedNodes_MapToTheSameEventuousInputWithoutExecutingTheirAction()
    {
        var pipelineScenario = IncidentPipelineScenario.CreateOfflineForEventSourcing();
        var nodeScenario = IncidentNodeWorkflowScenario.CreateOfflineForEventSourcing();

        var pipelineRun = await pipelineScenario.RunAsync("map-1", "inspect payments");
        var nodeRun = await nodeScenario.RunAsync("map-1", "inspect payments");
        var pipelineInput = MiyuIncidentInvestigationMapper.FromPipeline("map-1", pipelineRun);
        var nodeInput = MiyuIncidentInvestigationMapper.FromFixedNodes("map-1", nodeRun);

        Assert.False(pipelineRun.Result.WasAborted);
        Assert.Equal(7, pipelineRun.Result.StageHistory.Count);
        Assert.DoesNotContain(IncidentPipelineKeys.Receipt, pipelineRun.Pipeline.SharedData.Keys);
        Assert.DoesNotContain(
            nodeRun.Result.Artifacts,
            artifact => artifact.Kind == IncidentArtifactKinds.Receipt);
        Assert.Equal(0, pipelineScenario.ActionPort.ExecutionCount);
        Assert.Equal(0, nodeScenario.ActionPort.ExecutionCount);
        AssertEquivalent(pipelineInput, nodeInput);
    }

    [Fact]
    public async Task BothOrchestrations_PersistTheSameLifecycleAndOnlyEventuousExecutesEffects()
    {
        var store = new InMemoryEventStore();
        var commands = new IncidentInvestigationCommandService(store);
        var eventuousActionPort = new EventuousActionPort();
        var process = new IncidentInvestigationProcess(commands);
        var reaction = new IncidentActionHandler(commands, eventuousActionPort);
        var bridge = new MiyuIncidentInvestigationBridge(process);

        var pipeline = await bridge.RunPipelineAsync("bridge-pipeline", "inspect payments");
        var nodes = await bridge.RunFixedNodesAsync("bridge-nodes", "inspect payments");

        Assert.Equal(0, pipeline.MiyuActionExecutions);
        Assert.Equal(0, nodes.MiyuActionExecutions);
        Assert.Equal(0, eventuousActionPort.ExecutionCount);
        Assert.Equal(IncidentInvestigationStatus.ActionDecided, pipeline.Eventuous.State.Status);
        Assert.Equal(IncidentInvestigationStatus.ActionDecided, nodes.Eventuous.State.Status);
        Assert.Equal(5, await EventCount(store, "bridge-pipeline"));
        Assert.Equal(5, await EventCount(store, "bridge-nodes"));

        var pipelineHandled = await reaction.HandleAsync(DecisionEvent(pipeline.Eventuous.State));
        var nodesHandled = await reaction.HandleAsync(DecisionEvent(nodes.Eventuous.State));

        Assert.Equal(2, eventuousActionPort.ExecutionCount);
        Assert.Equal(IncidentInvestigationStatus.Completed, pipelineHandled.State.Status);
        Assert.Equal(IncidentInvestigationStatus.Completed, nodesHandled.State.Status);
        Assert.Equal(DomainActions.OpenP1, pipeline.Eventuous.State.Decision?.Action);
        Assert.Equal(DomainActions.OpenP1, nodes.Eventuous.State.Decision?.Action);
        Assert.Equal(
            pipeline.Eventuous.State.Decision?.Reasons,
            nodes.Eventuous.State.Decision?.Reasons);
        Assert.Equal(6, await EventCount(store, "bridge-pipeline"));
        Assert.Equal(6, await EventCount(store, "bridge-nodes"));
        AssertEquivalent(
            pipeline.Input with { InvestigationId = "same" },
            nodes.Input with { InvestigationId = "same" });
    }

    [Fact]
    public async Task AbortedMiyuRun_IsRejectedBeforeAnyEventIsPersisted()
    {
        var scenario = IncidentPipelineScenario.CreateOfflineForEventSourcing(
            analystResponse: """
                {"summary":"No evidence.","severity":"high","evidence":[]}
                """);
        var run = await scenario.RunAsync("bridge-invalid", "inspect payments");

        var error = Assert.Throws<InvalidOperationException>(() =>
            MiyuIncidentInvestigationMapper.FromPipeline("bridge-invalid", run));

        Assert.True(run.Result.WasAborted);
        Assert.Contains("aborted", error.Message);
        Assert.Equal(0, scenario.ActionPort.ExecutionCount);
    }

    static async Task<int> EventCount(InMemoryEventStore store, string investigationId) =>
        (await store.ReadEvents(
            new StreamName($"incident-investigation-{investigationId}"),
            StreamReadPosition.Start,
            int.MaxValue,
            fromEnd: false,
            CancellationToken.None)).Length;

    static IncidentInvestigationEvents.V1.IncidentActionDecided DecisionEvent(
        IncidentInvestigationState state) => new(
            state.InvestigationId,
            state.Decision!,
            DateTimeOffset.UtcNow.ToString("O"));

    static void AssertEquivalent(
        IncidentInvestigationInput expected,
        IncidentInvestigationInput actual)
    {
        Assert.Equal(expected.InvestigationId, actual.InvestigationId);
        Assert.Equal(expected.Service, actual.Service);
        Assert.Equal(expected.Signal, actual.Signal);
        Assert.Equal(expected.Analysis.Summary, actual.Analysis.Summary);
        Assert.Equal(expected.Analysis.Severity, actual.Analysis.Severity);
        Assert.Equal(expected.Analysis.Evidence, actual.Analysis.Evidence);
        Assert.Equal(expected.Analysis.Route, actual.Analysis.Route);
        Assert.Equal(expected.Critique.Verdict, actual.Critique.Verdict);
        Assert.Equal(expected.Critique.Confidence, actual.Critique.Confidence);
        Assert.Equal(expected.Critique.Issues, actual.Critique.Issues);
        Assert.Equal(expected.Critique.Route, actual.Critique.Route);
    }
}
