# Paso 5 · Persistencia: Eventuous + PostgreSQL

## Objetivo

Enchufar el **event store real** (PostgreSQL) y registrar los command services
en DI. Cuando termines este paso, los comandos del dominio se persisten de
verdad en Postgres.

## Concepto

Eventuous.Postgresql convierte un Postgres común en un event store:
tablas `streams` y `messages` + el tipo compuesto `stream_message`, en un
schema propio (`curso_eventstore`). El mismo Postgres también aloja el read
model (`curso_readmodel`, paso 8). El host HTTP del paso 13 agrega
`curso_coordination` para sus leases; son tres responsabilidades aisladas en
una sola base física.

## Código (archivo `src/CursoAgentes.Infrastructure/DependencyInjection.cs`)

```csharp
public static IServiceCollection AddCursoAgentesInfrastructure(
    this IServiceCollection services,
    IConfiguration configuration)
{
    // ── Opciones ───────────────────────────────────────────────────────────
    services.Configure<WorkflowManifest>(configuration.GetSection("Workflow"));
    services.Configure<LlmGatewayOptions>(configuration.GetSection("Llm"));
    services.AddSingleton(sp =>
        sp.GetRequiredService<IOptions<WorkflowManifest>>().Value);

    // ── Eventuous + PostgreSQL ─────────────────────────────────────────────
    var connectionString = configuration.GetConnectionString("EventStore")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:EventStore");
    services.AddEventuousPostgres(connectionString, "curso_eventstore", initializeDatabase: true);
    services.AddEventStore<PostgresStore>();
    services.AddPostgresCheckpointStore();

    // ── Command services (aggregates) ──────────────────────────────────────
    services.AddSingleton<WorkflowRunCommandService>();
    services.AddSingleton<WorkflowNodeCommandService>();

    // ── LLM (paso 6) ───────────────────────────────────────────────────────
    services.AddHttpClient<OpenAiCompatibleGateway>(client =>
        client.Timeout = configuration.GetValue("Llm:Timeout", TimeSpan.FromMinutes(2)));
    var provider = configuration.GetValue<string>("Llm:Provider") ?? "Fake";
    if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        services.AddSingleton<ILlmGateway, OpenAiCompatibleGateway>();
    else
        services.AddSingleton<ILlmGateway>(sp =>
            new FakeLlmGateway(
                logger: sp.GetRequiredService<ILogger<FakeLlmGateway>>()));

    // ── Agentes + motor (paso 7) ───────────────────────────────────────────
    services.AddSingleton<PlannerAgent>();
    services.AddSingleton<WorkerAgent>();
    services.AddSingleton<SynthesizerAgent>();
    services.AddSingleton<RecursiveWorkflowRunner>();

    // ── Read model (paso 8) ────────────────────────────────────────────────
    services.AddSingleton<WorkflowReadModelStore>();
    services.AddSingleton<WorkflowReadModelProjection>();
    services.AddSubscription<PostgresAllStreamSubscription, PostgresAllStreamSubscriptionOptions>(
        "WorkflowReadModel",
        builder => builder
            .Configure(options => ConfigureCheckpoint(options, "WorkflowReadModel"))
            .AddEventHandler<WorkflowReadModelProjection>());

    return services;
}
```

El fake se construye explícitamente sin script para activar su modo `smart`.
Registrarlo sólo por tipo haría que DI resolviera su parámetro opcional
`IEnumerable<string>` como una colección vacía y lo dejaría accidentalmente en
modo scripted.

### Qué hace cada línea de Eventuous

| Línea | Efecto |
|---|---|
| `AddEventuousPostgres(conn, "curso_eventstore", initializeDatabase: true)` | Registra el `NpgsqlDataSource` y un hosted service que **crea el schema del event store al arrancar el host** |
| `AddEventStore<PostgresStore>()` | El `IEventStore` concreto que usan los command services |
| `AddPostgresCheckpointStore()` | Checkpoints de suscripciones en Postgres |
| `AddSubscription<PostgresAllStreamSubscription, …>` | Registra un consumidor durable sobre **todos los streams**: puede alimentar una proyección o reaccionar con un efecto |

La solución final registra checkpoints independientes para
`WorkflowReadModel`, `IncidentReadModel` e `IncidentActions`. Esto es
intencional: que una proyección haya avanzado no significa que el efecto del
incidente haya terminado, ni viceversa.

> **Detalle del checkpoint** (mirá `ConfigureCheckpoint` en el archivo):
> `CheckpointCommitBatchSize = 1` y `CheckpointCommitDelayMs = 100` — flush
> agresivo para minimizar el replay tras un crash. En un sistema con más
> volumen conviene ajustar batch y demora según el costo de reprocesar.

### La connection string

En `src/CursoAgentes.App/appsettings.json`:

```jsonc
"ConnectionStrings": {
  "EventStore": "Host=localhost;Port=5432;Database=cursoagentesdb;Username=cursoagentes;Password=curso123"
}
```

Y el Postgres se levanta con `docker compose up -d` (mirá
`02-ejemplo/docker-compose.yml`: postgres 16, base `cursoagentesdb`).

## Probalo (requiere Postgres)

```bash
docker compose up -d
dotnet run --project src/CursoAgentes.App
```

La demo espera a que Eventuous cree el schema (`stream_message` + `messages`),
corre el workflow y al final imprime la **auditoría**: los eventos crudos de
`curso_eventstore.messages` con su payload jsonb. Esa impresión es la prueba
visual de que la persistencia anda.

---

**Siguiente**: [Paso 6 · Gateway LLM](06-gateway-llm.md)
