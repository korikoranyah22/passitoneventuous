using CursoAgentes.Engine.Coordination;

namespace CursoAgentes.Api;

public sealed record WorkflowExecutionLeaseOptions
{
    public bool Enabled { get; init; } = true;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RenewInterval { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed record ExecutionLeaseOwner(string OwnerId);

public interface IWorkflowRunExecutor
{
    /// <returns>
    /// true si esta instancia adquirió el derecho de ejecutar; false si otro
    /// propietario conserva un lease vigente.
    /// </returns>
    Task<bool> TryExecuteAsync(string runId, CancellationToken ct);
}

public sealed class WorkflowExecutionLeaseLostException(string runId)
    : Exception($"Execution lease was lost while running workflow '{runId}'.");

/// <summary>
/// Adapta el lease genérico a runs del workflow. Mantiene la renovación en
/// paralelo y cancela cooperativamente el runner si deja de ser propietario.
/// </summary>
public sealed class WorkflowRunLeaseExecutor(
    IWorkflowRunResumer resumer,
    IExecutionLeaseStore leases,
    ExecutionLeaseOwner owner,
    WorkflowExecutionLeaseOptions options,
    ILogger<WorkflowRunLeaseExecutor> logger) : IWorkflowRunExecutor
{
    public async Task<bool> TryExecuteAsync(string runId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ValidateOptions();

        if (!options.Enabled)
        {
            await resumer.ResumeAsync(runId, ct);
            return true;
        }

        var lease = await leases.TryAcquireAsync(
            ResourceId(runId),
            owner.OwnerId,
            options.Duration,
            ct);
        if (lease is null) return false;

        logger.LogInformation(
            "Lease {LeaseToken} adquirido para {RunId} por {OwnerId}.",
            lease.LeaseToken,
            runId,
            owner.OwnerId);

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var leaseLost = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalTask = KeepRenewingAsync(
            runId,
            lease,
            execution,
            leaseLost,
            renewal.Token);

        try
        {
            await resumer.ResumeAsync(runId, execution.Token);
            if (leaseLost.Task.IsCompleted)
                throw new WorkflowExecutionLeaseLostException(runId);
            return true;
        }
        catch (OperationCanceledException) when (
            leaseLost.Task.IsCompleted && !ct.IsCancellationRequested)
        {
            throw new WorkflowExecutionLeaseLostException(runId);
        }
        finally
        {
            await renewal.CancelAsync();
            try
            {
                await renewalTask;
            }
            catch (OperationCanceledException) when (renewal.IsCancellationRequested)
            {
                // Fin normal de la tarea de renovación.
            }

            try
            {
                using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (await leases.ReleaseAsync(lease, releaseTimeout.Token))
                {
                    logger.LogInformation(
                        "Lease {LeaseToken} liberado para {RunId}.",
                        lease.LeaseToken,
                        runId);
                }
            }
            catch (Exception ex)
            {
                // Si liberar falla, el lease vence solo. No ocultamos la falla
                // principal ni prolongamos el shutdown indefinidamente.
                logger.LogWarning(ex, "No se pudo liberar el lease de {RunId}; vencerá solo.", runId);
            }
        }
    }

    public static string ResourceId(string runId) => $"workflow-run:{runId}";

    private async Task KeepRenewingAsync(
        string runId,
        ExecutionLease lease,
        CancellationTokenSource execution,
        TaskCompletionSource leaseLost,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.RenewInterval, ct);
                var renewed = await leases.RenewAsync(lease, options.Duration, ct);
                if (renewed is not null) continue;

                logger.LogWarning(
                    "Se perdió el lease {LeaseToken} de {RunId}; se cancela la ejecución local.",
                    lease.LeaseToken,
                    runId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Falló la renovación del lease de {RunId}; se cancela por seguridad.",
                    runId);
            }

            leaseLost.TrySetResult();
            await execution.CancelAsync();
            return;
        }
    }

    private void ValidateOptions()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.OwnerId);
        if (!options.Enabled) return;
        if (options.Duration <= TimeSpan.Zero)
            throw new InvalidOperationException("WorkflowExecutionLease:Duration must be positive.");
        if (options.RenewInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("WorkflowExecutionLease:RenewInterval must be positive.");
        if (options.RenewInterval >= options.Duration)
        {
            throw new InvalidOperationException(
                "WorkflowExecutionLease:RenewInterval must be shorter than Duration.");
        }
    }
}
