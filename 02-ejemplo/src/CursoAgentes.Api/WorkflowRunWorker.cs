using CursoAgentes.Engine.Workflow;

namespace CursoAgentes.Api;

public interface IWorkflowRunResumer
{
    Task ResumeAsync(string runId, CancellationToken ct);
}

public sealed class WorkflowRunResumer(RecursiveWorkflowRunner runner) : IWorkflowRunResumer
{
    public async Task ResumeAsync(string runId, CancellationToken ct) =>
        await runner.ResumeAsync(runId, ct);
}

/// <summary>
/// Ejecuta fuera del request HTTP. Un cierre ordenado cancela el runner y lo
/// deja Running; una llamada posterior a resume continúa desde sus eventos.
/// </summary>
public sealed class WorkflowRunWorker(
    WorkflowRunQueue queue,
    IWorkflowRunExecutor executor,
    ILogger<WorkflowRunWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var runId in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    logger.LogDebug("Procesando entrega local de workflow {RunId}.", runId);
                    if (!await executor.TryExecuteAsync(runId, stoppingToken))
                    {
                        logger.LogDebug(
                            "Workflow {RunId} omitido: otra instancia conserva el lease.",
                            runId);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    logger.LogInformation(
                        "Worker detenido durante {RunId}; el run puede reanudarse.",
                        runId);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Falló la ejecución en background de {RunId}.", runId);
                }
                finally
                {
                    queue.MarkFinished(runId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Cancelación normal mientras el channel estaba vacío o el run se detenía.
        }
    }
}
