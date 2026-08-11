# Soluciones orientativas

> Estas son *orientaciones*, no la única respuesta. Compará tu solución, y si
> encontraste un camino distinto y válido, mejor.

## Ejercicio 1 · Cambiá el tope de profundidad

- Con `MaxDepth: 1`: la raíz (depth 0) se divide porque `0 < 1`; sus hijos
  (depth 1) son hojas porque `1 < 1` es falso. → 3 nodos.
- Con `MaxDepth: 0`: la raíz ya es `0 < 0` falso → todo el árbol es UNA hoja
  (1 nodo). El planner "quiere" dividir, pero el freno del runner se lo impide.
- Moraleja: `MaxDepth` es el freno duro; el LLM solo *propone*, el manifiesto
  *dispone*.

## Ejercicio 3 · Parallelizá los hijos

```csharp
var tasks = plan.SubGoals.Select(async (subGoal, i) =>
{
    var childId = childIds[i];
    await GuardResult(_nodes.Handle(
        new CreateWorkflowNode(childId, ctx.RunId, ctx.NodeId, ctx.Depth + 1, i, subGoal), ct));

    var childCtx = new AgentContext { RunId = ctx.RunId, NodeId = childId,
        Goal = subGoal, Depth = ctx.Depth + 1, MaxDepth = ctx.MaxDepth };

    var childResult = await ExecuteNodeAsync(childCtx, ct);
    return (childId, childResult);
});

var results = await Task.WhenAll(tasks);
foreach (var (childId, childResult) in results)
{
    children.Add(childResult);
    childAnswers[childId] = childResult.Answer;
}
```

- La invariante "el padre completa después que sus hijos" **sigue valiendo**:
  `Task.WhenAll` espera a todos antes de sintetizar.
- Lo que cambia: el orden de los eventos entre hijos ya no es determinista.
  **Esto es un punto clave del curso**: el event store tolera órdenes distintos
  (cada stream es independiente); un sistema CRUD con contadores globales
  tendría race conditions acá.

## Ejercicio 5 · Reintentos con backoff

```csharp
public sealed class RetryingLlmGateway : ILlmGateway
{
    private readonly ILlmGateway _inner;
    private readonly int _maxRetries;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _inner.CompleteAsync(request, ct);
            }
            catch (HttpRequestException ex) when (attempt < _maxRetries &&
                ex.StatusCode is System.Net.HttpStatusCode.TooManyRequests or
                    System.Net.HttpStatusCode.InternalServerError or
                    System.Net.HttpStatusCode.ServiceUnavailable)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay, ct);
            }
        }
    }
}
```

En DI: `services.AddSingleton<ILlmGateway>(sp => new RetryingLlmGateway(
    sp.GetRequiredService<OpenAiCompatibleGateway>(), 3));`

- **Importante**: el retry ocurre *antes* de que el motor persista cualquier
  evento de resultado, así que es seguro. Si reintentás *después* de persistir
  (ej. reenviando un comando ya aplicado), el guard del aggregate te lo
  rechaza — esa es la protección de idempotencia.

## Ejercicio 6 · Reanudación post-crash (esquema)

```csharp
// 1. Leer el run y saber su estado.
// 2. Leer los streams workflow-node-* del run (join por RunId en el payload
//    del evento NodeCreated, o filtrar por el read model).
// 3. Reconstruir cada nodo aplicando sus eventos (igual que LoadNodeStateAsync
//    de los tests).
// 4. Clasificar: Completed → usar su Answer; Planned → hay que continuar;
//    Pending → nunca se planificó → planificar primero.
// 5. Ejecutar solo los pendientes con el MISMO ExecuteNodeAsync.
```

El truco está en que `ExecuteNodeAsync` ya es **reanudable por diseño**: si
cada nodo solo se ejecuta cuando su estado está `Pending`/`Planned`, re-correr
el runner sobre el árbol existente "completa lo que falta" y los guards del
dominio rechazan lo que ya se hizo (un nodo `Completed` no se vuelve a
completar). El event store te da el árbol; el dominio te protege de repetir.

## Ejercicio 7 · HTTP API (esquema)

- `POST /api/runs`: validar `goal` no vacío → `runner.RunAsync` (o lanzar un
  background task) → `202 Accepted` con `{ runId }`.
- `GET /api/runs/{id}`: `WorkflowReadModelStore.GetRunAsync` + `GetNodesAsync`.
- `GET /api/runs/{id}/audit`: el SQL de `PrintAuditTrailAsync` devolviendo
  filas como JSON.
- Para runs largos: el patrón del repo real es fire-and-forget + un manager
  con estado (el runId queda registrado y la API consulta el read model para
  saber el progreso). Nunca bloquear el request HTTP durante minutos (los
  proxies cortan ~1800s).

## Ejercicio 8 · Proyección de resumen (esquema)

- Agregá `workflow_stats(fecha, runs, nodos, tokens)`.
- En la proyección, además del upsert por run/nodo, sumá al contador del día
  (con `INSERT … ON CONFLICT (fecha) DO UPDATE SET …`).
- Dos proyecciones sobre los mismos eventos: natural en event sourcing porque
  **cada read model sirve a una consulta distinta** y los eventos son la única
  fuente. En CRUD tendrías que mantener dos tablas sincronizadas a mano, con
  riesgo de divergencia — acá la sincronización es por construcción (eventos →
  proyecciones).
