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
//     └─ ExecuteNodeAsync(raíz)          ← RECURSIÓN
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
// event store nos da auditoría, reanudación y depuración gratis.
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
    private readonly PlannerAgent _planner;
    private readonly WorkerAgent _worker;
    private readonly SynthesizerAgent _synthesizer;
    private readonly WorkflowManifest _manifest;
    private readonly ILogger<RecursiveWorkflowRunner> _logger;

    public RecursiveWorkflowRunner(
        WorkflowRunCommandService runs,
        WorkflowNodeCommandService nodes,
        PlannerAgent planner,
        WorkerAgent worker,
        SynthesizerAgent synthesizer,
        WorkflowManifest manifest,
        ILogger<RecursiveWorkflowRunner> logger)
    {
        _runs = runs;
        _nodes = nodes;
        _planner = planner;
        _worker = worker;
        _synthesizer = synthesizer;
        _manifest = manifest;
        _logger = logger;
    }

    /// <summary>Ejecuta el workflow completo para un objetivo, dejando el run Completed en el event store.</summary>
    public async Task<WorkflowResult> RunAsync(string runId, string goal, CancellationToken ct)
    {
        var rootNodeId = $"n-{Guid.NewGuid():N}";

        _logger.LogInformation("▶ Iniciando workflow {RunId} — objetivo: «{Goal}»", runId, goal);

        // 1. El run arranca: se persiste WorkflowRunCreated.
        await GuardResult(_runs.Handle(new StartWorkflowRun(runId, goal, rootNodeId), ct));

        // 2. El nodo raíz se crea: se persiste WorkflowNodeCreated (depth 0).
        await GuardResult(_nodes.Handle(
            new CreateWorkflowNode(rootNodeId, runId, ParentNodeId: null, Depth: 0, Order: 0, goal), ct));

        try
        {
            var rootContext = new AgentContext
            {
                RunId = runId,
                NodeId = rootNodeId,
                Goal = goal,
                Depth = 0,
                MaxDepth = _manifest.MaxDepth
            };

            // 3. Ejecución recursiva del árbol.
            var root = await ExecuteNodeAsync(rootContext, ct);

            // 4. El run termina bien: se persiste WorkflowRunCompleted.
            await GuardResult(_runs.Handle(new CompleteWorkflowRun(runId, root.Answer), ct));

            _logger.LogInformation(
                "✔ Workflow {RunId} completado — {Nodes} nodos, respuesta final de {Chars} caracteres.",
                runId, CountNodes(root), root.Answer.Length);

            return new WorkflowResult(runId, root.Answer, root, rootContext.Steps);
        }
        catch (Exception ex)
        {
            // Si algo falla a mitad de camino, el run queda Failed en el event store
            // (auditoría: quedó el rastro de TODO lo que se hizo antes del error).
            _logger.LogError(ex, "✖ Workflow {RunId} falló — persistiendo WorkflowRunFailed.", runId);
            try
            {
                await _runs.Handle(new FailWorkflowRun(runId, ex.Message), ct);
            }
            catch (Exception failEx)
            {
                _logger.LogWarning(failEx, "No se pudo persistir el fallo del run {RunId}.", runId);
            }
            throw;
        }
    }

    // ── Recursión ────────────────────────────────────────────────────────────

    /// <summary>Ejecuta UN nodo: planifica, recorre hijos (si divide) y produce la respuesta.</summary>
    private async Task<NodeResult> ExecuteNodeAsync(AgentContext ctx, CancellationToken ct)
    {
        // ── Fase 1: PLANIFICAR ── ¿este objetivo se divide o se responde directo?
        var plan = await _planner.PlanAsync(ctx, ct);
        ctx.Steps.Add($"[{ctx.NodeId}] plan: {(plan.IsLeaf ? "hoja" : $"{plan.SubGoals.Count} sub-objetivos")} — {plan.Rationale}");

        if (!plan.IsLeaf && ctx.Depth < ctx.MaxDepth && plan.SubGoals.Count > 0)
        {
            // ── Fase 2a: DIVIDIR ── el nodo se convierte en padre de N hijos.
            var childIds = plan.SubGoals
                .Select((_, i) => $"n-{Guid.NewGuid():N}")
                .ToArray();

            // Invariante del aggregate: un nodo NO-hoja debe declarar hijos (guard en el dominio).
            await GuardResult(_nodes.Handle(
                new PlanWorkflowNode(ctx.NodeId, IsLeaf: false, childIds, plan.Rationale), ct));

            var children = new List<NodeResult>();
            var childAnswers = new Dictionary<string, string>();

            for (var i = 0; i < plan.SubGoals.Count; i++)
            {
                var childGoal = plan.SubGoals[i];
                var childId = childIds[i];

                // Cada hijo es SU PROPIO aggregate con SU stream.
                await GuardResult(_nodes.Handle(
                    new CreateWorkflowNode(childId, ctx.RunId, ctx.NodeId, ctx.Depth + 1, i, childGoal), ct));

                var childCtx = new AgentContext
                {
                    RunId = ctx.RunId,
                    NodeId = childId,
                    Goal = childGoal,
                    Depth = ctx.Depth + 1,
                    MaxDepth = ctx.MaxDepth
                };

                // ── RECURSIÓN: el hijo repite todo el proceso (planificar → dividir/responder).
                var childResult = await ExecuteNodeAsync(childCtx, ct);
                children.Add(childResult);
                childAnswers[childId] = childResult.Answer;
            }

            // ── Fase 3: SINTETIZAR ── el padre integra las respuestas de sus hijos.
            // Invariante CROSS-aggregate: acá TODOS los hijos ya están Completed
            // (la recursión terminó), así que el padre puede completarse.
            var synthCtx = ctx with { ChildAnswers = childAnswers };
            var synthesized = await _synthesizer.SynthesizeAsync(synthCtx, ct);
            ctx.Steps.Add($"[{ctx.NodeId}] síntesis de {childAnswers.Count} sub-respuestas");

            await GuardResult(_nodes.Handle(new CompleteWorkflowNode(ctx.NodeId, synthesized), ct));

            return new NodeResult(ctx.NodeId, ctx.Goal, ctx.Depth, IsLeaf: false, synthesized, children, plan.Rationale);
        }

        // ── Fase 2b: RESPONDER ── el nodo es una hoja: el Worker responde directo.
        await GuardResult(_nodes.Handle(
            new PlanWorkflowNode(ctx.NodeId, IsLeaf: true, [], plan.Rationale), ct));

        var answer = await _worker.AnswerAsync(ctx, ct);
        ctx.Steps.Add($"[{ctx.NodeId}] respuesta ({answer.Length} caracteres)");

        await GuardResult(_nodes.Handle(new CompleteWorkflowNode(ctx.NodeId, answer), ct));

        return new NodeResult(ctx.NodeId, ctx.Goal, ctx.Depth, IsLeaf: true, answer, [], plan.Rationale);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Un comando rechazado por el dominio (guard o stream inexistente) es un error del workflow.</summary>
    private static async Task GuardResult<TState>(Task<Eventuous.Result<TState>> handleTask)
        where TState : class, new()
    {
        var result = await handleTask;
        if (!result.Success)
            throw new InvalidOperationException(
                $"Comando rechazado por el dominio: {result.Exception?.Message ?? "(sin detalle)"}");
    }

    private static int CountNodes(NodeResult node) => 1 + node.Children.Sum(CountNodes);
}
