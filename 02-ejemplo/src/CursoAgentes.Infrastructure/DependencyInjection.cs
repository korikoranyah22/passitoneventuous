using CursoAgentes.Domain.Incidents;
using CursoAgentes.Domain.Workflow;
using CursoAgentes.Engine.Incidents;
using CursoAgentes.Engine.Llm;
using CursoAgentes.Engine.Workflow;
using CursoAgentes.Engine.Coordination;
using CursoAgentes.Infrastructure.Coordination;
using CursoAgentes.Infrastructure.Incidents;
using CursoAgentes.Infrastructure.Llm;
using CursoAgentes.Infrastructure.Projections;
using CursoAgentes.Infrastructure.Workflow;
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
        services.Configure<IncidentActionHttpOptions>(configuration.GetSection("IncidentAction"));
        services.Configure<IncidentActionReactionOptions>(
            configuration.GetSection("IncidentAction:Retry"));
        // Los agentes toman WorkflowManifest directo (no IOptions): exponemos el
        // valor bindeado como singleton.
        services.AddSingleton(sp =>
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkflowManifest>>().Value);
        services.AddSingleton(sp =>
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IncidentActionHttpOptions>>().Value);
        services.AddSingleton(sp =>
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IncidentActionReactionOptions>>().Value);

        // ── Eventuous + PostgreSQL ───────────────────────────────────────────
        // initializeDatabase: true crea el schema del event store (tablas
        // messages/streams + el tipo compuesto stream_message) si no existe.
        // También registra el NpgsqlDataSource que reutiliza el read model.
        var connectionString = configuration.GetConnectionString("EventStore")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:EventStore");
        services.AddEventuousPostgres(connectionString, EventStoreSchema, initializeDatabase: true);
        services.AddEventStore<PostgresStore>();
        services.AddPostgresCheckpointStore();
        services.AddSingleton<IExecutionLeaseStore, PostgresExecutionLeaseStore>();

        // ── Command services (aggregates) ────────────────────────────────────
        services.AddSingleton<WorkflowRunCommandService>();
        services.AddSingleton<WorkflowNodeCommandService>();
        services.AddSingleton<WorkflowExecutionReader>();
        services.AddSingleton<WorkflowExecutionRequestService>();
        services.AddSingleton<IncidentInvestigationCommandService>();

        // Efecto externo del caso práctico. Este adaptador es deliberadamente
        // local; un host real lo reemplaza por HTTP manteniendo la idempotencia.
        services.AddSingleton<InMemoryIncidentActionPort>();
        services.AddHttpClient("IncidentAction", (provider, client) =>
        {
            var options = provider.GetRequiredService<IncidentActionHttpOptions>();
            if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseAddress))
                throw new InvalidOperationException("IncidentAction:BaseUrl must be absolute.");
            if (options.Timeout <= TimeSpan.Zero)
                throw new InvalidOperationException("IncidentAction:Timeout must be positive.");
            client.BaseAddress = baseAddress;
            client.Timeout = options.Timeout;
        });
        var actionProvider = configuration.GetValue<string>("IncidentAction:Provider")
            ?? "InMemory";
        if (actionProvider.Equals("Http", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IIncidentActionPort>(provider =>
                new HttpIncidentActionPort(
                    provider.GetRequiredService<IHttpClientFactory>().CreateClient("IncidentAction"),
                    provider.GetRequiredService<IncidentActionHttpOptions>()));
        }
        else
        {
            services.AddSingleton<IIncidentActionPort>(provider =>
                provider.GetRequiredService<InMemoryIncidentActionPort>());
        }
        services.AddSingleton<IncidentActionHandler>();
        services.AddSingleton<IncidentActionParkingHandler>();
        services.AddSingleton<IncidentInvestigationStateReader>();
        services.AddSingleton<IncidentActionReactionProcessor>();
        services.AddSingleton<IncidentActionRetryProcess>();
        services.AddSingleton<IncidentInvestigationProcess>();

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
        services.AddSingleton<WorkflowAuditStore>();
        services.AddSingleton<WorkflowReadModelProjection>();
        services.AddSubscription<PostgresAllStreamSubscription, PostgresAllStreamSubscriptionOptions>(
            "WorkflowReadModel",
            builder => builder
                .Configure(options => ConfigureCheckpoint(options, "WorkflowReadModel"))
                .AddEventHandler<WorkflowReadModelProjection>());

        services.AddSingleton<IncidentReadModelStore>();
        services.AddSingleton<IncidentReadModelProjection>();
        services.AddSubscription<PostgresAllStreamSubscription, PostgresAllStreamSubscriptionOptions>(
            "IncidentReadModel",
            builder => builder
                .Configure(options => ConfigureCheckpoint(options, "IncidentReadModel"))
                .AddEventHandler<IncidentReadModelProjection>());

        // Reacción de negocio separada del read model. El checkpoint propio
        // evita considerar procesada una decisión antes de confirmar su efecto.
        services.AddSingleton<IncidentActionReaction>();
        services.AddSubscription<PostgresAllStreamSubscription, PostgresAllStreamSubscriptionOptions>(
            "IncidentActions",
            builder => builder
                .Configure(options => ConfigureCheckpoint(options, "IncidentActions"))
                .AddEventHandler<IncidentActionReaction>());

        return services;
    }

    /// <summary>
    /// Política de checkpoint agresiva: flush después de CADA evento procesado.
    /// En producción esto minimiza el trabajo de replay tras un crash. El
    /// tamaño y la demora son decisiones operativas configurables.
    /// </summary>
    static void ConfigureCheckpoint(PostgresAllStreamSubscriptionOptions options, string id)
    {
        options.SubscriptionId = id;
        options.CheckpointCommitBatchSize = 1;
        options.CheckpointCommitDelayMs = 100;
    }
}
