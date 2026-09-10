using CursoAgentes.Domain.Workflow;
using CursoAgentes.Engine.Agents;
using Microsoft.Extensions.Logging;

namespace CursoAgentes.Engine.Workflow;

// ─────────────────────────────────────────────────────────────────────────────
// EL MOTOR: el workflow de nodos recursivos.
//
// Cómo funciona (la idea central del curso):
//
//   RunAsync(goal)
//     └─ crea el run + el nodo raíz
//     └─ ResumeNodeAsync(raíz)           ← RECURSIÓN
//          ├─ Planner: ¿dividir o responder directo?
//          ├─ si divide → crea hijos, ejecuta cada hijo (recursión), y luego
//          │             Synthesizer integra las respuestas de los hijos
//          └─ si es hoja → Worker responde directo
//
// CADA transición se persiste con un comando al aggregate correspondiente
// (WorkflowRunCommandService / WorkflowNodeCommandService). Es decir: el estado
// del árbol NO vive solo en memoria — vive en el event store. Si el proceso
// muere a mitad de camino, al arrancar de nuevo se puede reconstruir el árbol
// entero releyendo los streams. ESA es la razón por la que programamos agentes
// con event sourcing: los workflows de LLM son largos, caros y fallan — y el
// event store nos da auditoría y los datos para reanudar; este runner aporta
// la política explícita que interpreta Pending, Planned y Completed.
//
// Nota de diseño (lección del curso): las invariantes DE UN nodo las valida el
// aggregate (guards del dominio). La invariante ENTRE nodos ("un padre solo se
// completa cuando todos sus hijos están completos") la aplica ESTE orquestador,
// porque es la pieza que conoce el árbol completo.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RecursiveWorkflowRunner
{
    private readonly WorkflowRunCommandService _runs;
    private readonly WorkflowNodeCommandService _nodes;
    private readonly WorkflowExecutionReader _reader;
    private readonly PlannerAgent _planner;
    private readonly WorkerAgent _worker;
    private readonly SynthesizerAgent _synthesizer;
    private readonly WorkflowManifest _manifest;
    private readonly ILogger<RecursiveWorkflowRunner> _logger;

    public RecursiveWorkflowRunner(
        WorkflowRunCommandService runs,
        WorkflowNodeCommandService nodes,
        WorkflowExecutionReader reader,
        PlannerAgent planner,
        WorkerAgent worker,
        SynthesizerAgent synthesizer,
        WorkflowManifest manifest,
        ILogger<RecursiveWorkflowRunner> logger)
    {
        _runs = runs;
        _nodes = nodes;
        _reader = reader;
        _planner = planner;
        _worker = worker;
        _synthesizer = synthesizer;
        _manifest = manifest;
        _logger = logger;
    }

    /// <summary>
    /// Persiste el run y su nodo raíz sin ejecutar agentes. Esta frontera sirve
    /// para entregar el trabajo a otro proceso o demostrar una reanudación.
    /// </summary>
    public async Task<WorkflowRunHandle> StartAsync(
        string runId,
        string goal,
        CancellationToken ct)
    {
        var rootNodeId = $"n-{Guid.NewGuid():N}";
        await GuardResult(_runs.Handle(
            new StartWorkflowRun(runId, goal, rootNodeId),
            ct));
        await GuardResult(_nodes.Handle(
            new CreateWorkflowNode(
                rootNodeId,
                runId,
                ParentNodeId: null,
                Depth: 0,
                Order: 0,
                goal),
            ct));
        _logger.LogInformation(
            "Workflow {RunId} preparado con raíz {RootNodeId}.",
            runId,
            rootNodeId);
        return new WorkflowRunHandle(runId, rootNodeId, goal);
    }

    /// <summary>Ejecuta el workflow completo para un objetivo, dejando el run Completed en el event store.</summary>
    public async Task<WorkflowResult> RunAsync(string runId, string goal, CancellationToken ct)
    {
        _logger.LogInformation("▶ Iniciando workflow {RunId} — objetivo: «{Goal}»", runId, goal);
        await StartAsync(runId, goal, ct);
        return await ResumeAsync(runId, ct);
    }

    /// <summary>
    /// Reconstruye un run desde el event store y continúa solamente sus fases
    /// pendientes. Un run ya completado se devuelve sin llamar agentes.
    /// </summary>
    public async Task<WorkflowResult> ResumeAsync(string runId, CancellationToken ct)
    {
        var run = await _reader.ReadRunAsync(runId, ct)
            ?? throw new InvalidOperationException($"Workflow run '{runId}' was not found.");
        var steps = new List<string>();
        if (run.Status == WorkflowRunStatus.Failed)
        {
            throw new InvalidOperationException(
                $"Workflow run '{runId}' is Failed; post-crash resume only accepts Running or Completed runs.");
        }
        if (run.Status == WorkflowRunStatus.Completed)
        {
            var completedRoot = await ReadCompletedNodeAsync(run.RootNodeId, steps, ct);
            steps.Add($"[{run.RootNodeId}] árbol restaurado; cero llamadas a agentes");
            return new WorkflowResult(run.RunId, run.Answer!, completedRoot, steps);
        }
        if (run.Status != WorkflowRunStatus.Running)
        {
            throw new InvalidOperationException(
                $"Workflow run '{runId}' cannot resume from {run.Status}.");
        }

        try
        {
            var rootState = await _reader.ReadNodeAsync(run.RootNodeId, ct);
            if (rootState is null)
            {
                rootState = await GuardResult(_nodes.Handle(
                    new CreateWorkflowNode(
                        run.RootNodeId,
                        run.RunId,
                        ParentNodeId: null,
                        Depth: 0,
                        Order: 0,
                        run.Goal),
                    ct));
                steps.Add($"[{run.RootNodeId}] raíz recreada desde WorkflowRunCreated");
            }
            var root = await ResumeNodeAsync(rootState, steps, ct);
            await GuardResult(_runs.Handle(new CompleteWorkflowRun(runId, root.Answer), ct));

            _logger.LogInformation(
                "✔ Workflow {RunId} completado — {Nodes} nodos, respuesta final de {Chars} caracteres.",
                runId, CountNodes(root), root.Answer.Length);

            return new WorkflowResult(runId, root.Answer, root, steps);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Workflow {RunId} interrumpido; permanece Running para poder reanudarse.",
                runId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "✖ Workflow {RunId} falló — persistiendo WorkflowRunFailed.", runId);
            try
            {
                await GuardResult(_runs.Handle(new FailWorkflowRun(runId, ex.Message), ct));
            }
            catch (Exception failEx)
            {
                _logger.LogWarning(failEx, "No se pudo persistir el fallo del run {RunId}.", runId);
            }
            throw;
        }
    }

    // ── Recursión ────────────────────────────────────────────────────────────

    /// <summary>
    /// Avanza UN nodo según el checkpoint reconstruido. Completed sólo se lee;
    /// Pending planifica; Planned ejecuta hijos/worker y completa.
    /// </summary>
    private async Task<NodeResult> ResumeNodeAsync(
        WorkflowNodeState state,
        List<string> steps,
        CancellationToken ct)
    {
        if (state.Status == WorkflowNodeStatus.Completed)
            return await ReadCompletedNodeAsync(state, steps, ct);
        if (state.Status == WorkflowNodeStatus.Failed)
            throw new InvalidOperationException(
                $"Workflow node '{state.NodeId}' is Failed and cannot be resumed automatically.");
        if (state.Status == WorkflowNodeStatus.None)
            throw new InvalidOperationException("Cannot resume a node without persisted history.");

        var ctx = Context(state, steps);
        if (state.Status == WorkflowNodeStatus.Pending)
        {
            var plan = await _planner.PlanAsync(ctx, ct);
            var decomposes = !plan.IsLeaf
                && state.Depth < _manifest.MaxDepth
                && plan.SubGoals.Count > 0;
            var childIds = decomposes
                ? plan.SubGoals.Select(_ => $"n-{Guid.NewGuid():N}").ToArray()
                : [];
            var childGoals = decomposes ? plan.SubGoals.ToArray() : [];
            state = await GuardResult(_nodes.Handle(
                new PlanWorkflowNode(
                    state.NodeId,
                    IsLeaf: !decomposes,
                    childIds,
                    plan.Rationale,
                    childGoals),
                ct));
            steps.Add(
                $"[{state.NodeId}] plan persistido: "
                + (state.IsLeaf ? "hoja" : $"{state.ChildrenIds.Length} sub-objetivos"));
        }

        if (state.Status != WorkflowNodeStatus.Planned)
            throw new InvalidOperationException(
                $"Workflow node '{state.NodeId}' expected Planned but was {state.Status}.");

        ctx = Context(state, steps);
        if (state.IsLeaf)
        {
            var answer = await _worker.AnswerAsync(ctx, ct);
            steps.Add($"[{state.NodeId}] respuesta ({answer.Length} caracteres)");
            var completed = await GuardResult(_nodes.Handle(
                new CompleteWorkflowNode(state.NodeId, answer),
                ct));
            return Result(completed, [], answer);
        }

        var children = new List<NodeResult>();
        var childAnswers = new Dictionary<string, string>();
        for (var i = 0; i < state.ChildrenIds.Length; i++)
        {
            var childId = state.ChildrenIds[i];
            var child = await _reader.ReadNodeAsync(childId, ct);
            if (child is null)
            {
                if (state.ChildGoals.Length != state.ChildrenIds.Length)
                {
                    throw new InvalidOperationException(
                        $"Node '{state.NodeId}' has a legacy incomplete plan: child '{childId}' "
                        + "is missing and its goal was not persisted.");
                }
                child = await GuardResult(_nodes.Handle(
                    new CreateWorkflowNode(
                        childId,
                        state.RunId,
                        state.NodeId,
                        state.Depth + 1,
                        i,
                        state.ChildGoals[i]),
                    ct));
                steps.Add($"[{childId}] hijo pendiente recreado desde el plan persistido");
            }
            ValidateChild(state, child, i);
            var childResult = await ResumeNodeAsync(child, steps, ct);
            children.Add(childResult);
            childAnswers[childId] = childResult.Answer;
        }

        var synthesized = await _synthesizer.SynthesizeAsync(
            ctx with { ChildAnswers = childAnswers },
            ct);
        steps.Add($"[{state.NodeId}] síntesis de {childAnswers.Count} sub-respuestas");
        var completedParent = await GuardResult(_nodes.Handle(
            new CompleteWorkflowNode(state.NodeId, synthesized),
            ct));
        return Result(completedParent, children, synthesized);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Un comando rechazado por el dominio (guard o stream inexistente) es un error del workflow.</summary>
    private async Task<NodeResult> ReadCompletedNodeAsync(
        string nodeId,
        List<string> steps,
        CancellationToken ct)
    {
        var state = await _reader.ReadNodeAsync(nodeId, ct)
            ?? throw new InvalidOperationException($"Workflow node '{nodeId}' was not found.");
        return await ReadCompletedNodeAsync(state, steps, ct);
    }

    private async Task<NodeResult> ReadCompletedNodeAsync(
        WorkflowNodeState state,
        List<string> steps,
        CancellationToken ct)
    {
        if (state.Status != WorkflowNodeStatus.Completed)
        {
            throw new InvalidOperationException(
                $"Workflow node '{state.NodeId}' expected Completed but was {state.Status}.");
        }

        var children = new List<NodeResult>();
        foreach (var childId in state.ChildrenIds)
            children.Add(await ReadCompletedNodeAsync(childId, steps, ct));
        steps.Add($"[{state.NodeId}] resultado restaurado desde eventos");
        return Result(state, children, state.Answer!);
    }

    private AgentContext Context(WorkflowNodeState state, List<string> steps) => new()
    {
        RunId = state.RunId,
        NodeId = state.NodeId,
        Goal = state.Goal,
        Depth = state.Depth,
        MaxDepth = _manifest.MaxDepth,
        Steps = steps,
    };

    private static NodeResult Result(
        WorkflowNodeState state,
        IReadOnlyList<NodeResult> children,
        string answer) => new(
            state.NodeId,
            state.Goal,
            state.Depth,
            state.IsLeaf,
            answer,
            children,
            state.Rationale);

    private static void ValidateChild(
        WorkflowNodeState parent,
        WorkflowNodeState child,
        int order)
    {
        if (child.RunId != parent.RunId
            || child.ParentNodeId != parent.NodeId
            || child.Depth != parent.Depth + 1
            || child.Order != order)
        {
            throw new InvalidOperationException(
                $"Child node '{child.NodeId}' does not match persisted plan of '{parent.NodeId}'.");
        }
        if (parent.ChildGoals.Length == parent.ChildrenIds.Length
            && child.Goal != parent.ChildGoals[order])
        {
            throw new InvalidOperationException(
                $"Child node '{child.NodeId}' goal differs from the persisted plan.");
        }
    }

    private static async Task<TState> GuardResult<TState>(Task<Eventuous.Result<TState>> handleTask)
        where TState : class, new()
    {
        var result = await handleTask;
        if (result.TryGet(out var ok)) return ok.State;
        throw new InvalidOperationException(
            $"Comando rechazado por el dominio: {result.Exception?.Message ?? "(sin detalle)"}");
    }

    private static int CountNodes(NodeResult node) => 1 + node.Children.Sum(CountNodes);
}
