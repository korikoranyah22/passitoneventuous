using CursoAgentes.Domain.Workflow;
using CursoAgentes.Engine.Llm;
using CursoAgentes.Engine.Workflow;
using CursoAgentes.Infrastructure.Llm;
using CursoAgentes.Infrastructure.Projections;
using Eventuous;
using Eventuous.Extensions;
using Eventuous.Postgresql;
using Eventuous.Postgresql.Subscriptions;
using Eventuous.Subscriptions.Registrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CursoAgentes.Infrastructure;

// ─────────────────────────────────────────────────────────────────────────────
// Wiring de TODA la infraestructura del ejemplo. Mirá cómo queda el mapa:
//
//   ▸ Eventuous + PostgreSQL  → event store real (schema curso_eventstore)
//   ▸ Command services        → los aggregates del dominio (WorkflowRun, WorkflowNode)
//   ▸ ILlmGateway             → Fake (sin red) o OpenAiCompatible (real)
//   ▸ Agentes del workflow    → PlannerAgent, WorkerAgent, SynthesizerAgent
//   ▸ RecursiveWorkflowRunner → el motor que orquesta todo
//   ▸ Read model              → proyección + tablas curso_readmodel (mismo Postgres)
//
// Cambiar de proveedor de LLM, de base o de tope de profundidad es CAMBIAR
// CONFIG, no código: la app lee esto desde appsettings.json.
// ─────────────────────────────────────────────────────────────────────────────

public static class ServiceCollectionExtensions
{
    public const string EventStoreSchema = "curso_eventstore";

    public static IServiceCollection AddCursoAgentesInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Opciones ─────────────────────────────────────────────────────────
        services.Configure<WorkflowManifest>(configuration.GetSection("Workflow"));
        services.Configure<LlmGatewayOptions>(configuration.GetSection("Llm"));
        // Los agentes toman WorkflowManifest directo (no IOptions): exponemos el
        // valor bindeado como singleton.
        services.AddSingleton(sp =>
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkflowManifest>>().Value);

        // ── Eventuous + PostgreSQL ───────────────────────────────────────────
        // initializeDatabase: true crea el schema del event store (tablas
        // messages/streams + el tipo compuesto stream_message) si no existe.
        // También registra el NpgsqlDataSource que reutiliza el read model.
        var connectionString = configuration.GetConnectionString("EventStore")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:EventStore");
        services.AddEventuousPostgres(connectionString, EventStoreSchema, initializeDatabase: true);
        services.AddEventStore<PostgresStore>();
        services.AddPostgresCheckpointStore();

        // ── Command services (aggregates) ────────────────────────────────────
        services.AddSingleton<WorkflowRunCommandService>();
        services.AddSingleton<WorkflowNodeCommandService>();

        // ── LLM: el puerto se enchufa con el adaptador que diga la config ───
        services.AddHttpClient<OpenAiCompatibleGateway>(client =>
            client.Timeout = configuration.GetValue("Llm:Timeout", TimeSpan.FromMinutes(2)));

        var provider = configuration.GetValue<string>("Llm:Provider") ?? "Fake";
        if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ILlmGateway, OpenAiCompatibleGateway>();
        }
        else
        {
            // TRAMPA CLÁSICA (lección del curso): NO registrar el FakeLlmGateway con
            // `AddSingleton<ILlmGateway, FakeLlmGateway>()`. Su constructor toma un
            // parámetro opcional `IEnumerable<string>? script` y el DI resuelve
            // `IEnumerable<string>` como la lista de TODOS los `string` registrados
            // → una colección VACÍA → el fake queda en modo "scripted" sin script y
            // siempre cae al fallback genérico (nunca divide, árbol de 1 nodo).
            // Por eso lo construimos a mano, SIN script (modo "smart").
            services.AddSingleton<ILlmGateway>(sp =>
                new FakeLlmGateway(logger: sp.GetRequiredService<ILogger<FakeLlmGateway>>()));
        }

        // ── Agentes del workflow ─────────────────────────────────────────────
        services.AddSingleton<PlannerAgent>();
        services.AddSingleton<WorkerAgent>();
        services.AddSingleton<SynthesizerAgent>();
        services.AddSingleton<RecursiveWorkflowRunner>();

        // ── Read model: proyección + suscripción a todos los streams ────────
        services.AddSingleton<WorkflowReadModelStore>();
        services.AddSingleton<WorkflowReadModelProjection>();
        services.AddSubscription<PostgresAllStreamSubscription, PostgresAllStreamSubscriptionOptions>(
            "WorkflowReadModel",
            builder => builder
                .Configure(options => ConfigureCheckpoint(options, "WorkflowReadModel"))
                .AddEventHandler<WorkflowReadModelProjection>());

        return services;
    }

    /// <summary>
    /// Política de checkpoint agresiva: flush después de CADA evento procesado.
    /// En producción esto minimiza el trabajo de replay tras un crash (el repo
    /// real de AngelNaira usa exactamente esta configuración).
    /// </summary>
    static void ConfigureCheckpoint(PostgresAllStreamSubscriptionOptions options, string id)
    {
        options.SubscriptionId = id;
        options.CheckpointCommitBatchSize = 1;
        options.CheckpointCommitDelayMs = 100;
    }
}
