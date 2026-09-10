namespace CursoAgentes.Engine.Coordination;

/// <summary>
/// Derecho temporal y exclusivo a procesar un recurso. LeaseToken crece cada
/// vez que cambia el propietario y permite reconocer a un dueño obsoleto.
/// </summary>
public sealed record ExecutionLease(
    string ResourceId,
    string OwnerId,
    long LeaseToken,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Puerto genérico de coordinación. No conoce workflows, agentes ni el tipo de
/// trabajo protegido; esas capas sólo aportan un ResourceId estable.
/// </summary>
public interface IExecutionLeaseStore
{
    Task InitializeAsync(CancellationToken ct);

    Task<ExecutionLease?> TryAcquireAsync(
        string resourceId,
        string ownerId,
        TimeSpan duration,
        CancellationToken ct);

    /// <summary>
    /// Lectura optimista para evitar trabajo inútil. TryAcquireAsync sigue
    /// siendo la única autoridad porque este resultado puede cambiar enseguida.
    /// </summary>
    Task<ExecutionLease?> GetActiveAsync(string resourceId, CancellationToken ct);

    Task<ExecutionLease?> RenewAsync(
        ExecutionLease lease,
        TimeSpan duration,
        CancellationToken ct);

    Task<bool> ReleaseAsync(ExecutionLease lease, CancellationToken ct);
}
