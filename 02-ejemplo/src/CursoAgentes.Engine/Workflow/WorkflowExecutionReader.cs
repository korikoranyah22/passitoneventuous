using CursoAgentes.Domain.Workflow;
using Eventuous;

namespace CursoAgentes.Engine.Workflow;

/// <summary>
/// Reconstruye runs y nodos directamente desde sus streams. El motor de
/// reanudación usa la historia como checkpoint; nunca depende del read model,
/// que puede estar temporalmente atrasado.
/// </summary>
public sealed class WorkflowExecutionReader(IEventStore store)
{
    public async Task<WorkflowRunState?> ReadRunAsync(
        string runId,
        CancellationToken ct = default)
    {
        var events = await store.ReadEvents(
            new StreamName($"workflow-run-{runId}"),
            StreamReadPosition.Start,
            int.MaxValue,
            false,
            ct);
        if (events.Length == 0) return null;

        var state = new WorkflowRunState();
        foreach (var @event in events) state = Apply(state, @event.Payload);
        return state;
    }

    public async Task<WorkflowNodeState?> ReadNodeAsync(
        string nodeId,
        CancellationToken ct = default)
    {
        var events = await store.ReadEvents(
            new StreamName($"workflow-node-{nodeId}"),
            StreamReadPosition.Start,
            int.MaxValue,
            false,
            ct);
        if (events.Length == 0) return null;

        var state = new WorkflowNodeState();
        foreach (var @event in events) state = Apply(state, @event.Payload);
        return state;
    }

    static WorkflowRunState Apply(WorkflowRunState state, object? payload) =>
        payload switch
        {
            WorkflowRunEvents.V1.WorkflowRunCreated e => state.When(e),
            WorkflowRunEvents.V1.WorkflowRunExecutionRequested e => state.When(e),
            WorkflowRunEvents.V1.WorkflowRunCompleted e => state.When(e),
            WorkflowRunEvents.V1.WorkflowRunFailed e => state.When(e),
            null => throw new InvalidOperationException("Workflow run stream contains a null event."),
            _ => throw new InvalidOperationException(
                $"Unexpected event '{payload.GetType().Name}' in workflow run stream."),
        };

    static WorkflowNodeState Apply(WorkflowNodeState state, object? payload) =>
        payload switch
        {
            WorkflowNodeEvents.V1.WorkflowNodeCreated e => state.When(e),
            WorkflowNodeEvents.V1.WorkflowNodePlanned e => state.When(e),
            WorkflowNodeEvents.V1.WorkflowNodeCompleted e => state.When(e),
            WorkflowNodeEvents.V1.WorkflowNodeFailed e => state.When(e),
            null => throw new InvalidOperationException("Workflow node stream contains a null event."),
            _ => throw new InvalidOperationException(
                $"Unexpected event '{payload.GetType().Name}' in workflow node stream."),
        };
}
