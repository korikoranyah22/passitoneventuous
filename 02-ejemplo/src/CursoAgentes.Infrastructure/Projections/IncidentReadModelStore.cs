using CursoAgentes.Domain.Incidents;
using Npgsql;

namespace CursoAgentes.Infrastructure.Projections;

public sealed record IncidentInvestigationView(
    string InvestigationId,
    string Service,
    string Status,
    int? FailedChecks,
    int? AffectedRegions,
    string? AnalysisSummary,
    string? AnalystProfile,
    string? AnalystRouteId,
    string? AnalystModel,
    int? AnalystAttempts,
    string? CritiqueVerdict,
    double? CritiqueConfidence,
    string? CriticProfile,
    string? CriticRouteId,
    string? CriticModel,
    int? CriticAttempts,
    string? Action,
    string? PolicyVersion,
    string? FailureCode,
    bool? FailureWasTransient,
    int? FailureAttempts,
    int ManualRetryCount,
    string? LastRetryRequestId,
    string? LastRetryRequestedBy,
    string? LastRetryReason,
    string? ExternalId,
    string? IdempotencyKey);

/// <summary>
/// Vista de consulta del caso práctico. Conserva artefactos estructurados y
/// auditoría de rutas, pero no prompts ni razonamiento interno del modelo.
/// </summary>
public sealed class IncidentReadModelStore(NpgsqlDataSource dataSource)
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            CREATE SCHEMA IF NOT EXISTS curso_readmodel;

            CREATE TABLE IF NOT EXISTS curso_readmodel.incident_investigations (
                investigation_id  TEXT PRIMARY KEY,
                service           TEXT NOT NULL,
                status            TEXT NOT NULL,
                total_checks      INTEGER,
                failed_checks     INTEGER,
                affected_regions  INTEGER,
                analysis_summary  TEXT,
                analyst_profile   TEXT,
                analyst_route_id  TEXT,
                analyst_model     TEXT,
                analyst_attempts  INTEGER,
                critique_verdict  TEXT,
                critique_confidence DOUBLE PRECISION,
                critic_profile    TEXT,
                critic_route_id   TEXT,
                critic_model      TEXT,
                critic_attempts   INTEGER,
                action            TEXT,
                policy_version    TEXT,
                reasons           TEXT[],
                failure_code      TEXT,
                failure_transient BOOLEAN,
                failure_attempts  INTEGER,
                manual_retry_count INTEGER NOT NULL DEFAULT 0,
                last_retry_request_id TEXT,
                last_retry_requested_by TEXT,
                last_retry_reason TEXT,
                external_id       TEXT,
                idempotency_key   TEXT,
                started_at        TIMESTAMPTZ NOT NULL,
                completed_at      TIMESTAMPTZ
            );

            ALTER TABLE curso_readmodel.incident_investigations
                ADD COLUMN IF NOT EXISTS failure_code TEXT,
                ADD COLUMN IF NOT EXISTS failure_transient BOOLEAN,
                ADD COLUMN IF NOT EXISTS failure_attempts INTEGER,
                ADD COLUMN IF NOT EXISTS manual_retry_count INTEGER NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS last_retry_request_id TEXT,
                ADD COLUMN IF NOT EXISTS last_retry_requested_by TEXT,
                ADD COLUMN IF NOT EXISTS last_retry_reason TEXT;
            """, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(
        IncidentInvestigationEvents.V1.IncidentInvestigationStarted e,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO curso_readmodel.incident_investigations
                (investigation_id, service, status, started_at)
            VALUES (@id, @service, 'Started', @startedAt)
            ON CONFLICT (investigation_id) DO UPDATE SET
                service = EXCLUDED.service
            """, connection);
        command.Parameters.AddWithValue("id", e.InvestigationId);
        command.Parameters.AddWithValue("service", e.Service);
        command.Parameters.AddWithValue("startedAt", Parse(e.StartedAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(
        IncidentInvestigationEvents.V1.IncidentSignalRecorded e,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            UPDATE curso_readmodel.incident_investigations
               SET status = 'SignalRecorded',
                   total_checks = @total,
                   failed_checks = @failed,
                   affected_regions = @regions
             WHERE investigation_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", e.InvestigationId);
        command.Parameters.AddWithValue("total", e.Signal.TotalChecks);
        command.Parameters.AddWithValue("failed", e.Signal.FailedChecks);
        command.Parameters.AddWithValue("regions", e.Signal.AffectedRegions);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(
        IncidentInvestigationEvents.V1.IncidentAnalysisRecorded e,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            UPDATE curso_readmodel.incident_investigations
               SET status = 'AnalysisRecorded',
                   analysis_summary = @summary,
                   analyst_profile = @profile,
                   analyst_route_id = @routeId,
                   analyst_model = @model,
                   analyst_attempts = @attempts
             WHERE investigation_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", e.InvestigationId);
        command.Parameters.AddWithValue("summary", e.Analysis.Summary);
        command.Parameters.AddWithValue("profile", e.Analysis.Route.Profile);
        command.Parameters.AddWithValue("routeId", e.Analysis.Route.RouteId);
        command.Parameters.AddWithValue("model", e.Analysis.Route.Model);
        command.Parameters.AddWithValue("attempts", e.Analysis.Route.Attempts);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(
        IncidentInvestigationEvents.V1.IncidentCritiqueRecorded e,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            UPDATE curso_readmodel.incident_investigations
               SET status = 'CritiqueRecorded',
                   critique_verdict = @verdict,
                   critique_confidence = @confidence,
                   critic_profile = @profile,
                   critic_route_id = @routeId,
                   critic_model = @model,
                   critic_attempts = @attempts
             WHERE investigation_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", e.InvestigationId);
        command.Parameters.AddWithValue("verdict", e.Critique.Verdict);
        command.Parameters.AddWithValue("confidence", e.Critique.Confidence);
        command.Parameters.AddWithValue("profile", e.Critique.Route.Profile);
        command.Parameters.AddWithValue("routeId", e.Critique.Route.RouteId);
        command.Parameters.AddWithValue("model", e.Critique.Route.Model);
        command.Parameters.AddWithValue("attempts", e.Critique.Route.Attempts);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(
        IncidentInvestigationEvents.V1.IncidentActionDecided e,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            UPDATE curso_readmodel.incident_investigations
               SET status = 'ActionDecided',
                   action = @action,
                   policy_version = @policy,
                   reasons = @reasons
             WHERE investigation_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", e.InvestigationId);
        command.Parameters.AddWithValue("action", e.Decision.Action);
        command.Parameters.AddWithValue("policy", e.Decision.PolicyVersion);
        command.Parameters.AddWithValue("reasons", e.Decision.Reasons);
        await command.ExecuteNonQueryAsync(ct);
    }

    public Task UpsertAsync(
        IncidentInvestigationEvents.V1.IncidentOpened e,
        CancellationToken ct) =>
        CompleteAsync(e.InvestigationId, e.ExternalId, e.IdempotencyKey, e.OpenedAt, ct);

    public Task UpsertAsync(
        IncidentInvestigationEvents.V1.IncidentHumanReviewRequested e,
        CancellationToken ct) =>
        CompleteAsync(e.InvestigationId, e.ExternalId, e.IdempotencyKey, e.RequestedAt, ct);

    public async Task UpsertAsync(
        IncidentInvestigationEvents.V1.IncidentActionParked e,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            UPDATE curso_readmodel.incident_investigations
               SET status = 'ActionParked',
                   failure_code = @code,
                   failure_transient = @transient,
                   failure_attempts = @attempts
             WHERE investigation_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", e.InvestigationId);
        command.Parameters.AddWithValue("code", e.Failure.Code);
        command.Parameters.AddWithValue("transient", e.Failure.WasTransient);
        command.Parameters.AddWithValue("attempts", e.Failure.Attempts);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(
        IncidentInvestigationEvents.V1.IncidentActionRetryRequested e,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            UPDATE curso_readmodel.incident_investigations
               SET status = 'ActionDecided',
                   failure_code = NULL,
                   failure_transient = NULL,
                   failure_attempts = NULL,
                   manual_retry_count = @retryNumber,
                   last_retry_request_id = @requestId,
                   last_retry_requested_by = @requestedBy,
                   last_retry_reason = @reason
             WHERE investigation_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", e.InvestigationId);
        command.Parameters.AddWithValue("retryNumber", e.RetryNumber);
        command.Parameters.AddWithValue("requestId", e.RequestId);
        command.Parameters.AddWithValue("requestedBy", e.RequestedBy);
        command.Parameters.AddWithValue("reason", e.Reason);
        await command.ExecuteNonQueryAsync(ct);
    }

    async Task CompleteAsync(
        string investigationId,
        string externalId,
        string idempotencyKey,
        string completedAt,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            UPDATE curso_readmodel.incident_investigations
               SET status = 'Completed',
                   external_id = @externalId,
                   idempotency_key = @idempotencyKey,
                   completed_at = @completedAt
             WHERE investigation_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", investigationId);
        command.Parameters.AddWithValue("externalId", externalId);
        command.Parameters.AddWithValue("idempotencyKey", idempotencyKey);
        command.Parameters.AddWithValue("completedAt", Parse(completedAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IncidentInvestigationView?> GetAsync(
        string investigationId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT investigation_id, service, status, failed_checks, affected_regions,
                   analysis_summary, analyst_profile, analyst_route_id, analyst_model,
                   analyst_attempts, critique_verdict, critique_confidence,
                   critic_profile, critic_route_id, critic_model, critic_attempts,
                   action, policy_version, failure_code, failure_transient,
                   failure_attempts, manual_retry_count, last_retry_request_id,
                   last_retry_requested_by, last_retry_reason, external_id,
                   idempotency_key
              FROM curso_readmodel.incident_investigations
             WHERE investigation_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", investigationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new IncidentInvestigationView(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            NullableInt(reader, 3),
            NullableInt(reader, 4),
            NullableString(reader, 5),
            NullableString(reader, 6),
            NullableString(reader, 7),
            NullableString(reader, 8),
            NullableInt(reader, 9),
            NullableString(reader, 10),
            reader.IsDBNull(11) ? null : reader.GetDouble(11),
            NullableString(reader, 12),
            NullableString(reader, 13),
            NullableString(reader, 14),
            NullableInt(reader, 15),
            NullableString(reader, 16),
            NullableString(reader, 17),
            NullableString(reader, 18),
            NullableBool(reader, 19),
            NullableInt(reader, 20),
            reader.GetInt32(21),
            NullableString(reader, 22),
            NullableString(reader, 23),
            NullableString(reader, 24),
            NullableString(reader, 25),
            NullableString(reader, 26));
    }

    static int? NullableInt(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    static bool? NullableBool(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);

    static DateTime Parse(string value) =>
        DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
