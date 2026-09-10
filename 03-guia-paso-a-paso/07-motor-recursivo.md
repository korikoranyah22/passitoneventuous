# Paso 7 · Motor recursivo (el corazón del ejemplo)

## Objetivo

Construir el **orquestador** que une dominio + LLM: `RecursiveWorkflowRunner`.
Es el archivo más importante del curso — leelo entero en
`src/CursoAgentes.Engine/Workflow/RecursiveWorkflowRunner.cs`.

## Concepto

El runner ejecuta el workflow y, en **cada transición**, persiste un evento
vía los command services. El estado del árbol NO vive solo en memoria: vive en
el event store. Si el proceso muere a mitad de camino, el árbol se reconstruye
releyendo los streams.

```text
RunAsync = StartAsync + ResumeAsync

StartAsync
  ├─ WorkflowRunCreated
  └─ WorkflowNodeCreated(raíz Pending)

ResumeAsync(runId)
  ├─ reconstruye run + raíz desde sus streams
  └─ ResumeNodeAsync(estado)                   ← RECURSIÓN
       ├─ Completed → restaura respuesta e hijos; cero LLM
       ├─ Pending   → planifica y persiste IDs + objetivos de hijos
       └─ Planned
            ├─ hoja   → Worker + Complete
            └─ padre → crea/reanuda hijos + Synthesizer + Complete
```

## El método recursivo, en detalle

```csharp
private async Task<NodeResult> ResumeNodeAsync(WorkflowNodeState state, ...)
{
    if (state.Status == Completed)
        return await ReadCompletedNodeAsync(state); // no llama agentes

    if (state.Status == Pending)
    {
        var plan = await _planner.PlanAsync(Context(state), ct);
        state = await Plan(state, plan); // persiste childIds Y childGoals
    }

    if (state.IsLeaf)
        return await AnswerAndComplete(state, ct);

    foreach (var childId in state.ChildrenIds)
    {
        var child = await ReadOrCreateFromPersistedPlan(state, childId, ct);
        children.Add(await ResumeNodeAsync(child, ...)); // recursión
    }

    return await SynthesizeAndComplete(state, children, ct);
}
```

`WorkflowExecutionReader` aplica eventos tipados directamente desde el event
store. El runner no usa el read model para decidir: una proyección puede estar
atrasada, mientras que el stream del aggregate es la fuente de verdad.

### Las cuatro cosas que tenés que notar

1. **La recursión y sus tres frenos**: `!plan.IsLeaf` (el LLM dijo "dividí"),
   `ctx.Depth < ctx.MaxDepth` (tope de profundidad — el freno duro), y
   `plan.SubGoals.Count > 0` (que haya algo que dividir). El `PlannerAgent`
   además recorta a `MaxChildrenPerNode` y trata el JSON inválido como hoja.
2. **Cada transición persiste**: `GuardResult` lanza si el dominio rechazó el
   comando. El workflow solo avanza si el evento quedó guardado.
3. **La invariante cross-aggregate la aplica el runner**: cuando el padre se
   completa, la recursión ya terminó → todos los hijos están `Completed` *por
   construcción*.
4. **El plan es un checkpoint completo**: `WorkflowNodePlanned` guarda los IDs
   y objetivos de los hijos. Si el proceso cae antes de crear uno, el objetivo
   no se pierde y el hijo puede recrearse exactamente desde el evento.

### Manejo de fallos

```csharp
try
{
    var root = await ResumeNodeAsync(rootState, steps, ct);
    await GuardResult(_runs.Handle(new CompleteWorkflowRun(runId, root.Answer), ct));
    return new WorkflowResult(runId, root.Answer, root, steps);
}
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    // Interrupción operativa: queda Running para ResumeAsync.
    throw;
}
catch (Exception ex)
{
    // El run queda Failed EN EL EVENT STORE, con el rastro de todo lo hecho antes.
    await _runs.Handle(new FailWorkflowRun(runId, ex.Message), ct);
    throw;
}
```

Un crash real o una cancelación deja el run `Running`; `ResumeAsync` continúa
desde ahí. Una falla de negocio/técnica no cancelada queda `Failed` y requiere
una política explícita diferente: el resume post-crash no reabre fallos.

La garantía es **no repetir fases ya persistidas**, no exactamente-once del
LLM. Si el proceso cae después de recibir una respuesta pero antes de guardar
`WorkflowNodeCompleted`, esa llamada puede repetirse. Para efectos externos se
necesita además idempotencia; para LLMs costosos puede agregarse un evento de
resultado intermedio antes de completar el nodo.

## Probalo

```bash
dotnet test --filter "FullyQualifiedName~RecursiveWorkflowRunnerTests"
```

Los siete tests (todos con LLM falso y event store en memoria) incluyen:

1. **`FullRun_WithScriptedLlm_BuildsTree_AndPersistsEverything`**: árbol de 3
   nodos, cada nodo con sus 3 eventos (Created+Planned+Completed), run
   Completed, 11 eventos totales.
2. **`Run_WithPlannerAlwaysDecomposing_StopsAtMaxDepth`**: el planner "infinito"
   + MaxDepth=2 → árbol acotado a 7 nodos y el run termina. **Prueba de
   terminación.**
3. **`Run_WhenLlmThrows_RunEndsFailed_InEventStore`**: LLM caído → run queda
   `Failed` con su evento persistido.
4. **`Resume_PlannedParent_CreatesMissingChildrenFromPersistedGoals`**:
   recupera hijos que todavía no tenían stream.
5. **`Resume_AfterCancellation_SkipsCompletedSubtree`**: una rama completa se
   restaura y sólo se ejecutan la rama pendiente y la síntesis.
6. **`Resume_CompletedRun_ReconstructsTreeWithoutCallingLlmOrAppendingEvents`**:
   reconstruye el árbol sin llamadas ni eventos nuevos.
7. **`Resume_RunCreatedWithoutRoot_RecreatesRootFromRunEvent`**: cubre el crash
   entre crear el aggregate del run y crear el aggregate raíz.

Probalo en dos procesos con PostgreSQL:

```bash
dotnet run --project src/CursoAgentes.App -- \
  --workflow-start "¿Cómo se reanuda un árbol?"

# copiá el runId impreso
dotnet run --project src/CursoAgentes.App -- --workflow-resume run-1234abcd
```

---

**Siguiente**: [Paso 8 · Proyecciones y read model](08-proyecciones-read-model.md)
