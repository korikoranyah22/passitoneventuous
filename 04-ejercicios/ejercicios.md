# Ejercicios

Ocho ejercicios ordenados por dificultad. Cada uno tiene un **objetivo**, los
**archivos que vas a tocar** y una **pista**. Las soluciones orientativas están
en `soluciones-orientativas.md` (leelas después de intentarlo, no antes 😉).

---

## Nivel fácil

### Ejercicio 1 · Cambiá el tope de profundidad

**Objetivo**: entender cómo el manifiesto controla el comportamiento del
workflow sin tocar código.

1. En `src/CursoAgentes.App/appsettings.json`, poné `"Workflow": { "MaxDepth": 1 }`.
2. Corré `dotnet run --project src/CursoAgentes.App`.
3. Observá el árbol: ¿cuántos niveles tiene? ¿Y con `MaxDepth: 5`?

**Preguntas**:
- ¿Por qué `MaxDepth: 1` sigue produciendo 3 nodos (raíz + 2 hijos)?
- ¿Qué pasaría con `MaxDepth: 0`? Probálo.

### Ejercicio 2 · El LLM falso contra el real

**Objetivo**: ver la diferencia entre simulación y producción.

1. Corré la demo con el fake (default) y guardá la salida.
2. Si tenés Ollama: cambiá `"Llm": { "Provider": "OpenAI", "BaseUrl": "http://localhost:11434/v1", "Model": "llama3.2" }` y corré de nuevo.
3. Compará: árbol, respuestas, y la *forma* de las respuestas (el fake es determinista; el real no).

**Pregunta**: ¿en qué partes del flujo el determinismo del fake oculta problemas que sí tendrías en producción? (Pista: JSON del planner.)

---

## Nivel medio

### Ejercicio 3 · Parallelizá los hijos

**Objetivo**: que los hijos de un nodo corran en paralelo.

En `RecursiveWorkflowRunner.ExecuteNodeAsync`, el loop actual es:

```csharp
for (var i = 0; i < plan.SubGoals.Count; i++)
{
    // … crea el hijo y lo ejecuta con await
}
```

Cambialo para ejecutar los hijos con `Task.WhenAll`. Cuidado con:

- El orden de `ctx.Steps` (deja de ser lineal).
- El orden de los eventos en el store (los hijos ya no terminan en orden).
- Las invariantes: ¿sigue garantizándose "el padre completa después que sus hijos"?

**Pista**: guardá las tareas en una lista, `await Task.WhenAll(tasks)`, y recién
después armá `children`/`childAnswers` recorriendo las tareas terminadas.

### Ejercicio 4 · Costo por run

**Objetivo**: llevar la telemetría de tokens al read model.

`LlmResponse` trae `InputTokens` y `OutputTokens`. Hoy se descartan. Agregá un
evento `WorkflowNodeLlmCallRecorded(NodeId, InputTokens, OutputTokens,
Model)` emitido por el motor (o mejor: un agregado de telemetría), y mostrá en
la demo el total de tokens (y un costo estimado) por run.

**Preguntas**:
- ¿Dónde emitís el evento: dentro del aggregate de nodo o en un aggregate
  aparte? ¿Por qué?
- ¿Qué pasa con los tokens si reintentás una llamada? (Esto es *exactamente*
  el problema que resuelve la auditoría por eventos.)

### Ejercicio 5 · Reintentos con backoff

**Objetivo**: hacer resiliente el gateway sin tocar el motor.

Creá un `RetryingLlmGateway : ILlmGateway` que **envuelva** a otro gateway
(patrón decorator):

```csharp
public sealed class RetryingLlmGateway : ILlmGateway
{
    private readonly ILlmGateway _inner;
    private readonly int _maxRetries;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        // intentá _maxRetries veces con backoff exponencial;
        // si el proveedor responde 429/5xx, esperá y reintentá
    }
}
```

Registralo en DI envolviendo al gateway real. El motor no cambia ni una línea.

**Pregunta**: ¿qué diferencia hay entre reintentar una llamada **idempotente**
(completado) y una que **ya persistió un evento**? (Hint: ver ejercicio 4.)

---

## Nivel difícil

### Ejercicio 6 · Reanudación post-crash (la joya)

**Objetivo**: demostrar el valor del event sourcing: retomar un run a mitad de
camino.

Construí un `ResumeWorkflowRunner` que:

1. Lea el stream del run (`workflow-run-{id}`) y los de sus nodos.
2. Reconstruya el árbol: qué nodos están `Completed` (con su respuesta), cuáles
   `Planned` sin completar, cuáles `Pending`.
3. **Continúe solo desde los nodos pendientes** (planificar/completar lo que
   falta) sin re-ejecutar lo ya hecho.

**Pista**: usá el SQL de auditoría del paso 9 o el `InMemoryEventStore` como
referencia de lectura. El estado reconstruido de cada nodo te dice dónde
seguir: un nodo `Planned` con hijos → sus hijos están creados pero quizás
incompletos; un nodo `Pending` → nunca se planificó.

**Para probar**: corré la demo con el fake, cortala (Ctrl+C) a mitad de camino
(poné `MaxDepth: 4` y un sleep en el worker para tener tiempo), y después
reanudá el mismo `runId`.

### Ejercicio 7 · HTTP API

**Objetivo**: exponer el workflow como servicio.

Agregá un proyecto `CursoAgentes.Api` (ASP.NET Core) con:

- `POST /api/runs` → body `{ "goal": "…" }` → crea y dispara el run (fire and
  forget o espera síncrona) → devuelve `runId`.
- `GET /api/runs/{runId}` → lee el read model (`WorkflowReadModelStore`).
- `GET /api/runs/{runId}/audit` → devuelve los eventos del run (la auditoría
  como JSON).

**Preguntas**:
- ¿Dónde vive la validación del body? (Mirá el patrón de controllers del repo
  real en el apéndice.)
- ¿Cómo manejás un run que tarda minutos? (Pista: el patrón del repo real usa
  fire-and-forget + un manager que trackea el progreso.)

### Ejercicio 8 · Proyección de resumen

**Objetivo**: una segunda proyección para una consulta distinta.

Hoy el read model tiene por-run y por-nodo. Agregá una tabla `workflow_stats`
con una fila por día: `fecha, runs_completados, runs_fallidos,
total_nodos, total_tokens` (si hiciste el ejercicio 4). Necesitás una segunda
suscripción o extender la proyección existente.

**Pregunta**: ¿por qué tener DOS proyecciones sobre los MISMOS eventos es
natural en event sourcing y sería raro en CRUD?

---

## Rúbrica de autoevaluación

| Nivel | Hito |
|---|---|
| ✅ Básico | Ejercicios 1-2: entendés el rol del manifiesto y del fake |
| ✅ Intermedio | Ejercicios 3-5: tocás el motor y la resiliencia sin romper invariantes |
| ✅ Avanzado | Ejercicios 6-8: usás el event store como fuente para reanudar, exponer y derivar |
