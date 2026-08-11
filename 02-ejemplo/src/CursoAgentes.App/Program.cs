using CursoAgentes.Domain.Workflow;
using CursoAgentes.Engine.Workflow;
using CursoAgentes.Infrastructure;
using CursoAgentes.Infrastructure.Projections;
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
//  3. Corre el workflow de nodos recursivos (con el gateway Fake por default;
//     cambiá Llm:Provider a "OpenAI" para usar un LLM real).
//  4. Imprime el árbol de nodos, la bitácora, los EVENTOS CRUDOS del event
//     store (¡la auditoría!) y el read model.
//
// Correlo con:  dotnet run --project src/CursoAgentes.App
// (Postgres arriba: docker compose up -d)
// ─────────────────────────────────────────────────────────────────────────────

// Registrar los tipos de eventos en el TypeMap: sin esto Eventuous no puede
// deserializar los eventos guardados en Postgres (el clásico "me guarda pero
// no me lee").
TypeMap.RegisterKnownEventTypes(typeof(WorkflowRunEvents.V1.WorkflowRunCreated).Assembly);

var builder = Host.CreateApplicationBuilder(args);

// La demo debe poder correrse desde CUALQUIER carpeta (p. ej.
// `dotnet run --project src/CursoAgentes.App` desde la raíz del ejemplo).
// El content root por defecto es el working directory; lo fijamos a la carpeta
// del binario, donde vive appsettings.json (copiado por CopyToOutputDirectory).
builder.Environment.ContentRootPath = AppContext.BaseDirectory;
builder.Configuration.Sources.Clear();
builder.Configuration.SetBasePath(AppContext.BaseDirectory);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

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

// ── 2. Arrancar el host ──────────────────────────────────────────────────────
// Al arrancar, el SchemaInitializer de Eventuous crea el schema del event
// store (curso_eventstore) y arrancan los hosted services (la suscripción
// WorkflowReadModel).
await host.StartAsync(ct);
await WaitForEventStoreObjectsAsync(connectionString, ct);

// El read model son tablas aparte en el mismo Postgres (schema curso_readmodel).
var readModel = host.Services.GetRequiredService<WorkflowReadModelStore>();
await readModel.InitializeAsync(ct);
Console.WriteLine("✔ Postgres listo — event store (curso_eventstore) y read model (curso_readmodel).");
Console.WriteLine();

// ── 3. Correr el workflow ────────────────────────────────────────────────────
var goal = args.Length > 0
    ? string.Join(" ", args)
    : "¿Por qué los agentes necesitan event sourcing?";
var runId = "run-" + Guid.NewGuid().ToString("N")[..8];

var runner = host.Services.GetRequiredService<RecursiveWorkflowRunner>();
Console.WriteLine($"▶ Ejecutando workflow para: «{goal}»");
Console.WriteLine($"  (runId={runId} — cada nodo es un stream workflow-node-* en Postgres)");
Console.WriteLine();

var result = await runner.RunAsync(runId, goal, ct);

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
Console.WriteLine($"   volvé a correr con el MISMO runId para ver que los eventos ya están.");
await host.StopAsync(ct);

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

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
