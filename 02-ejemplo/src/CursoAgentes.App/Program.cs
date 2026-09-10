using CursoAgentes.Domain.Incidents;
using CursoAgentes.Domain.Workflow;
using CursoAgentes.Engine.Incidents;
using CursoAgentes.Engine.Workflow;
using CursoAgentes.Infrastructure;
using CursoAgentes.Infrastructure.Projections;
using CursoAgentes.MiyuAgents;
using Eventuous;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

// ─────────────────────────────────────────────────────────────────────────────
// LA DEMO END-TO-END del curso.
//
//  1. Conecta con Postgres (el mismo que usa el event store).
//  2. Arranca el host: Eventuous crea el schema del event store y arranca la
//     suscripción que alimenta el read model.
//  3. Sin argumentos, corre el workflow recursivo. --workflow-start lo deja
//     Pending y --workflow-resume continúa desde eventos. Los modos --incident,
//     --incident-pipeline y --incident-nodes muestran el caso híbrido.
//  4. Imprime los EVENTOS CRUDOS del event store y el read model correspondiente.
//
// Correlo con:  dotnet run --project src/CursoAgentes.App
// (Postgres arriba: docker compose up -d)
// ─────────────────────────────────────────────────────────────────────────────

// Registrar los tipos de eventos en el TypeMap: sin esto Eventuous no puede
// deserializar los eventos guardados en Postgres (el clásico "me guarda pero
// no me lee").
TypeMap.RegisterKnownEventTypes(typeof(WorkflowRunEvents.V1.WorkflowRunCreated).Assembly);

var builder = Host.CreateApplicationBuilder(args);

// La demo debe comportarse igual en Windows, Linux y contenedores. El host de
// Windows agrega EventLog por defecto, pero escribir allí puede requerir
// privilegios elevados y un simple warning no debe detener una suscripción.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Eventuous.Subscription", LogLevel.Error);

// La demo debe poder correrse desde CUALQUIER carpeta (p. ej.
// `dotnet run --project src/CursoAgentes.App` desde la raíz del ejemplo).
// El content root por defecto es el working directory; lo fijamos a la carpeta
// del binario, donde vive appsettings.json (copiado por CopyToOutputDirectory).
builder.Environment.ContentRootPath = AppContext.BaseDirectory;
builder.Configuration.Sources.Clear();
builder.Configuration.SetBasePath(AppContext.BaseDirectory);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args.Where(argument => !IsAppMode(argument)).ToArray());

builder.Services.AddCursoAgentesInfrastructure(builder.Configuration);

using var host = builder.Build();
var configuration = builder.Configuration;

Console.WriteLine();
Console.WriteLine("══════════════════════════════════════════════════════════════");
Console.WriteLine("  CURSO: Event Sourcing + Eventuous + PostgreSQL para Agentes");
Console.WriteLine("  Demo del workflow de nodos recursivos con llamadas a LLM");
Console.WriteLine("══════════════════════════════════════════════════════════════");
Console.WriteLine();

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var ct = cts.Token;

// ── 1. Postgres disponible? ──────────────────────────────────────────────────
var connectionString = configuration.GetConnectionString("EventStore")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:EventStore");
await EnsurePostgresAsync(connectionString, ct);

// ── 2. Preparar read models y arrancar el host ───────────────────────────────
// Las tablas se crean antes de arrancar las suscripciones, para que un replay
// de eventos existentes nunca llegue a una proyección sin destino.
var readModel = host.Services.GetRequiredService<WorkflowReadModelStore>();
var incidentReadModel = host.Services.GetRequiredService<IncidentReadModelStore>();
await readModel.InitializeAsync(ct);
await incidentReadModel.InitializeAsync(ct);
await host.StartAsync(ct);
await WaitForEventStoreObjectsAsync(connectionString, ct);
Console.WriteLine("✔ Postgres listo — event store (curso_eventstore) y read model (curso_readmodel).");
Console.WriteLine();

