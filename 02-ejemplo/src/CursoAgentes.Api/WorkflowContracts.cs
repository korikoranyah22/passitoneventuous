namespace CursoAgentes.Api;

public sealed record StartWorkflowRequest(string Goal, bool StartImmediately = true);

public sealed record WorkflowAcceptedResponse(
    string RunId,
    string RootNodeId,
    string Status,
    bool WasCreated,
    bool ExecutionRequested,
    bool Scheduled,
    string StatusUrl,
    string AuditUrl);

public sealed record WorkflowResumeResponse(
    string RunId,
    string Status,
    bool ExecutionRequested,
    bool Scheduled,
    string StatusUrl);

public sealed record WorkflowNodeResponse(
    string NodeId,
    string Goal,
    int Depth,
    int Order,
    string Status,
    bool IsLeaf,
    string Rationale,
    string? Answer,
    IReadOnlyList<WorkflowNodeResponse> Children);

public sealed record WorkflowStatusResponse(
    string RunId,
    string Goal,
    string RootNodeId,
    string Status,
    string? Answer,
    bool ExecutionRequested,
    bool ExecutionQueued,
    string? ProjectedStatus,
    bool TreeProjectionAvailable,
    WorkflowNodeResponse? Root);
