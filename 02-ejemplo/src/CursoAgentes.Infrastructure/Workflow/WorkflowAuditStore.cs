using System.Text.Json;
using Npgsql;

namespace CursoAgentes.Infrastructure.Workflow;

public sealed record WorkflowAuditEntry(
    long GlobalPosition,
    string StreamName,
    string EventType,
    long StreamPosition,
    DateTime CreatedAt,
    JsonElement Payload);

/// <summary>
/// Consulta de auditoría del workflow sobre la historia inmutable. Descubre
/// los streams de nodos a partir de WorkflowNodeCreated, no desde el read
/// model, por lo que sigue funcionando aunque la proyección esté atrasada.
/// </summary>
public sealed class WorkflowAuditStore(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<WorkflowAuditEntry>> GetAsync(
        string runId,
        CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            WITH target_streams AS (
                SELECT stream_id
                  FROM curso_eventstore.streams
                 WHERE stream_name = @runStream
                UNION
                SELECT DISTINCT m.stream_id
                  FROM curso_eventstore.messages m
                 WHERE m.message_type = 'V1.WorkflowNodeCreated'
                   AND m.json_data ->> 'runId' = @runId
            )
            SELECT m.global_position,
                   s.stream_name,
                   m.message_type,
                   m.stream_position,
                   m.created,
                   m.json_data::text
              FROM curso_eventstore.messages m
              JOIN curso_eventstore.streams s ON s.stream_id = m.stream_id
             WHERE m.stream_id IN (SELECT stream_id FROM target_streams)
             ORDER BY m.global_position
            """, conn);
        cmd.Parameters.AddWithValue("runId", runId);
        cmd.Parameters.AddWithValue("runStream", $"workflow-run-{runId}");

        var entries = new List<WorkflowAuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            using var payload = JsonDocument.Parse(reader.GetString(5));
            entries.Add(new WorkflowAuditEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetDateTime(4),
                payload.RootElement.Clone()));
        }
        return entries;
    }
}