var incidentMode = args.FirstOrDefault(IsIncidentMode);
if (incidentMode is not null)
{
    if (incidentMode.Equals("--incident-retry", StringComparison.OrdinalIgnoreCase))
    {
        var modeIndex = Array.FindIndex(
            args,
            argument => argument.Equals(incidentMode, StringComparison.OrdinalIgnoreCase));
        var investigationId = modeIndex >= 0 && modeIndex + 1 < args.Length
            ? args[modeIndex + 1]
            : null;
        if (string.IsNullOrWhiteSpace(investigationId)
            || investigationId.StartsWith("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Uso: --incident-retry <investigationId> "
                + "--IncidentRetry:RequestId=<id> "
                + "--IncidentRetry:RequestedBy=<actor> "
                + "--IncidentRetry:Reason=<motivo>");
        }

        await RunIncidentRetryAsync(
            host.Services,
            incidentReadModel,
            connectionString,
            investigationId,
            RequiredRetrySetting(configuration, "RequestId"),
            RequiredRetrySetting(configuration, "RequestedBy"),
            RequiredRetrySetting(configuration, "Reason"),
            ct);
    }
    else
    {
        await RunIncidentDemoAsync(
            host.Services,
            incidentReadModel,
            connectionString,
            incidentMode,
            ct);
    }
    await host.StopAsync(ct);
    return;
}

// ── 3. Correr el workflow ────────────────────────────────────────────────────
var runner = host.Services.GetRequiredService<RecursiveWorkflowRunner>();
var workflowMode = args.FirstOrDefault(IsWorkflowMode);
if (workflowMode?.Equals("--workflow-start", StringComparison.OrdinalIgnoreCase) == true)
{
    var goalToPersist = RequiredArgumentAfter(args, workflowMode, "objetivo");
    var preparedRunId = "run-" + Guid.NewGuid().ToString("N")[..8];
    var handle = await runner.StartAsync(preparedRunId, goalToPersist, ct);
    await WaitForRunProjectionStatusAsync(
        readModel,
        preparedRunId,
        "Running",
        TimeSpan.FromSeconds(10),
        ct);

    Console.WriteLine("⏸ Workflow preparado sin ejecutar agentes");
    Console.WriteLine($"  runId={handle.RunId}");
    Console.WriteLine($"  rootNodeId={handle.RootNodeId}");
    Console.WriteLine($"  objetivo=«{handle.Goal}»");
    Console.WriteLine("  eventos persistidos=2 (run creado + nodo raíz pendiente)");
    Console.WriteLine();
    Console.WriteLine("Para continuar desde otro proceso:");
    Console.WriteLine($"  dotnet run --project src/CursoAgentes.App -- --workflow-resume {handle.RunId}");
    await host.StopAsync(ct);
    return;
}

string runId;
string goal;
WorkflowResult result;
if (workflowMode?.Equals("--workflow-resume", StringComparison.OrdinalIgnoreCase) == true)
{
    runId = RequiredArgumentAfter(args, workflowMode, "runId");
    Console.WriteLine($"▶ Reanudando workflow {runId} desde sus eventos");
    Console.WriteLine("  Los nodos Completed se restauran; sólo avanzan Pending y Planned.");
    Console.WriteLine();
    result = await runner.ResumeAsync(runId, ct);
    goal = result.Root.Goal;
}
else
{
    goal = args.Length > 0
        ? string.Join(" ", args)
        : "¿Por qué los agentes necesitan event sourcing?";
    runId = "run-" + Guid.NewGuid().ToString("N")[..8];
    Console.WriteLine($"▶ Ejecutando workflow para: «{goal}»");
    Console.WriteLine($"  (runId={runId} — cada nodo es un stream workflow-node-* en Postgres)");
    Console.WriteLine();
    result = await runner.RunAsync(runId, goal, ct);
}

// ── 4a. El árbol resultante ──────────────────────────────────────────────────
Console.WriteLine("── ÁRBOL DE NODOS (resultado en memoria) ─────────────────────");
PrintTree(result.Root, "");
Console.WriteLine();
Console.WriteLine($"Total de nodos: {result.NodeCount}");

Console.WriteLine();
Console.WriteLine("── BITÁCORA DEL RUN ───────────────────────────────────────────");
foreach (var step in result.Steps)
    Console.WriteLine($"  • {step}");

Console.WriteLine();
Console.WriteLine($"RESPUESTA FINAL ({result.FinalAnswer.Length} caracteres):");
Console.WriteLine(result.FinalAnswer);
Console.WriteLine();

// ── 4b. La auditoría: los eventos crudos en Postgres ─────────────────────────
// Esto es EL corazón del event sourcing: todo lo que pasó, en orden, como
// eventos inmutables. Si el proceso muriera acá, podríamos reconstruir el
// estado completo releyendo estos streams.
await PrintAuditTrailAsync(connectionString, runId, result, ct);

