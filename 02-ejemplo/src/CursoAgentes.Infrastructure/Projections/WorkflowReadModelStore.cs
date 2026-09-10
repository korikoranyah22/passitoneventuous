using System.Data;
using CursoAgentes.Domain.Workflow;
using Npgsql;

namespace CursoAgentes.Infrastructure.Projections;

public sealed record WorkflowRunReadModel(
    string RunId,
    string Goal,
    string RootNodeId,
    string Status,
    string? Answer,
    bool ExecutionRequested,
    string? ExecutionRequestId);

public sealed record WorkflowNodeReadModel(
    string NodeId,
    string RunId,
    string? ParentNodeId,
    int Depth,
    int Order,
    string Goal,
    string Status,
    bool IsLeaf,
    string Rationale,
    string? Answer);

// ─────────────────────────────────────────────────────────────────────────────
// READ MODEL (lado de lectura). En event sourcing hay DOS bases de datos en una:
// el event store (escribir, fuente de verdad) y el read model (leer, optimizado
// para consultas). Acá el read model son dos tablas planas en el MISMO Postgres
// (schema curso_readmodel), escritas por la proyección conforme llegan eventos.
//
// Este store escribe con SQL directo (INSERT ... ON CONFLICT DO UPDATE) a
// propósito: es la forma más transparente de mostrar qué hace una proyección.
// En sistemas grandes esto suele ser un DbContext de EF Core (como en el repo
// que inspira el curso) — el concepto es idéntico.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class WorkflowReadModelStore
{
    private readonly NpgsqlDataSource _dataSource;

    public WorkflowReadModelStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>Crea el schema y las tablas del read model si no existen (idempotente).</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            CREATE SCHEMA IF NOT EXISTS curso_readmodel;

            CREATE TABLE IF NOT EXISTS curso_readmodel.workflow_runs (
                run_id         TEXT PRIMARY KEY,
                goal           TEXT NOT NULL,
                root_node_id   TEXT NOT NULL,
                status         TEXT NOT NULL,
                answer         TEXT,
                execution_requested BOOLEAN NOT NULL DEFAULT FALSE,
                execution_request_id TEXT,
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

            CREATE INDEX IF NOT EXISTS ix_nodes_run ON curso_readmodel.workflow_nodes (run_id);
            ALTER TABLE curso_readmodel.workflow_runs
                ADD COLUMN IF NOT EXISTS execution_requested BOOLEAN NOT NULL DEFAULT FALSE,
                ADD COLUMN IF NOT EXISTS execution_request_id TEXT;
            CREATE INDEX IF NOT EXISTS ix_workflow_runs_requested
                ON curso_readmodel.workflow_runs (status, execution_requested)
                WHERE status = 'Running' AND execution_requested = TRUE;
            """, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Upserts (los llama la proyección por cada evento) ────────────────────

    public async Task UpsertRunAsync(WorkflowRunEvents.V1.WorkflowRunCreated e, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO curso_readmodel.workflow_runs (run_id, goal, root_node_id, status, created_at)
            VALUES (@runId, @goal, @rootNodeId, 'Running', @createdAt)
            ON CONFLICT (run_id) DO UPDATE SET
                goal = EXCLUDED.goal,
                root_node_id = EXCLUDED.root_node_id
            """, conn);
        cmd.Parameters.AddWithValue("runId", e.RunId);
        cmd.Parameters.AddWithValue("goal", e.Goal);
        cmd.Parameters.AddWithValue("rootNodeId", e.RootNodeId);
        cmd.Parameters.AddWithValue("createdAt", DateTime.Parse(e.CreatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertRunAsync(WorkflowRunEvents.V1.WorkflowRunCompleted e, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE curso_readmodel.workflow_runs
               SET status = 'Completed', answer = @answer, completed_at = @completedAt
             WHERE run_id = @runId
            """, conn);
        cmd.Parameters.AddWithValue("runId", e.RunId);
        cmd.Parameters.AddWithValue("answer", (object?)e.Answer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("completedAt", DateTime.Parse(e.CompletedAt, null, System.Globalization.DateTimeStyles.RoundtripKind));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertRunAsync(
        WorkflowRunEvents.V1.WorkflowRunExecutionRequested e,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE curso_readmodel.workflow_runs
               SET execution_requested = TRUE,
                   execution_request_id = @requestId
             WHERE run_id = @runId
            """, conn);
        cmd.Parameters.AddWithValue("runId", e.RunId);
        cmd.Parameters.AddWithValue("requestId", e.RequestId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertRunAsync(WorkflowRunEvents.V1.WorkflowRunFailed e, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE curso_readmodel.workflow_runs
               SET status = 'Failed'
             WHERE run_id = @runId
            """, conn);
        cmd.Parameters.AddWithValue("runId", e.RunId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertNodeAsync(WorkflowNodeEvents.V1.WorkflowNodeCreated e, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO curso_readmodel.workflow_nodes
                (node_id, run_id, parent_node_id, depth, ord, goal, status, is_leaf, created_at)
            VALUES
                (@nodeId, @runId, @parentNodeId, @depth, @ord, @goal, 'Pending', FALSE, @createdAt)
            ON CONFLICT (node_id) DO UPDATE SET
                goal = EXCLUDED.goal,
                depth = EXCLUDED.depth
            """, conn);
        cmd.Parameters.AddWithValue("nodeId", e.NodeId);
        cmd.Parameters.AddWithValue("runId", e.RunId);
        cmd.Parameters.AddWithValue("parentNodeId", (object?)e.ParentNodeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("depth", e.Depth);
        cmd.Parameters.AddWithValue("ord", e.Order);
        cmd.Parameters.AddWithValue("goal", e.Goal);
        cmd.Parameters.AddWithValue("createdAt", DateTime.Parse(e.CreatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertNodeAsync(WorkflowNodeEvents.V1.WorkflowNodePlanned e, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE curso_readmodel.workflow_nodes
               SET status = 'Planned', is_leaf = @isLeaf, rationale = @rationale
             WHERE node_id = @nodeId
            """, conn);
        cmd.Parameters.AddWithValue("nodeId", e.NodeId);
        cmd.Parameters.AddWithValue("isLeaf", e.IsLeaf);
        cmd.Parameters.AddWithValue("rationale", e.Rationale);
        await cmd.ExecuteNonQueryAsync(ct);
    }

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

    public async Task UpsertNodeAsync(WorkflowNodeEvents.V1.WorkflowNodeFailed e, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE curso_readmodel.workflow_nodes
               SET status = 'Failed'
             WHERE node_id = @nodeId
            """, conn);
        cmd.Parameters.AddWithValue("nodeId", e.NodeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Lecturas (para la demo) ──────────────────────────────────────────────

    public async Task<IReadOnlyList<(string NodeId, int Depth, bool IsLeaf, string Status, string Goal)>> GetNodesAsync(
        string runId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT node_id, depth, is_leaf, status, goal
              FROM curso_readmodel.workflow_nodes
             WHERE run_id = @runId
             ORDER BY depth, ord
            """, conn);
        cmd.Parameters.AddWithValue("runId", runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<(string, int, bool, string, string)>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add((
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetBoolean(2),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return rows;
    }

    public async Task<(string RunId, string Goal, string Status, string? Answer)?> GetRunAsync(
        string runId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT run_id, goal, status, answer
              FROM curso_readmodel.workflow_runs
             WHERE run_id = @runId
            """, conn);
        cmd.Parameters.AddWithValue("runId", runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct)) return null;
        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    /// <summary>
    /// Vista completa usada por hosts de consulta. Sigue siendo eventualmente
    /// consistente: para decidir si un run existe o puede reanudarse se leen
    /// los streams mediante WorkflowExecutionReader.
    /// </summary>
    public async Task<WorkflowRunReadModel?> GetRunDetailsAsync(
        string runId,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT run_id, goal, root_node_id, status, answer,
                   execution_requested, execution_request_id
              FROM curso_readmodel.workflow_runs
             WHERE run_id = @runId
            """, conn);
        cmd.Parameters.AddWithValue("runId", runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct)) return null;
        return new WorkflowRunReadModel(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetBoolean(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public async Task<IReadOnlyList<string>> GetRequestedRunningRunIdsAsync(
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT run_id
              FROM curso_readmodel.workflow_runs
             WHERE status = 'Running'
               AND execution_requested = TRUE
             ORDER BY created_at
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var runIds = new List<string>();
        while (await reader.ReadAsync(ct)) runIds.Add(reader.GetString(0));
        return runIds;
    }

    public async Task<IReadOnlyList<WorkflowNodeReadModel>> GetNodeDetailsAsync(
        string runId,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT node_id, run_id, parent_node_id, depth, ord, goal,
                   status, is_leaf, rationale, answer
              FROM curso_readmodel.workflow_nodes
             WHERE run_id = @runId
             ORDER BY depth, ord
            """, conn);
        cmd.Parameters.AddWithValue("runId", runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<WorkflowNodeReadModel>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new WorkflowNodeReadModel(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetBoolean(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return rows;
    }
}
