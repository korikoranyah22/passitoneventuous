using CursoAgentes.Domain.Workflow;

namespace CursoAgentes.Engine.Workflow;

/// <summary>
/// Persiste la intención de ejecutar un run. No agenda threads ni conoce al
/// host: una API, consola o proceso distribuido decide cómo despachar después.
/// </summary>
public sealed class WorkflowExecutionRequestService(WorkflowRunCommandService commands)
{
    public async Task<WorkflowRunState> RequestAsync(
        string runId,
        string requestId,
        CancellationToken ct)
    {
        var result = await commands.Handle(
            new RequestWorkflowRunExecution(runId, requestId),
            ct);
        if (result.TryGet(out var ok)) return ok.State;
        throw new InvalidOperationException(
            $"Execution request rejected: {result.Exception?.Message ?? "(no detail)"}");
    }
}
