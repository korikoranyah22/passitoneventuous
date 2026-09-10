using Microsoft.Extensions.Logging.Abstractions;
using MiyuAgents.Core;
using MiyuAgents.Workflows;

namespace PassItOn.ElectionAudit.Shared;

/// <summary>
/// Domain-neutral adapter between an electoral audit step and MiyuAgents' node contract.
/// Concrete nodes remain responsible for their own deterministic or non-deterministic work.
/// </summary>
public abstract class ElectionAuditNodeBase(string id, string name)
    : AgentBase<NodeResult>(NullLogger<AgentBase<NodeResult>>.Instance), INodeAgent
{
    public override string AgentId { get; } = id;
    public override string AgentName { get; } = name;
    public override AgentRole Role => AgentRole.Orchestration;

    public abstract Task<NodeResult> RunNodeAsync(
        NodeState state,
        CancellationToken ct = default);

    protected override async Task<NodeResult?> ExecuteCoreAsync(
        AgentContext ctx,
        CancellationToken ct) =>
        await RunNodeAsync(new NodeState { Input = ctx.UserMessage, Context = ctx }, ct);

    protected NodeResult Done(params Artifact[] artifacts) =>
        ElectionAuditNodeResults.Done(AgentId, AgentName, artifacts);

    protected NodeResult Failed(Exception exception) =>
        ElectionAuditNodeResults.Failed(AgentId, AgentName, exception.Message);

    protected Artifact Output(string kind, object payload, string? name = null) =>
        new(kind, name ?? AgentId, payload);
}

public static class ElectionAuditNodeResults
{
    public static NodeResult Done(
        string agentId,
        string agentName,
        params Artifact[] artifacts) =>
        NodeResult.From(
            new AgentResponse
            {
                AgentId = agentId,
                AgentName = agentName,
                Role = AgentRole.Orchestration,
                Data = artifacts.LastOrDefault()?.Payload,
                Status = AgentStatus.Ok
            },
            NodeSignal.Done,
            artifacts);

    public static NodeResult Failed(
        string agentId,
        string agentName,
        string message) =>
        NodeResult.From(
            new AgentResponse
            {
                AgentId = agentId,
                AgentName = agentName,
                Role = AgentRole.Orchestration,
                Status = AgentStatus.Error,
                ErrorMessage = message
            },
            NodeSignal.Failed);

    public static T Required<T>(NodeState state, string kind)
    {
        var payload = state.History
            .SelectMany(result => result.Artifacts)
            .LastOrDefault(artifact => artifact.Kind == kind)
            ?.Payload;
        return payload is T value
            ? value
            : throw new ElectionAuditContractException(
                $"required artifact '{kind}' was not produced");
    }
}