// ── 4c. El read model (lo que ve una query normal) ───────────────────────────
// Le damos a la proyección un momento para alcanzar el último evento.
await WaitForProjectionAsync(readModel, runId, TimeSpan.FromSeconds(10), ct);
Console.WriteLine("── READ MODEL (curso_readmodel, escrito por la proyección) ─────");
var run = await readModel.GetRunAsync(runId, ct);
if (run is { } r)
{
    Console.WriteLine($"  run={r.RunId}  estado={r.Status}  objetivo=«{r.Goal}»");
    Console.WriteLine($"  respuesta={r.Answer?[..Math.Min(80, r.Answer!.Length)]}…");
}
var nodes = await readModel.GetNodesAsync(runId, ct);
foreach (var (nodeId, depth, isLeaf, status, nodeGoal) in nodes)
{
    var indent = new string(' ', (depth + 1) * 2);
    Console.WriteLine($"{indent}• {nodeId}  profundidad={depth}  hoja={isLeaf}  estado={status}  «{Truncate(nodeGoal, 50)}»");
}
Console.WriteLine();

Console.WriteLine("✅ Demo completa. El workflow quedó persistido en Postgres —");
Console.WriteLine($"   --workflow-resume {runId} reconstruye el mismo árbol sin repetir nodos completos.");
await host.StopAsync(ct);

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

static bool IsAppMode(string argument) =>
    IsIncidentMode(argument) || IsWorkflowMode(argument);

static bool IsIncidentMode(string argument) =>
    argument.Equals("--incident", StringComparison.OrdinalIgnoreCase)
    || argument.Equals("--incident-pipeline", StringComparison.OrdinalIgnoreCase)
    || argument.Equals("--incident-nodes", StringComparison.OrdinalIgnoreCase)
    || argument.Equals("--incident-retry", StringComparison.OrdinalIgnoreCase);

static bool IsWorkflowMode(string argument) =>
    argument.Equals("--workflow-start", StringComparison.OrdinalIgnoreCase)
    || argument.Equals("--workflow-resume", StringComparison.OrdinalIgnoreCase);

static string RequiredArgumentAfter(string[] arguments, string mode, string name)
{
    var index = Array.FindIndex(
        arguments,
        argument => argument.Equals(mode, StringComparison.OrdinalIgnoreCase));
    var value = index >= 0 && index + 1 < arguments.Length
        ? arguments[index + 1]
        : null;
    if (!string.IsNullOrWhiteSpace(value)
        && !value.StartsWith("--", StringComparison.Ordinal))
    {
        return value;
    }
    throw new InvalidOperationException($"Falta {name} después de {mode}.");
}

static string RequiredRetrySetting(IConfiguration configuration, string name)
{
    var value = configuration[$"IncidentRetry:{name}"];
    if (!string.IsNullOrWhiteSpace(value)) return value;
    throw new InvalidOperationException(
        $"Falta --IncidentRetry:{name}=<valor> para auditar el reintento manual.");
}

static async Task EnsurePostgresAsync(string connectionString, CancellationToken ct)
{
    var started = DateTime.UtcNow;
    while (true)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            Console.WriteLine("✔ Conexión a Postgres establecida.");
            return;
        }
        catch (Exception ex) when (DateTime.UtcNow - started < TimeSpan.FromSeconds(30))
        {
            Console.WriteLine($"  ⏳ Esperando a Postgres ({ex.Message.Split('\n')[0]})…");
            await Task.Delay(1000, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("✖ No se pudo conectar a Postgres. Levantalo con:");
            Console.WriteLine("    cd 02-ejemplo && docker compose up -d");
            Console.WriteLine($"  Detalle: {ex.Message}");
            Console.WriteLine();
            throw;
        }
    }
}

