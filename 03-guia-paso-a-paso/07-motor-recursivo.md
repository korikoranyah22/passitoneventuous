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

```
RunAsync(objetivo)
  ├─ StartWorkflowRun        → WorkflowRunCreated
  ├─ CreateWorkflowNode(raíz)→ WorkflowNodeCreated
  └─ ExecuteNodeAsync(raíz)                        ← RECURSIÓN
       ├─ PlannerAgent.PlanAsync → NodePlan (¿hoja o divide?)
       ├─ si divide:
       │    PlanWorkflowNode(no-hoja) → WorkflowNodePlanned
       │    para cada sub-objetivo: CreateWorkflowNode + ExecuteNodeAsync(hijo)
       │    SynthesizerAgent.SynthesizeAsync(childrenAnswers)
       │    CompleteWorkflowNode → WorkflowNodeCompleted
       └─ si es hoja:
            PlanWorkflowNode(hoja) → WorkflowNodePlanned
            WorkerAgent.AnswerAsync
            CompleteWorkflowNode → WorkflowNodeCompleted
  ├─ CompleteWorkflowRun     → WorkflowRunCompleted
```

## El método recursivo, en detalle

```csharp
private async Task<NodeResult> ExecuteNodeAsync(AgentContext ctx, CancellationToken ct)
{
    // ── Fase 1: PLANIFICAR ── ¿se divide o se responde directo?
    var plan = await _planner.PlanAsync(ctx, ct);
    ctx.Steps.Add($"[{ctx.NodeId}] plan: {(plan.IsLeaf ? "hoja" : $"{plan.SubGoals.Count} sub-objetivos")} — {plan.Rationale}");

    if (!plan.IsLeaf && ctx.Depth < ctx.MaxDepth && plan.SubGoals.Count > 0)
    {
        // ── Fase 2a: DIVIDIR ──
        var childIds = plan.SubGoals.Select((_, i) => $"n-{Guid.NewGuid():N}").ToArray();
        await GuardResult(_nodes.Handle(
            new PlanWorkflowNode(ctx.NodeId, IsLeaf: false, childIds, plan.Rationale), ct));

        var children = new List<NodeResult>();
        var childAnswers = new Dictionary<string, string>();

        for (var i = 0; i < plan.SubGoals.Count; i++)
        {
            var childId = childIds[i];
            await GuardResult(_nodes.Handle(
                new CreateWorkflowNode(childId, ctx.RunId, ctx.NodeId, ctx.Depth + 1, i, plan.SubGoals[i]), ct));

            var childCtx = new AgentContext { RunId = ctx.RunId, NodeId = childId,
                Goal = plan.SubGoals[i], Depth = ctx.Depth + 1, MaxDepth = ctx.MaxDepth };

            // ── RECURSIÓN: el hijo repite TODO el proceso ──
            var childResult = await ExecuteNodeAsync(childCtx, ct);
            children.Add(childResult);
            childAnswers[childId] = childResult.Answer;
        }

        // ── Fase 3: SINTETIZAR ── (acá todos los hijos ya están Completed)
        var synthCtx = ctx with { ChildAnswers = childAnswers };
        var synthesized = await _synthesizer.SynthesizeAsync(synthCtx, ct);
        await GuardResult(_nodes.Handle(new CompleteWorkflowNode(ctx.NodeId, synthesized), ct));

        return new NodeResult(ctx.NodeId, ctx.Goal, ctx.Depth, IsLeaf: false, synthesized, children, plan.Rationale);
    }

    // ── Fase 2b: HOJA ── el Worker responde directo.
    await GuardResult(_nodes.Handle(new PlanWorkflowNode(ctx.NodeId, IsLeaf: true, [], plan.Rationale), ct));
    var answer = await _worker.AnswerAsync(ctx, ct);
    await GuardResult(_nodes.Handle(new CompleteWorkflowNode(ctx.NodeId, answer), ct));

    return new NodeResult(ctx.NodeId, ctx.Goal, ctx.Depth, IsLeaf: true, answer, [], plan.Rationale);
}
```

### Las tres cosas que tenés que notar

1. **La recursión y sus tres frenos**: `!plan.IsLeaf` (el LLM dijo "dividí"),
   `ctx.Depth < ctx.MaxDepth` (tope de profundidad — el freno duro), y
   `plan.SubGoals.Count > 0` (que haya algo que dividir). El `PlannerAgent`
   además recorta a `MaxChildrenPerNode` y trata el JSON inválido como hoja.
2. **Cada transición persiste**: `GuardResult` lanza si el dominio rechazó el
   comando. El workflow solo avanza si el evento quedó guardado.
3. **La invariante cross-aggregate la aplica el runner**: cuando el padre se
   completa, la recursión ya terminó → todos los hijos están `Completed` *por
   construcción*.

### Manejo de fallos

```csharp
try
{
    var root = await ExecuteNodeAsync(rootContext, ct);
    await GuardResult(_runs.Handle(new CompleteWorkflowRun(runId, root.Answer), ct));
    return new WorkflowResult(runId, root.Answer, root, rootContext.Steps);
}
catch (Exception ex)
{
    // El run queda Failed EN EL EVENT STORE, con el rastro de todo lo hecho antes.
    await _runs.Handle(new FailWorkflowRun(runId, ex.Message), ct);
    throw;
}
```

Si el LLM se cae a mitad de camino: el run queda `Failed`, y los eventos de los
nodos que sí se completaron siguen ahí. Auditoría + reanudación.

## Probalo

```bash
dotnet test --filter "FullyQualifiedName~RecursiveWorkflowRunnerTests"
```

Los tres tests (todos con LLM falso y event store en memoria):

1. **`FullRun_WithScriptedLlm_BuildsTree_AndPersistsEverything`**: árbol de 3
   nodos, cada nodo con sus 3 eventos (Created+Planned+Completed), run
   Completed, 11 eventos totales.
2. **`Run_WithPlannerAlwaysDecomposing_StopsAtMaxDepth`**: el planner "infinito"
   + MaxDepth=2 → árbol acotado a 7 nodos y el run termina. **Prueba de
   terminación.**
3. **`Run_WhenLlmThrows_RunEndsFailed_InEventStore`**: LLM caído → run queda
   `Failed` con su evento persistido.

---

**Siguiente**: [Paso 8 · Proyecciones y read model](08-proyecciones-read-model.md)
