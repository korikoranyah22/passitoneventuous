using System.Security.Cryptography;
using System.Text;
using CursoAgentes.Domain.Workflow;
using CursoAgentes.Engine.Workflow;

namespace CursoAgentes.Api;

public interface IWorkflowAcceptanceStore
{
    Task<WorkflowRunState?> ReadAsync(string runId, CancellationToken ct);
    Task<WorkflowRunHandle> StartAsync(string runId, string goal, CancellationToken ct);
    Task<WorkflowRunState> RequestExecutionAsync(
        string runId,
        string requestId,
        CancellationToken ct);
}

public sealed class EventSourcedWorkflowAcceptanceStore(
    WorkflowExecutionReader reader,
    RecursiveWorkflowRunner runner,
    WorkflowExecutionRequestService requests) : IWorkflowAcceptanceStore
{
    public Task<WorkflowRunState?> ReadAsync(string runId, CancellationToken ct) =>
        reader.ReadRunAsync(runId, ct);

    public Task<WorkflowRunHandle> StartAsync(
        string runId,
        string goal,
        CancellationToken ct) => runner.StartAsync(runId, goal, ct);

    public Task<WorkflowRunState> RequestExecutionAsync(
        string runId,
        string requestId,
        CancellationToken ct) => requests.RequestAsync(runId, requestId, ct);
}

public sealed record WorkflowAcceptance(
    WorkflowRunState Run,
    bool WasCreated,
    bool Scheduled);

public sealed class WorkflowAcceptanceConflictException(string message) : Exception(message);

/// <summary>
/// Acepta un run de forma idempotente cuando el cliente envía Idempotency-Key.
/// La clave se transforma en un id estable; no se persiste el valor HTTP crudo.
/// </summary>
public sealed class WorkflowAcceptanceService(
    IWorkflowAcceptanceStore store,
    WorkflowRunQueue queue)
{
    public async Task<WorkflowAcceptance> AcceptAsync(
        string goal,
        bool startImmediately,
        string? idempotencyKey,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        var normalizedGoal = goal.Trim();
        var runId = string.IsNullOrWhiteSpace(idempotencyKey)
            ? $"run-{Guid.NewGuid():N}"
            : WorkflowIdempotency.DeriveRunId(idempotencyKey);

        var run = await store.ReadAsync(runId, ct);
        var wasCreated = false;
        if (run is null)
        {
            try
            {
                var handle = await store.StartAsync(runId, normalizedGoal, ct);
                run = new WorkflowRunState().When(
                    new WorkflowRunEvents.V1.WorkflowRunCreated(
                        handle.RunId,
                        handle.Goal,
                        handle.RootNodeId,
                        DateTime.UtcNow.ToString("O")));
                wasCreated = true;
            }
            catch (InvalidOperationException) when (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                // Dos requests con la misma clave pueden observar el stream
                // inexistente a la vez. El perdedor relee el ganador.
                run = await store.ReadAsync(runId, ct);
                if (run is null) throw;
            }
        }

        if (!string.Equals(run.Goal, normalizedGoal, StringComparison.Ordinal))
        {
            throw new WorkflowAcceptanceConflictException(
                "Idempotency-Key is already associated with a different goal.");
        }

        var scheduled = false;
        if (startImmediately && run.Status == WorkflowRunStatus.Running)
        {
            run = await store.RequestExecutionAsync(
                runId,
                ExecutionRequestId(runId),
                ct);
            scheduled = await queue.ScheduleAsync(runId, CancellationToken.None);
        }

        return new WorkflowAcceptance(run, wasCreated, scheduled);
    }

    public static string ExecutionRequestId(string runId) => $"execute-{runId}";
}

public static class WorkflowIdempotency
{
    public static string DeriveRunId(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var key = idempotencyKey.Trim();
        if (key.Length > 200)
            throw new ArgumentException("Idempotency-Key cannot exceed 200 characters.");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"run-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }
}
