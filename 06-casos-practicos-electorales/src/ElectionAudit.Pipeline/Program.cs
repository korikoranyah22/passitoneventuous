using PassItOn.ElectionAudit.Pipeline;
using PassItOn.ElectionAudit.Shared;

var scenario = CompletePipelineScenario.CreateOffline();
var first = await scenario.RunAsync("pipeline-2027-demo");
var replay = await scenario.RunAsync("pipeline-2027-demo");
var signal = (ElectionAuditSignal)first.Context.SharedData[ElectionAuditArtifacts.Signal];
var decision = (AuditDecision)first.Context.SharedData[ElectionAuditArtifacts.Decision];
var receipt = (HumanReviewReceipt)replay.Context.SharedData[ElectionAuditArtifacts.Receipt];

Console.WriteLine("COMPLETE NORMAL PIPELINE");
Console.WriteLine($"Calendar: {signal.GeneralElectionDate:yyyy-MM-dd} ({signal.CalendarAuthority})");
Console.WriteLine($"Findings: {signal.Findings.Count}");
Console.WriteLine($"Decision: {decision.Action}");
Console.WriteLine($"Automatic mutation: {decision.AutomaticMutationAllowed}");
Console.WriteLine($"Review cases created: {scenario.Cases.CreationCount}");
Console.WriteLine($"Replay reused receipt: {receipt.WasAlreadyCreated}");
