using CursoAgentes.Api;
using CursoAgentes.Domain.Workflow;
using CursoAgentes.Engine.Workflow;
using CursoAgentes.Engine.Coordination;
using CursoAgentes.Infrastructure;
using CursoAgentes.Infrastructure.Projections;
using CursoAgentes.Infrastructure.Workflow;
using Eventuous;

TypeMap.RegisterKnownEventTypes(typeof(WorkflowRunEvents.V1.WorkflowRunCreated).Assembly);

var builder = WebApplication.CreateBuilder(args);

// En Windows, el host web agrega EventLog. Un log interno de Eventuous puede
// requerir privilegios para escribir allí y terminar abortando la suscripción.
// La consola es portable y un warning nunca debe romper el consumo de eventos.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Npgsql", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Eventuous.Subscription", LogLevel.Error);

builder.Services.AddCursoAgentesInfrastructure(builder.Configuration);
builder.Services.AddSingleton<WorkflowRunQueue>();
builder.Services.AddSingleton<IWorkflowRunResumer, WorkflowRunResumer>();
builder.Services.Configure<WorkflowExecutionLeaseOptions>(
    builder.Configuration.GetSection("WorkflowExecutionLease"));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkflowExecutionLeaseOptions>>().Value);
builder.Services.AddSingleton(new ExecutionLeaseOwner(
    $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}"));
builder.Services.AddSingleton<IWorkflowRunExecutor, WorkflowRunLeaseExecutor>();
builder.Services.AddSingleton<IWorkflowAcceptanceStore, EventSourcedWorkflowAcceptanceStore>();
builder.Services.AddSingleton<WorkflowAcceptanceService>();
builder.Services.AddSingleton<WorkflowStatusQuery>();
builder.Services.Configure<WorkflowRecoveryOptions>(
    builder.Configuration.GetSection("WorkflowRecovery"));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkflowRecoveryOptions>>().Value);
builder.Services.AddSingleton<IWorkflowRecoverySource, WorkflowRecoverySource>();
builder.Services.AddHostedService<WorkflowRunWorker>();
builder.Services.AddHostedService<WorkflowRecoveryScanner>();

var app = builder.Build();

// Las tablas deben existir antes de que las suscripciones empiecen a proyectar.
await app.Services.GetRequiredService<WorkflowReadModelStore>()
    .InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<IncidentReadModelStore>()
    .InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<IExecutionLeaseStore>()
    .InitializeAsync(CancellationToken.None);

app.MapGet("/", () => Results.Ok(new
{
    service = "CursoAgentes.Api",
    workflowRuns = "/api/workflow-runs"
}));

app.MapPost("/api/workflow-runs", async (
    StartWorkflowRequest request,
    HttpRequest httpRequest,
    WorkflowAcceptanceService acceptanceService,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Goal))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.Goal)] = ["Goal is required."]
        });
    }

    var keyValues = httpRequest.Headers["Idempotency-Key"];
    if (keyValues.Count > 1 || (keyValues.Count == 1 && string.IsNullOrWhiteSpace(keyValues[0])))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["Idempotency-Key"] = ["Provide at most one non-empty Idempotency-Key header."]
        });
    }

    try
    {
        var accepted = await acceptanceService.AcceptAsync(
            request.Goal,
            request.StartImmediately,
            keyValues.Count == 1 ? keyValues[0] : null,
            ct);
        var run = accepted.Run;
        var statusUrl = $"/api/workflow-runs/{run.RunId}";
        return Results.Accepted(statusUrl, new WorkflowAcceptedResponse(
            run.RunId,
            run.RootNodeId,
            run.Status.ToString(),
            accepted.WasCreated,
            run.ExecutionRequested,
            accepted.Scheduled,
            statusUrl,
            $"{statusUrl}/audit"));
    }
    catch (ArgumentException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["Idempotency-Key"] = [ex.Message]
        });
    }
    catch (WorkflowAcceptanceConflictException ex)
    {
        return Results.Conflict(new { detail = ex.Message });
    }
});

app.MapGet("/api/workflow-runs/{runId}", async (
    string runId,
    WorkflowStatusQuery query,
    CancellationToken ct) =>
{
    var response = await query.GetAsync(runId, ct);
    return response is null ? Results.NotFound() : Results.Ok(response);
});

app.MapGet("/api/workflow-runs/{runId}/audit", async (
    string runId,
    WorkflowExecutionReader reader,
    WorkflowAuditStore audit,
    CancellationToken ct) =>
{
    if (await reader.ReadRunAsync(runId, ct) is null) return Results.NotFound();
    return Results.Ok(await audit.GetAsync(runId, ct));
});

app.MapPost("/api/workflow-runs/{runId}/resume", async (
    string runId,
    WorkflowExecutionReader reader,
    WorkflowExecutionRequestService requests,
    WorkflowRunQueue queue,
    CancellationToken ct) =>
{
    var run = await reader.ReadRunAsync(runId, ct);
    if (run is null) return Results.NotFound();
    if (run.Status == WorkflowRunStatus.Failed)
    {
        return Results.Conflict(new
        {
            runId,
            status = run.Status.ToString(),
            detail = "Failed runs require an explicit recovery policy."
        });
    }

    var statusUrl = $"/api/workflow-runs/{runId}";
    if (run.Status == WorkflowRunStatus.Completed)
    {
        return Results.Ok(new WorkflowResumeResponse(
            runId,
            run.Status.ToString(),
            run.ExecutionRequested,
            Scheduled: false,
            statusUrl));
    }

    if (!run.ExecutionRequested)
    {
        run = await requests.RequestAsync(
            runId,
            WorkflowAcceptanceService.ExecutionRequestId(runId),
            ct);
    }
    var scheduled = await queue.ScheduleAsync(runId, CancellationToken.None);
    return Results.Accepted(statusUrl, new WorkflowResumeResponse(
        runId,
        run.Status.ToString(),
        run.ExecutionRequested,
        scheduled,
        statusUrl));
});

await app.RunAsync();

public partial class Program;
