using System.Collections.Concurrent;
using Eventuous;

namespace CursoAgentes.Tests.Testing;

// ─────────────────────────────────────────────────────────────────────────────
// EVENT STORE EN MEMORIA — una implementación mínima de IEventStore para
// testear los command services y el motor SIN Postgres.
//
// Es un artefacto de enseñanza: implementar IEventStore a mano te muestra qué
// contrato tiene que cumplir un event store de verdad:
//   • AppendEvents  → escribir eventos en un stream con control de versión
//                     esperada (optimistic concurrency).
//   • ReadEvents    → leer los eventos de un stream en orden.
//   • StreamExists  → saber si un stream ya existe.
//
// El PostgresStore real (Eventuous.Postgresql) hace exactamente lo mismo pero
// con tablas, transacciones y la versión esperada resuelta por SQL.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class InMemoryEventStore : IEventStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<StreamEvent>> _streams = new();
    private long _nextGlobalPosition;

    public Task<bool> StreamExists(StreamName stream, CancellationToken ct)
    {
        lock (_lock)
            return Task.FromResult(_streams.ContainsKey(stream.ToString()));
    }

    public Task<AppendEventsResult> AppendEvents(
        StreamName stream,
        ExpectedStreamVersion expectedVersion,
        IReadOnlyCollection<NewStreamEvent> events,
        CancellationToken ct)
    {
        lock (_lock)
        {
            var key = stream.ToString();
            _streams.TryGetValue(key, out var existing);

            var currentVersion = existing is null ? -1 : existing[^1].Revision;

            // Optimistic concurrency: si el llamador esperaba otra versión, fallamos.
            var expectedValue = expectedVersion.Value;
            if (expectedValue != -2 && expectedValue != currentVersion)
                throw new AppendToStreamException(
                    $"Expected version {expectedValue} but stream '{key}' is at {currentVersion}.",
                    new InvalidOperationException("Versión esperada no coincide con la versión actual del stream."));

            existing ??= [];
            foreach (var e in events)
            {
                existing.Add(new StreamEvent(
                    e.Id,
                    e.Payload,
                    e.Metadata,
                    ContentType: "application/json",
                    Revision: currentVersion + 1,
                    Created: DateTime.UtcNow,
                    FromArchive: false));
                currentVersion++;
                _nextGlobalPosition++;
            }

            _streams[key] = existing;
            return Task.FromResult(new AppendEventsResult((ulong)_nextGlobalPosition, currentVersion));
        }
    }

    public Task<AppendEventsResult[]> AppendEvents(
        IReadOnlyCollection<NewStreamAppend> appends, CancellationToken ct)
    {
        // Para el curso alcanza con el append de a-un-stream; el batch no se usa.
        var results = appends
            .Select(a => AppendEvents(a.StreamName, a.ExpectedVersion, a.Events, ct).GetAwaiter().GetResult())
            .ToArray();
        return Task.FromResult(results);
    }

    public Task<StreamEvent[]> ReadEvents(
        StreamName stream, StreamReadPosition start, int count, bool fromEnd, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(stream.ToString(), out var events))
                return Task.FromResult(Array.Empty<StreamEvent>());

            var result = fromEnd
                ? events.Skip(Math.Max(0, events.Count - count)).ToArray()
                : events.Skip((int)start.Value).Take(count).ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<StreamEvent[]> ReadEventsBackwards(
        StreamName stream, StreamReadPosition start, int count, bool fromEnd, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_streams.TryGetValue(stream.ToString(), out var events))
                return Task.FromResult(Array.Empty<StreamEvent>());

            var result = events.AsEnumerable().Reverse()
                .Skip((int)start.Value).Take(count).ToArray();
            return Task.FromResult(result);
        }
    }

    public Task TruncateStream(
        StreamName stream, StreamTruncatePosition truncatePosition,
        ExpectedStreamVersion expectedVersion, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(stream.ToString(), out var events))
            {
                var keep = events.Take((int)truncatePosition.Value).ToList();
                _streams[stream.ToString()] = keep;
            }
            return Task.CompletedTask;
        }
    }

    public Task DeleteStream(StreamName stream, ExpectedStreamVersion expectedVersion, CancellationToken ct)
    {
        lock (_lock)
        {
            _streams.Remove(stream.ToString());
            return Task.CompletedTask;
        }
    }
}
