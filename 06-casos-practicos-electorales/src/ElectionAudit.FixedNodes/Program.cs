using PassItOn.ElectionAudit.FixedNodes;
using PassItOn.ElectionAudit.Shared;

var scenario = CompleteFixedNodeScenario.CreateOffline();
var first = await scenario.RunAsync("fixed-nodes-2027-demo");
var replay = await scenario.RunAsync("fixed-nodes-2027-demo");
var signal = first.Artifacts
    .Single(item => item.Kind == ElectionAuditArtifacts.Signal)
    .Payload as ElectionAuditSignal;
var decision = first.Artifacts
    .Single(item => item.Kind == ElectionAuditArtifacts.Decision)
    .Payload as AuditDecision;
var receipt = replay.Artifacts
    .Single(item => item.Kind == ElectionAuditArtifacts.Receipt)
    .Payload as HumanReviewReceipt;

Console.WriteLine("COMPLETE FIXED NODE WORKFLOW");
Console.WriteLine($"Signal: {first.Signal}");
Console.WriteLine($"Calendar: {signal!.GeneralElectionDate:yyyy-MM-dd} ({signal.CalendarAuthority})");
Console.WriteLine($"Findings: {signal.Findings.Count}");
Console.WriteLine($"Decision: {decision!.Action}");
Console.WriteLine($"Automatic mutation: {decision.AutomaticMutationAllowed}");
Console.WriteLine($"Review cases created: {scenario.Cases.CreationCount}");
Console.WriteLine($"Replay reused receipt: {receipt!.WasAlreadyCreated}");
