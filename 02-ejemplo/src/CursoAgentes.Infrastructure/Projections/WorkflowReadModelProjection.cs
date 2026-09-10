using CursoAgentes.Domain.Workflow;
using Eventuous.Subscriptions;
using EventHandler = Eventuous.Subscriptions.EventHandler;

namespace CursoAgentes.Infrastructure.Projections;

// ─────────────────────────────────────────────────────────────────────────────
// PROYECCIÓN: convierte eventos del event store en filas del read model.
// Se registra como handler de una suscripción a TODOS los streams
// (PostgresAllStreamSubscription) y por cada evento relevante hace un upsert
// idempotente en Postgres. Idempotente = si el mismo evento se procesa dos
// veces (replay, crash, reentrega), el read model queda igual.
//
// Por qué hace falta: los eventos son la historia COMPLETA; las tablas del read
// model son la foto ACTUAL. La proyección mantiene la foto al día escuchando la
// historia. Esto es el "CQRS" del event sourcing: un modelo de escritura
// (eventos) y un modelo de lectura (tablas) que se sincronizan por eventos.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class WorkflowReadModelProjection : EventHandler
{
    private readonly WorkflowReadModelStore _store;

    public WorkflowReadModelProjection(WorkflowReadModelStore store)
    {
        _store = store;

        // Runs
        On<WorkflowRunEvents.V1.WorkflowRunCreated>(ctx =>
            new ValueTask(_store.UpsertRunAsync(ctx.Message, ctx.CancellationToken)));
        On<WorkflowRunEvents.V1.WorkflowRunExecutionRequested>(ctx =>
            new ValueTask(_store.UpsertRunAsync(ctx.Message, ctx.CancellationToken)));
        On<WorkflowRunEvents.V1.WorkflowRunCompleted>(ctx =>
            new ValueTask(_store.UpsertRunAsync(ctx.Message, ctx.CancellationToken)));
        On<WorkflowRunEvents.V1.WorkflowRunFailed>(ctx =>
            new ValueTask(_store.UpsertRunAsync(ctx.Message, ctx.CancellationToken)));

        // Nodos
        On<WorkflowNodeEvents.V1.WorkflowNodeCreated>(ctx =>
            new ValueTask(_store.UpsertNodeAsync(ctx.Message, ctx.CancellationToken)));
        On<WorkflowNodeEvents.V1.WorkflowNodePlanned>(ctx =>
            new ValueTask(_store.UpsertNodeAsync(ctx.Message, ctx.CancellationToken)));
        On<WorkflowNodeEvents.V1.WorkflowNodeCompleted>(ctx =>
            new ValueTask(_store.UpsertNodeAsync(ctx.Message, ctx.CancellationToken)));
        On<WorkflowNodeEvents.V1.WorkflowNodeFailed>(ctx =>
            new ValueTask(_store.UpsertNodeAsync(ctx.Message, ctx.CancellationToken)));
    }
}
