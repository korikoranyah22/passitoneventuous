using MiyuAgents.Workflows;
using PassItOn.ElectionAudit.Recursive;
using PassItOn.ElectionAudit.Shared;

var scenario = CompleteRecursiveScenario.CreateOffline();
var result = await scenario.RunAsync("recursive-2027-demo");
var signal = result.Artifacts
    .Single(item => item.Kind == ElectionAuditArtifacts.Signal)
    .Payload as ElectionAuditSignal;
var decision = result.Artifacts
    .Single(item => item.Kind == ElectionAuditArtifacts.Decision)
    .Payload as AuditDecision;
var audits = result.Artifacts
    .Where(item => item.Kind == ElectionAuditArtifacts.RecursionAudit)
    .Select(item => (RecursionAudit)item.Payload!)
    .ToArray();

Console.WriteLine("COMPLETE RECURSIVE OBJECTIVE WORKFLOW");
Console.WriteLine($"Signal: {result.Signal}");
Console.WriteLine($"Calendar: {signal!.GeneralElectionDate:yyyy-MM-dd} ({signal.CalendarAuthority})");
Console.WriteLine($"Findings: {signal.Findings.Count}");
foreach (var audit in audits)
    Console.WriteLine($"Recursive objective: {audit.Objective}; refinements: {audit.Refinements}");
Console.WriteLine($"Decision: {decision!.Action}");
Console.WriteLine($"Automatic mutation: {decision.AutomaticMutationAllowed}");
Console.WriteLine($"Review cases created: {scenario.Cases.CreationCount}");
