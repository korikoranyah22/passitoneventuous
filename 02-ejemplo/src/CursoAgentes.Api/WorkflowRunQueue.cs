using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CursoAgentes.Api;

/// <summary>
/// Cola local del host. Deduplica por run mientras está encolado o ejecutándose.
/// No pretende reemplazar la durabilidad: los checkpoints viven en Eventuous.
/// </summary>
public sealed class WorkflowRunQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _scheduled = new();

    public bool IsScheduled(string runId) => _scheduled.ContainsKey(runId);

    public async ValueTask<bool> ScheduleAsync(
        string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!_scheduled.TryAdd(runId, 0)) return false;

        try
        {
            await _channel.Writer.WriteAsync(runId, ct);
            return true;
        }
        catch
        {
            _scheduled.TryRemove(runId, out _);
            throw;
        }
    }

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void MarkFinished(string runId) => _scheduled.TryRemove(runId, out _);
}