/// <summary>Espera a que Eventuous haya creado el tipo compuesto stream_message y la tabla messages.</summary>
static async Task WaitForEventStoreObjectsAsync(string connectionString, CancellationToken ct)
{
    var started = DateTime.UtcNow;
    while (true)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            """
            SELECT
              EXISTS (SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
                      WHERE n.nspname = 'curso_eventstore' AND t.typname = 'stream_message'),
              EXISTS (SELECT 1 FROM information_schema.tables
                      WHERE table_schema = 'curso_eventstore' AND table_name = 'messages');
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        if (reader.GetBoolean(0) && reader.GetBoolean(1))
        {
            Console.WriteLine("✔ Event store inicializado (stream_message + messages).");
            return;
        }

        if (DateTime.UtcNow - started > TimeSpan.FromSeconds(60))
            throw new TimeoutException("Tiempo de espera agotado: Eventuous no creó el schema del event store.");

        await Task.Delay(1000, ct);
    }
}

static async Task PrintAuditTrailAsync(
    string connectionString, string runId, WorkflowResult result, CancellationToken ct)
{
    var streams = new List<string> { $"workflow-run-{runId}" };
    CollectStreams(result.Root, streams);

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync(ct);
    await using var cmd = new NpgsqlCommand(
        """
        SELECT m.global_position, s.stream_name, m.message_type, m.stream_position, m.created, m.json_data::text
          FROM curso_eventstore.messages m
          JOIN curso_eventstore.streams s ON s.stream_id = m.stream_id
         WHERE s.stream_name = ANY(@streams)
         ORDER BY m.global_position
        """, conn);
    cmd.Parameters.AddWithValue("streams", streams.ToArray());

    Console.WriteLine("── AUDITORÍA: eventos crudos del event store (curso_eventstore.messages) ──");
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    var count = 0;
    while (await reader.ReadAsync(ct))
    {
        count++;
        var global = reader.GetInt64(0);
        var stream = reader.GetString(1);
        var type = reader.GetString(2);
        var pos = reader.GetInt64(3);
        var created = reader.GetDateTime(4);
        var payload = reader.GetString(5);

        Console.WriteLine($"  #{global} [{created:HH:mm:ss}] {stream}  {type}  (pos {pos})");
        Console.WriteLine($"      payload: {Truncate(payload, 160)}");
    }
    Console.WriteLine($"  ({count} eventos en el event store para este run)");
    Console.WriteLine();

    static void CollectStreams(NodeResult node, List<string> acc)
    {
        acc.Add($"workflow-node-{node.NodeId}");
        foreach (var child in node.Children) CollectStreams(child, acc);
    }
}

static async Task WaitForProjectionAsync(
    WorkflowReadModelStore readModel, string runId, TimeSpan timeout, CancellationToken ct)
{
    var started = DateTime.UtcNow;
    while (DateTime.UtcNow - started < timeout)
    {
        var run = await readModel.GetRunAsync(runId, ct);
        if (run is { Status: "Completed" or "Failed" })
            return;
        await Task.Delay(200, ct);
    }
    Console.WriteLine("  ⚠ La proyección no alcanzó el estado final a tiempo (¿suscripción detenida?).");
}

static async Task WaitForRunProjectionStatusAsync(
    WorkflowReadModelStore readModel,
    string runId,
    string expectedStatus,
    TimeSpan timeout,
    CancellationToken ct)
{
    var started = DateTime.UtcNow;
    while (DateTime.UtcNow - started < timeout)
    {
        var run = await readModel.GetRunAsync(runId, ct);
        if (run?.Status == expectedStatus) return;
        await Task.Delay(200, ct);
    }
    throw new TimeoutException(
        $"La proyección del workflow no alcanzó {expectedStatus} a tiempo.");
}

static async Task RunIncidentDemoAsync(
    IServiceProvider services,
    IncidentReadModelStore readModel,
    string connectionString,
    string mode,
    CancellationToken ct)
{
    var investigationId = "inv-" + Guid.NewGuid().ToString("N")[..8];
    var process = services.GetRequiredService<IncidentInvestigationProcess>();
    var actionPort = services.GetRequiredService<IIncidentActionPort>();
    IncidentInvestigationRun run;
    string source;
    int? miyuActionExecutions = null;

    if (mode.Equals("--incident-pipeline", StringComparison.OrdinalIgnoreCase))
    {
        var bridged = await new MiyuIncidentInvestigationBridge(process).RunPipelineAsync(
            investigationId,
            "Investigate repeated payment API health-check failures.",
            ct: ct);
        run = bridged.Eventuous;
        source = "MiyuAgents PipelineRunner → Eventuous";
        miyuActionExecutions = bridged.MiyuActionExecutions;
    }
    else if (mode.Equals("--incident-nodes", StringComparison.OrdinalIgnoreCase))
    {
        var bridged = await new MiyuIncidentInvestigationBridge(process).RunFixedNodesAsync(
            investigationId,
            "Investigate repeated payment API health-check failures.",
            ct: ct);
        run = bridged.Eventuous;
        source = "MiyuAgents fixed nodes → Eventuous";
        miyuActionExecutions = bridged.MiyuActionExecutions;
    }
    else
    {
        var collectedAt = DateTimeOffset.UtcNow.ToString("O");
        var input = new IncidentInvestigationInput(
            investigationId,
            "payments-api",
            new IncidentSignal(
                "payments-api",
                TotalChecks: 7,
                FailedChecks: 6,
                AffectedRegions: 2,
                collectedAt),
            new IncidentAnalysis(
                "Payment checks are failing in two regions.",
                "high",
                ["6 failed checks", "2 affected regions"],
                new IncidentRouteAudit(
                    "fast-private-analysis",
                    "local/local-fast",
                    "local",
                    "local-fast",
                    Attempts: 1)),
            new IncidentCritique(
                "confirmed",
                Confidence: 0.92,
                Issues: [],
                new IncidentRouteAudit(
                    "critical-judge",
                    "cloud/cloud-reasoner",
                    "cloud",
                    "cloud-reasoner",
                    Attempts: 1)));
        run = await process.RunAsync(input, ct);
        source = "Eventuous directo";
    }

    Console.WriteLine("▶ Ejecutando investigación de incidente event-sourced");
    Console.WriteLine($"  investigationId={investigationId}");
    Console.WriteLine($"  origen={source}");
    Console.WriteLine($"  action port={actionPort.GetType().Name}");
    if (miyuActionExecutions is not null)
        Console.WriteLine($"  efectos ejecutados por MiyuAgents={miyuActionExecutions} (esperado: 0)");
    Console.WriteLine("  Los artefactos collector/LLM ya llegan estructurados al aggregate.");
    Console.WriteLine();

    Console.WriteLine($"  señal: {run.State.Signal?.FailedChecks}/{run.State.Signal?.TotalChecks} checks fallidos");
    Console.WriteLine($"  analista: {run.State.Analysis?.Route.Model}");
    Console.WriteLine($"  crítico: {run.State.Critique?.Route.Model} ({run.State.Critique?.Confidence:P0})");
    Console.WriteLine($"  política: {run.State.Decision?.PolicyVersion} → {run.State.Decision?.Action}");
    Console.WriteLine($"  frontera transaccional: {run.State.Status}; efecto pendiente de la suscripción");
    Console.WriteLine();

    await WaitForIncidentProjectionAsync(readModel, investigationId, TimeSpan.FromSeconds(10), ct);
    var view = await readModel.GetAsync(investigationId, ct);
    await PrintIncidentAuditTrailAsync(connectionString, investigationId, ct);
    Console.WriteLine("── READ MODEL DEL INCIDENTE ───────────────────────────────────");
    if (view is not null)
    {
        Console.WriteLine($"  estado={view.Status} servicio={view.Service}");
        Console.WriteLine($"  decisión={view.Action} política={view.PolicyVersion}");
        if (view.Status == "ActionParked")
        {
            Console.WriteLine(
                $"  parked={view.FailureCode} transitorio={view.FailureWasTransient} intentos={view.FailureAttempts}");
        }
        else
        {
            Console.WriteLine($"  externo={view.ExternalId} clave={view.IdempotencyKey}");
        }
    }
    Console.WriteLine();
    Console.WriteLine(view?.Status == "ActionParked"
        ? "⚠ Acción estacionada para intervención: no se registró una confirmación falsa."
        : "✅ Investigación persistida. El replay de estos eventos no llama al LLM ni repite el efecto.");
}

static async Task RunIncidentRetryAsync(
    IServiceProvider services,
    IncidentReadModelStore readModel,
    string connectionString,
    string investigationId,
    string requestId,
    string requestedBy,
    string reason,
    CancellationToken ct)
{
    var retry = services.GetRequiredService<IncidentActionRetryProcess>();
    var actionPort = services.GetRequiredService<IIncidentActionPort>();

    Console.WriteLine("▶ Solicitando recuperación manual de una acción estacionada");
    Console.WriteLine($"  investigationId={investigationId}");
    Console.WriteLine($"  requestId={requestId} actor={requestedBy}");
    Console.WriteLine($"  motivo={reason}");
    Console.WriteLine($"  action port={actionPort.GetType().Name}");
    Console.WriteLine();

    var state = await retry.RequestAsync(
        investigationId,
        requestId,
        requestedBy,
        reason,
        ct);
    Console.WriteLine(
        state.Status == IncidentInvestigationStatus.Completed
            ? "  La misma solicitud ya estaba aplicada; no se agregó otro evento ni se repitió el efecto."
            : $"  reintento manual #{state.ManualRetryCount} persistido; la suscripción ejecutará el efecto.");
    Console.WriteLine();

    await WaitForIncidentProjectionAsync(
        readModel,
        investigationId,
        TimeSpan.FromSeconds(10),
        ct,
        minimumManualRetryCount: state.ManualRetryCount);
    var view = await readModel.GetAsync(investigationId, ct);
    await PrintIncidentAuditTrailAsync(connectionString, investigationId, ct);
    Console.WriteLine("── READ MODEL DEL INCIDENTE RECUPERADO ────────────────────");
    if (view is not null)
    {
        Console.WriteLine($"  estado={view.Status} servicio={view.Service}");
        Console.WriteLine(
            $"  reintentos manuales={view.ManualRetryCount} "
            + $"requestId={view.LastRetryRequestId} actor={view.LastRetryRequestedBy}");
        Console.WriteLine($"  motivo={view.LastRetryReason}");
        if (view.Status == "ActionParked")
        {
            Console.WriteLine(
                $"  nuevo fallo={view.FailureCode} transitorio={view.FailureWasTransient} "
                + $"intentos={view.FailureAttempts}");
        }
        else
        {
            Console.WriteLine($"  externo={view.ExternalId} clave={view.IdempotencyKey}");
        }
    }
    Console.WriteLine();
    Console.WriteLine(view?.Status == "Completed"
        ? "✅ Recuperación completada y auditada sin alterar la historia previa."
        : "⚠ El reintento también fue estacionado; la nueva falla quedó auditada.");
}

static async Task PrintIncidentAuditTrailAsync(
    string connectionString,
    string investigationId,
    CancellationToken ct)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand(
        """
        SELECT m.global_position, m.message_type, m.stream_position, m.json_data::text
          FROM curso_eventstore.messages m
          JOIN curso_eventstore.streams s ON s.stream_id = m.stream_id
         WHERE s.stream_name = @stream
         ORDER BY m.global_position
        """, connection);
    command.Parameters.AddWithValue("stream", $"incident-investigation-{investigationId}");

    Console.WriteLine("── AUDITORÍA DEL INCIDENTE ────────────────────────────────────");
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        Console.WriteLine($"  #{reader.GetInt64(0)} {reader.GetString(1)} (pos {reader.GetInt64(2)})");
        Console.WriteLine($"      payload: {Truncate(reader.GetString(3), 160)}");
    }
    Console.WriteLine();
}

static async Task WaitForIncidentProjectionAsync(
    IncidentReadModelStore readModel,
    string investigationId,
    TimeSpan timeout,
    CancellationToken ct,
    int minimumManualRetryCount = 0)
{
    var started = DateTime.UtcNow;
    while (DateTime.UtcNow - started < timeout)
    {
        var view = await readModel.GetAsync(investigationId, ct);
        if (view is { Status: "Completed" or "ActionParked" }
            && view.ManualRetryCount >= minimumManualRetryCount)
        {
            return;
        }
        await Task.Delay(200, ct);
    }
    throw new TimeoutException(
        "La reacción o la proyección del incidente no alcanzó el estado final a tiempo.");
}

static void PrintTree(NodeResult node, string prefix)
{
    var leaf = node.IsLeaf ? " (hoja)" : "";
    Console.WriteLine($"{prefix}▪ {node.NodeId}  profundidad={node.Depth}{leaf}  «{Truncate(node.Goal, 60)}»");
    Console.WriteLine($"{prefix}  ↳ {Truncate(node.Answer, 90)}");

    for (var i = 0; i < node.Children.Count; i++)
    {
        var isLast = i == node.Children.Count - 1;
        var childPrefix = prefix + (isLast ? "   " : "  │");
        PrintTree(node.Children[i], childPrefix);
    }
}

static string Truncate(string s, int max)
{
    if (string.IsNullOrEmpty(s)) return "";
    return s.Length <= max ? s : s[..max] + "…";
}
