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
var tasks = state.ChildrenIds.Select(async (childId, i) =>
{
    var child = await _reader.ReadNodeAsync(childId, ct)
        ?? await GuardResult(_nodes.Handle(new CreateWorkflowNode(
            childId, state.RunId, state.NodeId, state.Depth + 1, i,
            state.ChildGoals[i]), ct));
    var childSteps = new List<string>(); // no compartir List entre tareas
    var childResult = await ResumeNodeAsync(child, childSteps, ct);
    return (childId, childResult, childSteps);
});

var results = await Task.WhenAll(tasks);
foreach (var (childId, childResult, childSteps) in results)
{
    children.Add(childResult);
    childAnswers[childId] = childResult.Answer;
    steps.AddRange(childSteps); // merge determinista en orden de ChildrenIds
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

- **Importante**: el retry ocurre *antes* de que el motor persista un evento de
  resultado, por lo que no duplica esa transición de dominio. No vuelve
  idempotente al proveedor: cada intento puede consumir tokens y devolver otro
  texto. Si repetís un comando ya aplicado, el guard o la semántica idempotente
  del aggregate debe proteger la historia por separado.

## Ejercicio 6 · Reanudación post-crash

```csharp
// 1. Leer el run y saber su estado.
// 2. Leer la raíz indicada por WorkflowRunCreated.
// 3. Reconstruir cada nodo aplicando sus eventos tipados.
// 4. Completed → restaurar Answer e hijos, sin agentes.
// 5. Pending → planificar y persistir IDs + objetivos de hijos.
// 6. Planned → crear hijos faltantes, recursar o completar la hoja.
// 7. Sintetizar el padre sólo cuando todos sus hijos están Completed.
```

La solución ejecutable está en `WorkflowExecutionReader` y
`RecursiveWorkflowRunner.ResumeAsync`. `StartAsync` permite persistir el run y
la raíz sin comenzar los agentes. El detalle decisivo es que el evento de plan
guarda también `ChildGoals`: IDs solos no alcanzan si el crash ocurre antes de
crear el stream de un hijo.

Un nodo `Completed` no vuelve a invocar planner, worker ni synthesizer. Un nodo
`Planned` sí puede repetir una llamada cuyo resultado todavía no llegó a un
evento; event sourcing evita repetir trabajo **persistido**, no vuelve
exactly-once a un I/O no idempotente.

## Ejercicio 7 · Takeover y fencing estricto (esquema)

- La base ya resuelve replay del cliente, recovery y exclusión temporal con
  `IExecutionLeaseStore`. Dos scanners pueden descubrir el mismo candidato,
  pero sólo el ganador de `TryAcquireAsync` ejecuta; el token aumenta durante
  un takeover.
- La cancelación al perder una renovación es cooperativa: un proceso detenido
  por el runtime o la máquina puede despertar sin haber observado el token. Por
  eso propagá `(resourceId, ownerId, leaseToken)` en el contexto del runner.
- Antes de cada append, hacé una operación atómica que valide que el token siga
  siendo el vigente. Un token menor debe fallar aunque el proceso conserve
  objetos en memoria. Para I/O costoso, validá también justo antes de llamar y
  conservá idempotencia/checkpoints porque siempre queda una ventana física.
- Probá el caso A(token 1) → pausa → vencimiento → B(token 2) → A despierta.
  B puede escribir; A debe recibir un error de fencing y no producir una nueva
  transición. Esa prueba diferencia un lease útil de una garantía completa.
- Aun con lease, una caída entre respuesta LLM y evento puede repetir esa
  llamada. Cerrá la ventana con un checkpoint de intento/resultado o con una
  clave idempotente si el proveedor la soporta.

## Ejercicio 8 · Proyección de resumen (esquema)

- Agregá `workflow_stats(fecha, runs, nodos, tokens)`.
- En la proyección, además del upsert por run/nodo, sumá al contador del día
  (con `INSERT … ON CONFLICT (fecha) DO UPDATE SET …`).
- Dos proyecciones sobre los mismos eventos: natural en event sourcing porque
  **cada read model sirve a una consulta distinta** y los eventos son la única
  fuente. En CRUD tendrías que mantener dos tablas sincronizadas a mano. Acá
  cada vista se puede reconstruir con replay y checkpoint; aun así, una
  proyección defectuosa o detenida puede divergir operacionalmente.

## Ejercicio 9 · Routing por perfiles y failover

El perfil expresa el trabajo:

```csharp
new LlmRouteProfile
{
    Name = "critical-judge",
    Route = new RouteRequest
    {
        RequiredTags = ["critic", "reasoning", "structured-output"]
    },
    PreferredModels = ["primary-model", "backup-model"],
    Execution = new LlmExecutionOptions
    {
        MaxAttemptsPerRoute = 2,
        MaxRoutes = 2,
        BaseRetryDelay = TimeSpan.Zero
    }
};
```

- El gateway declara identidad, modelos, tags, prioridad y disponibilidad.
- El perfil declara restricciones y preferencias del workload.
- El executor clasifica fallos y conserva el historial de intentos.
- HTTP 503 es transitorio: `primary → primary → backup`.
- HTTP 401 es permanente: `primary → backup`.

Una ruta explícita no saltea constraints porque es una preferencia operacional,
no un bypass de seguridad. MiyuAgents debe conservar selección, clasificación y
contratos; el producto host debe persistir configuración, gates y decisiones
de negocio específicas.

## Ejercicio 10 · Frontera determinista e idempotencia

Una secuencia posible:

```text
IncidentInvestigationStarted
IncidentSignalRecorded
IncidentAnalysisRecorded
IncidentCritiqueRecorded
IncidentActionDecided(action, policyVersion)
IncidentActionParked(failure, attempts)
IncidentActionRetryRequested(requestId, requestedBy, reason, retryNumber)
IncidentOpened(externalId, idempotencyKey)
```

El replay aplica `When(event)` y reconstruye estado: no ejecuta handlers de I/O.
El comando `ConfirmIncidentOpened` verifica que la acción decidida lo permita.
Si recibe otra vez la misma clave e identificador externo, no produce un evento
nuevo; si recibe valores distintos, rechaza el conflicto. El adaptador externo
usa `incident-investigation:{InvestigationId}` y un retry devuelve el mismo
incidente en vez de crear otro.

La reacción captura sólo `IncidentActionPortException`: si es transitoria,
reintenta con backoff hasta `MaxAttempts`; si es permanente o se agota, emite
`IncidentActionParked`. Una excepción no clasificada se propaga para que la
suscripción no confirme el checkpoint por error.

La recuperación usa un comando separado que sólo acepta `ActionParked` y emite
`IncidentActionRetryRequested`; no ejecuta el efecto directamente. El estado
reconstruido conserva los `RequestId` ya vistos, por lo que repetir uno es
idempotente aun si el caso ya completó. Un `RequestId` nuevo sobre un caso
completado se rechaza: no es una nueva orden de negocio implícita.

No conviene guardar indiscriminadamente el razonamiento interno completo:

- puede contener datos sensibles;
- cambia entre proveedores y no es un contrato estable;
- aumenta mucho el event store;
- no vuelve determinista el replay.

Sí conviene guardar artefactos estructurados, evidencias citadas, perfil/ruta/
modelo efectivos, intentos, versión del prompt o política y hashes cuando hagan
falta para trazabilidad.

## Ejercicio 11 · Pipeline versus grafo fijo

La correspondencia esperada es:

| Concepto | Pipeline | Grafo fijo |
|---|---|---|
| señal normalizada | `PipelineContext.SharedData` | artefacto `status-signal` |
| contrato inválido | `PipelineStageResult.Abort` | `NodeSignal.Failed` |
| ruta LLM | detalle del resultado del stage | artefacto `llm-execution` |
| decisión | entrada `incident-decision` | artefacto `incident-decision` |
| efecto | entrada `incident-action-receipt` | artefacto del mismo tipo |

Para un paso estrictamente lineal, el pipeline normalmente requiere menos
estructura: una clase de stage, prioridad y una clave compartida. El grafo fijo
es más expresivo cuando el nuevo paso debe tener señal, traza, transcript o ser
el punto de composición de un subworkflow.

`resumable` se justifica si una etapa debe suspenderse y continuar después de
recibir una respuesta externa o humana. La recursión se justifica sólo si la
ejecución descubre subobjetivos de la misma forma, con progreso medible y
límites de profundidad o presupuesto. Agregar pasos conocidos no es recursión.

## Ejercicio 12 · Objetivos recursivos acotados en MiyuAgents

La estructura esperada conserva dos niveles distintos:

```text
WorkflowNode(sequence)
├── RecursiveObjectiveNode<ResearchState>
├── RecursiveObjectiveNode<DraftState>
└── RecursiveObjectiveNode<ReviewState>
```

La secuencia exterior expresa etapas conocidas. Cada nodo interior recursa
porque desconoce de antemano cuántas refinaciones necesitará para satisfacer su
objetivo. Anidar nodos crea estructura de control; recursar transforma repetidas
veces un estado del mismo tipo.

Los indicadores de progreso del ejemplo son:

| Etapa | Estado que progresa | Gate determinista |
|---|---|---|
| evidencia | categorías cubiertas | están `cost`, `reliability` y `operations` |
| borrador | número/contenido de revisión | secciones requeridas + todas las citas |
| red-team | ronda, bloqueos y texto | cero bloqueos + trade-off explícito |

Si el refiner devuelve el mismo estado, el cycle key vuelve a repetirse mientras
sigue activo y el resultado es `NodeSignal.Failed`. Con ciclos desactivados, el
trampoline continúa hasta que `MaxDepth` o `MaxCalls` lo corta. Ninguna de las
dos protecciones depende del proveedor LLM.

Para agregar `rollback-plan`, el evaluador suma el criterio cuando el texto no
contiene esa sección. El refiner puede necesitar otra revisión para producirla,
pero el criterio permanece fuera del prompt y vuelve a verificarse sobre el
resultado estructurado.

Al usar un LLM real, sólo cambia el callback `refiner`:

```csharp
async (frame, assessment, ct) =>
{
    var execution = await executor.CompleteAsync(
        BuildRevisionRequest(frame.State, assessment.UnmetCriteria),
        "structured-brief-writer",
        ct);
    return ParseDraftState(execution.Response.Content, frame.State.Revision + 1);
}
```

Para reanudar tras un crash hacen falta, como mínimo, el estado tipado actual,
la evaluación que motivó la siguiente refinación, el índice de llamada y la
versión de política. También conviene persistir perfil/ruta/modelo e intentos.
Eso es una capacidad durable adicional: la recursión en memoria por sí sola no
es event sourcing ni garantiza resume.

El LLM propone una revisión potencialmente no determinista. El evaluador y los
presupuestos autorizan el corte porque son reglas inspeccionables, testeables y
estables bajo replay.

## Ejercicio 13 · IA defensiva con autoridad acotada

La separación esperada es:

```text
telemetría sintética
  → detector determinista
  → evaluación LLM
  → gate de contrato y citas
  → crítica LLM
  → política versionada
  → caso humano idempotente
```

Sin el mismatch de release queda una señal menos severa y la política ya no
cumple la combinación `release-integrity + transmission-integrity` exigida
para revisión urgente.
No alcanza con que el LLM mantenga una hipótesis alarmante: la acción se calcula
sólo desde findings deterministas y una crítica validada.

Una cita `finding-inventado` falla en `ValidateAssessment`. El pipeline aborta
antes del crítico y el grafo produce `NodeSignal.Failed`; en ninguno de los dos
casos se abre el case port. Ese gate convierte los IDs de evidencia en una
capability que el modelo no puede fabricar.

El ciclo recursivo se prueba haciendo que el refiner no cambie su cycle key. Si
la detección de ciclos está deshabilitada, debe fallar por `MaxDepth` o
`MaxCalls`. La falla segura es preferible a aceptar una evaluación incompleta.

La idempotencia usa `provisional-audit:{runId}`. Dos ejecuciones con el mismo ID
devuelven el mismo caso; IDs distintos representan investigaciones distintas y
crean dos. Esta regla vive en el puerto determinista, no en el texto del prompt.

Para sumar `configuration-drift`, el cambio mínimo coherente incluye:

- una constante de categoría;
- una regla reproducible que cite records concretos;
- inclusión en el criterio de cobertura recursiva;
- tests positivos, negativos y de input inválido;
- una actualización explícita de la política si la categoría cambia severidad.

Al reemplazar gateways offline por Ollama, los perfiles deben seguir exigiendo
`private`. Conviene usar `AllowFallback = false` para telemetría no sanitizada y
registrar únicamente rutas aprobadas por el host. El hecho de que dos modelos
coincidan no otorga autoridad: `RequiresHumanApproval` y
`AutomaticMutationAllowed` siguen siendo resultados de política, no de
consenso agéntico.
