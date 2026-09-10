using CursoAgentes.Engine.Coordination;
using Npgsql;

namespace CursoAgentes.Infrastructure.Coordination;

/// <summary>
/// Lease durable con reloj de PostgreSQL. Adquirir, renovar y liberar son
/// operaciones atómicas; ninguna decisión depende del reloj del proceso.
/// </summary>
public sealed class PostgresExecutionLeaseStore(NpgsqlDataSource dataSource)
    : IExecutionLeaseStore
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            CREATE SCHEMA IF NOT EXISTS curso_coordination;

            CREATE TABLE IF NOT EXISTS curso_coordination.execution_leases (
                resource_id  TEXT PRIMARY KEY,
                owner_id     TEXT NOT NULL,
                lease_token  BIGINT NOT NULL,
                expires_at   TIMESTAMPTZ NOT NULL,
                updated_at   TIMESTAMPTZ NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_execution_leases_expires_at
                ON curso_coordination.execution_leases (expires_at);
            """,
            connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ExecutionLease?> TryAcquireAsync(
        string resourceId,
        string ownerId,
        TimeSpan duration,
        CancellationToken ct)
    {
        Validate(resourceId, ownerId, duration);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO curso_coordination.execution_leases
                (resource_id, owner_id, lease_token, expires_at, updated_at)
            VALUES
                (@resourceId, @ownerId, 1,
                 clock_timestamp() + @duration, clock_timestamp())
            ON CONFLICT (resource_id) DO UPDATE SET
                owner_id = EXCLUDED.owner_id,
                lease_token = CASE
                    WHEN curso_coordination.execution_leases.owner_id = EXCLUDED.owner_id
                    THEN curso_coordination.execution_leases.lease_token
                    ELSE curso_coordination.execution_leases.lease_token + 1
                END,
                expires_at = clock_timestamp() + @duration,
                updated_at = clock_timestamp()
            WHERE curso_coordination.execution_leases.expires_at <= clock_timestamp()
               OR curso_coordination.execution_leases.owner_id = EXCLUDED.owner_id
            RETURNING resource_id, owner_id, lease_token, expires_at;
            """,
            connection);
        command.Parameters.AddWithValue("resourceId", resourceId);
        command.Parameters.AddWithValue("ownerId", ownerId);
        command.Parameters.AddWithValue("duration", duration);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLease(reader) : null;
    }

    public async Task<ExecutionLease?> RenewAsync(
        ExecutionLease lease,
        TimeSpan duration,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        Validate(lease.ResourceId, lease.OwnerId, duration);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            UPDATE curso_coordination.execution_leases
               SET expires_at = clock_timestamp() + @duration,
                   updated_at = clock_timestamp()
             WHERE resource_id = @resourceId
               AND owner_id = @ownerId
               AND lease_token = @leaseToken
               AND expires_at > clock_timestamp()
            RETURNING resource_id, owner_id, lease_token, expires_at;
            """,
            connection);
        AddIdentityParameters(command, lease);
        command.Parameters.AddWithValue("duration", duration);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLease(reader) : null;
    }

    public async Task<ExecutionLease?> GetActiveAsync(
        string resourceId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT resource_id, owner_id, lease_token, expires_at
              FROM curso_coordination.execution_leases
             WHERE resource_id = @resourceId
               AND expires_at > clock_timestamp();
            """,
            connection);
        command.Parameters.AddWithValue("resourceId", resourceId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLease(reader) : null;
    }

    public async Task<bool> ReleaseAsync(ExecutionLease lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            UPDATE curso_coordination.execution_leases
               SET expires_at = clock_timestamp(),
                   updated_at = clock_timestamp()
             WHERE resource_id = @resourceId
               AND owner_id = @ownerId
               AND lease_token = @leaseToken;
            """,
            connection);
        AddIdentityParameters(command, lease);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static ExecutionLease ReadLease(NpgsqlDataReader reader)
    {
        var expiresAt = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc);
        return new ExecutionLease(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            new DateTimeOffset(expiresAt));
    }

    private static void AddIdentityParameters(NpgsqlCommand command, ExecutionLease lease)
    {
        command.Parameters.AddWithValue("resourceId", lease.ResourceId);
        command.Parameters.AddWithValue("ownerId", lease.OwnerId);
        command.Parameters.AddWithValue("leaseToken", lease.LeaseToken);
    }

    private static void Validate(string resourceId, string ownerId, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be positive.");
    }
}
