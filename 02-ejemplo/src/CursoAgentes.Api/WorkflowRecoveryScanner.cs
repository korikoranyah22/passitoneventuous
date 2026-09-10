using CursoAgentes.Domain.Workflow;
using CursoAgentes.Engine.Workflow;
using CursoAgentes.Infrastructure.Projections;
using CursoAgentes.Engine.Coordination;

namespace CursoAgentes.Api;

public sealed record WorkflowRecoveryOptions
{
    public bool Enabled { get; init; } = true;
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(2);
}

public interface IWorkflowRecoverySource
{
    Task<IReadOnlyList<string>> FindPendingAsync(CancellationToken ct);
}

/// <summary>
/// El read model encuentra candidatos eficientemente; cada candidato se
/// confirma contra su stream antes de despacharlo.
/// </summary>
public sealed class WorkflowRecoverySource(
    WorkflowReadModelStore readModel,
    WorkflowExecutionReader reader,
    IExecutionLeaseStore leases) : IWorkflowRecoverySource
{
    public async Task<IReadOnlyList<string>> FindPendingAsync(CancellationToken ct)
    {
        var candidates = await readModel.GetRequestedRunningRunIdsAsync(ct);
        var pending = new List<string>();
        foreach (var runId in candidates)
        {
            // Sólo reduce churn entre scanners. La adquisición atómica en el
            // worker sigue siendo la decisión autoritativa.
            if (await leases.GetActiveAsync(WorkflowRunLeaseExecutor.ResourceId(runId), ct)
                is not null)
            {
                continue;
            }
            var run = await reader.ReadRunAsync(runId, ct);
            if (run is { Status: WorkflowRunStatus.Running, ExecutionRequested: true })
                pending.Add(runId);
        }
        return pending;
    }
}

/// <summary>
/// Recupera entregas locales perdidas. No solicita ejecución para runs
/// deliberadamente diferidos: sólo reencola los que ya tienen el evento
/// WorkflowRunExecutionRequested.
/// </summary>
public sealed class WorkflowRecoveryScanner(
    IWorkflowRecoverySource source,
    WorkflowRunQueue queue,
    WorkflowRecoveryOptions options,
    ILogger<WorkflowRecoveryScanner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Workflow recovery scanner deshabilitado.");
            return;
        }
        if (options.ScanInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("WorkflowRecovery:ScanInterval must be positive.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pending = await source.FindPendingAsync(stoppingToken);
                foreach (var runId in pending)
                {
                    if (await queue.ScheduleAsync(runId, stoppingToken))
                        logger.LogDebug("Workflow {RunId} candidato a recuperación.", runId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falló el scan de recuperación; se reintentará.");
            }

            try
            {
                await Task.Delay(options.ScanInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
