using MiyuAgents.Workflows;
using PassItOn.ElectionAudit.FixedNodes;
using PassItOn.ElectionAudit.Pipeline;
using PassItOn.ElectionAudit.Recursive;
using PassItOn.ElectionAudit.Shared;

namespace PassItOn.ElectionAudit.Tests;

public sealed class SharedDomainTests
{
    [Fact]
    public async Task Synthetic_observations_produce_reproducible_defensive_findings()
    {
        var bundle = await ElectionAuditFixture.CreateCollector().CollectAsync(CancellationToken.None);

        var signal = ElectionAuditDetector.Inspect(bundle);

        Assert.Equal(new DateOnly(2027, 10, 24), signal.GeneralElectionDate);
        Assert.Equal(CalendarAuthority.ProjectedFromCurrentLaw, signal.CalendarAuthority);
        Assert.Equal("paper-single-ballot", bundle.Calendar.VotingInstrument);
        Assert.Equal(3, bundle.Calendar.Contests.Count);
        Assert.Contains(bundle.Calendar.Contests, contest =>
            contest.Office == "president-and-vice-president");
        Assert.Equal(7, signal.Findings.Count);
        Assert.Contains(signal.Findings, item =>
            item.Category == AuditFindingCategories.ReleaseIntegrity);
        Assert.Contains(signal.Findings, item =>
            item.Category == AuditFindingCategories.TransmissionIntegrity);
        Assert.Contains(signal.Findings, item =>
            item.Category == AuditFindingCategories.PublicationMonotonicity);
        Assert.All(signal.Findings, item => Assert.NotEmpty(item.EvidenceIds));
    }

    [Fact]
    public async Task Assessment_gate_rejects_incomplete_evidence_citations()
    {
        var signal = await SignalAsync();
        var incomplete = new AuditAssessment(
            "A hypothesis that cites only one finding.",
            ["A control may have failed."],
            0.8,
            [signal.Findings[0].Id],
            []);

        var exception = Assert.Throws<ElectionAuditContractException>(() =>
            ElectionAuditContracts.Validate(signal, incomplete));

        Assert.Contains("does not cite finding", exception.Message);
    }

    [Fact]
    public async Task Critique_gate_rejects_unknown_evidence_citations()
    {
        var signal = await SignalAsync();
        var critique = new AuditCritique(
            "supported",
            0.9,
            [],
            [],
            signal.Findings.Select(item => item.Id).Append("invented-finding").ToArray());

        var exception = Assert.Throws<ElectionAuditContractException>(() =>
            ElectionAuditContracts.Validate(signal, critique));

        Assert.Contains("unknown finding", exception.Message);
    }

    [Fact]
    public async Task Versioned_policy_cannot_mutate_electoral_systems()
    {
        var signal = await SignalAsync();
        var critique = new AuditCritique(
            "supported",
            0.91,
            [],
            [],
            signal.Findings.Select(item => item.Id).ToArray());

        var decision = ElectionAuditPolicy.Decide(signal, critique);

        Assert.Equal("OPEN_URGENT_HUMAN_REVIEW", decision.Action);
        Assert.True(decision.RequiresHumanApproval);
        Assert.False(decision.AutomaticMutationAllowed);
        Assert.Contains("not definitive scrutiny", decision.LegalScope);
    }

    private static async Task<ElectionAuditSignal> SignalAsync() =>
        ElectionAuditDetector.Inspect(
            await ElectionAuditFixture.CreateCollector().CollectAsync(CancellationToken.None));
}

public sealed class ControlStyleTests
{
    [Fact]
    public async Task Normal_pipeline_runs_each_stage_once_and_replay_is_idempotent()
    {
        var scenario = CompletePipelineScenario.CreateOffline();

        var first = await scenario.RunAsync("pipeline-test");
        var replay = await scenario.RunAsync("pipeline-test");

        Assert.False(first.Turn.WasAborted);
        Assert.Equal(8, first.Turn.StageHistory.Count);
        Assert.Equal(1, scenario.Cases.CreationCount);
        var decision = Assert.IsType<AuditDecision>(
            first.Context.SharedData[ElectionAuditArtifacts.Decision]);
        var receipt = Assert.IsType<HumanReviewReceipt>(
            replay.Context.SharedData[ElectionAuditArtifacts.Receipt]);
        Assert.False(decision.AutomaticMutationAllowed);
        Assert.True(receipt.WasAlreadyCreated);
    }

    [Fact]
    public async Task Fixed_node_workflow_composes_the_same_complete_flow_without_recursion()
    {
        var scenario = CompleteFixedNodeScenario.CreateOffline();

        var first = await scenario.RunAsync("fixed-test");
        var replay = await scenario.RunAsync("fixed-test");

        Assert.Equal(NodeSignal.Done, first.Signal);
        Assert.DoesNotContain(first.Artifacts, item =>
            item.Kind == ElectionAuditArtifacts.RecursionAudit);
        Assert.Equal(1, scenario.Cases.CreationCount);
        var decision = Artifact<AuditDecision>(first, ElectionAuditArtifacts.Decision);
        var receipt = Artifact<HumanReviewReceipt>(replay, ElectionAuditArtifacts.Receipt);
        Assert.Equal("OPEN_URGENT_HUMAN_REVIEW", decision.Action);
        Assert.False(decision.AutomaticMutationAllowed);
        Assert.True(receipt.WasAlreadyCreated);
    }

    [Fact]
    public async Task Recursive_workflow_refines_three_bounded_objectives_then_stops()
    {
        var scenario = CompleteRecursiveScenario.CreateOffline();

        var result = await scenario.RunAsync("recursive-test");

        Assert.Equal(NodeSignal.Done, result.Signal);
        var audits = result.Artifacts
            .Where(item => item.Kind == ElectionAuditArtifacts.RecursionAudit)
            .Select(item => Assert.IsType<RecursionAudit>(item.Payload))
            .ToDictionary(item => item.Objective, item => item.Refinements);
        Assert.Equal(6, audits["complete-read-only-feed-set"]);
        Assert.Equal(2, audits["grounded-assessment"]);
        Assert.Equal(2, audits["supported-independent-critique"]);

        var decision = Artifact<AuditDecision>(result, ElectionAuditArtifacts.Decision);
        Assert.False(decision.AutomaticMutationAllowed);
        Assert.Equal(1, scenario.Cases.CreationCount);
    }

    private static T Artifact<T>(NodeResult result, string kind) =>
        Assert.IsType<T>(result.Artifacts.Single(item => item.Kind == kind).Payload);
}
