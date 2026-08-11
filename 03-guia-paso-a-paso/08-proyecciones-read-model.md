# Paso 8 · Proyecciones y read model

## Objetivo

Construir el **lado de lectura**: dos tablas planas en Postgres
(`curso_readmodel.workflow_runs` y `workflow_nodes`) que se mantienen al día
escuchando los eventos del store. Esto es el **CQRS** del event sourcing: un
modelo de escritura (eventos, la historia) y un modelo de lectura (tablas, la
foto actual).

## Concepto

Los eventos son la fuente de verdad, pero no son cómodos para consultar ("dame
los runs de hoy"). Para eso existen las **proyecciones**: handlers que escuchan
eventos y hacen *upserts idempotentes* en tablas de consulta.

- **Idempotente**: si el mismo evento se procesa dos veces (replay, crash,
  reentrega), el read model queda igual. Por eso los upserts usan
  `INSERT … ON CONFLICT DO UPDATE`.
- **Eventual consistency**: el read model va un ratito *detrás* del event
  store (lo que tarda la suscripción en procesar). La demo espera a que la
  proyección alcance el estado final antes de imprimir el read model.

## Código

### 1. La proyección (`src/CursoAgentes.Infrastructure/Projections/WorkflowReadModelProjection.cs`)

```csharp
public sealed class WorkflowReadModelProjection : EventHandler
{
    private readonly WorkflowReadModelStore _store;

    public WorkflowReadModelProjection(WorkflowReadModelStore store)
    {
        _store = store;

        // Runs
        On<WorkflowRunEvents.V1.WorkflowRunCreated>(ctx =>
            new ValueTask(_store.UpsertRunAsync(ctx.Message, ctx.CancellationToken)));
        On<WorkflowRunEvents.V1.WorkflowRunCompleted>(ctx =>
            new ValueTask(_store.UpsertRunAsync(ctx.Message, ctx.CancellationToken)));
        On<WorkflowRunEvents.V1.WorkflowRunFailed>(ctx =>
            new ValueTask(_store.UpsertRunAsync(ctx.Message, ctx.CancellationToken)));

        // Nodos
        On<WorkflowNodeEvents.V1.WorkflowNodeCreated>(ctx =>
            new ValueTask(_store.UpsertNodeAsync(ctx.Message, ctx.CancellationToken)));
        // … Planned, Completed, Failed …
    }
}
```

Se registra como handler de la suscripción `PostgresAllStreamSubscription`
(ver paso 5): escucha **todos los streams** y despacha cada evento al `On<T>`
que corresponda.

### 2. El store del read model (`src/CursoAgentes.Infrastructure/Projections/WorkflowReadModelStore.cs`)

Crea las tablas (idempotente):

```sql
CREATE SCHEMA IF NOT EXISTS curso_readmodel;

CREATE TABLE IF NOT EXISTS curso_readmodel.workflow_runs (
    run_id         TEXT PRIMARY KEY,
    goal           TEXT NOT NULL,
    root_node_id   TEXT NOT NULL,
    status         TEXT NOT NULL,
    answer         TEXT,
    created_at     TIMESTAMPTZ NOT NULL,
    completed_at   TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS curso_readmodel.workflow_nodes (
    node_id        TEXT PRIMARY KEY,
    run_id         TEXT NOT NULL,
    parent_node_id TEXT,
    depth          INTEGER NOT NULL,
    ord            INTEGER NOT NULL,
    goal           TEXT NOT NULL,
    status         TEXT NOT NULL,
    is_leaf        BOOLEAN NOT NULL,
    rationale      TEXT NOT NULL DEFAULT '',
    answer         TEXT,
    created_at     TIMESTAMPTZ NOT NULL
);
```

Y los upserts por evento, con SQL directo. Un ejemplo:

```csharp
public async Task UpsertNodeAsync(WorkflowNodeEvents.V1.WorkflowNodeCompleted e, CancellationToken ct)
{
    await using var conn = await _dataSource.OpenConnectionAsync(ct);
    await using var cmd = new NpgsqlCommand(
        """
        UPDATE curso_readmodel.workflow_nodes
           SET status = 'Completed', answer = @answer
         WHERE node_id = @nodeId
        """, conn);
    cmd.Parameters.AddWithValue("nodeId", e.NodeId);
    cmd.Parameters.AddWithValue("answer", (object?)e.Answer ?? DBNull.Value);
    await cmd.ExecuteNonQueryAsync(ct);
}
```

> El store usa el `NpgsqlDataSource` que registró `AddEventuousPostgres` (paso
> 5): el event store y el read model comparten la conexión a Postgres, pero son
> schemas independientes.

> **Nota honesta**: acá escribimos SQL a mano a propósito, para que se vea
> qué hace una proyección. En sistemas grandes esto suele ser un `DbContext`
> de EF Core (el repo real que inspira este curso lo hace así). El concepto es
> idéntico: evento → upsert → tabla.

## Probalo

```bash
docker compose up -d
dotnet run --project src/CursoAgentes.App
```

Al final de la corrida, la demo imprime el read model (run + nodos con su
estado). Además podés consultarlo a mano:

```bash
docker compose exec postgres psql -U cursoagentes -d cursoagentesdb \
  -c "SELECT run_id, status, goal FROM curso_readmodel.workflow_runs;"
```

---

**Siguiente**: [Paso 9 · App de demostración](09-app-demo.md)
