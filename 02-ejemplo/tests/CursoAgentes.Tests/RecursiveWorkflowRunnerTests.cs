using CursoAgentes.Domain.Workflow;
using CursoAgentes.Engine.Llm;
using CursoAgentes.Engine.Workflow;
using CursoAgentes.Tests.Testing;
using Eventuous;
using Microsoft.Extensions.Logging.Abstractions;

namespace CursoAgentes.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Tests del MOTOR recursivo. La joya del curso: el workflow completo corre
// contra el event store en memoria y con el LLM falso, y verificamos:
//   • el árbol se construye (hojas + síntesis en el nivel correcto)
//   • CADA nodo queda persistido como aggregate (Pending → Planned → Completed)
//   • el run termina Completed en el event store
//   • la recursión se corta en MaxDepth (terminación garantizada)
// ─────────────────────────────────────────────────────────────────────────────

public class RecursiveWorkflowRunnerTests
{
    private static readonly string Now = DateTime.UtcNow.ToString("O");

    static RecursiveWorkflowRunnerTests()
        => EventTypes.EnsureRegistered();

    private static RecursiveWorkflowRunner BuildRunner(
        InMemoryEventStore store,
        ILlmGateway llm,
        int maxDepth = 3)
    {
        var manifest = new WorkflowManifest { MaxDepth = maxDepth, MaxChildrenPerNode = 3 };

        return new RecursiveWorkflowRunner(
            new WorkflowRunCommandService(store),
            new WorkflowNodeCommandService(store),
            new PlannerAgent(llm, manifest, NullLogger<PlannerAgent>.Instance),
            new WorkerAgent(llm, manifest, NullLogger<WorkerAgent>.Instance),
            new SynthesizerAgent(llm, manifest, NullLogger<SynthesizerAgent>.Instance),
            manifest,
            NullLogger<RecursiveWorkflowRunner>.Instance);
    }

    [Fact]
    public async Task FullRun_WithScriptedLlm_BuildsTree_AndPersistsEverything()
    {
        var store = new InMemoryEventStore();
        // El orden de llamadas es DEPTH-FIRST: cada hijo se ejecuta completo
        // (planner → worker) antes de pasar al siguiente hermano.
        var script = new[]
        {
            // 1. planner(root) → divide en 2
            """{"isLeaf": false, "subGoals": ["Sub 1", "Sub 2"], "rationale": "dividir"}""",
            // 2. planner(child1) → hoja
            """{"isLeaf": true, "subGoals": [], "rationale": "directo"}""",
            // 3. worker(child1) → responde directo
            "respuesta del sub-objetivo 1",
            // 4. planner(child2) → hoja
            """{"isLeaf": true, "subGoals": [], "rationale": "directo"}""",
            // 5. worker(child2) → responde directo
            "respuesta del sub-objetivo 2",
            // 6. synthesizer(root) → integra las respuestas de los hijos
            "síntesis integrada de ambos sub-objetivos"
        };
        var llm = new FakeLlmGateway(script);
        var runner = BuildRunner(store, llm);

        var result = await runner.RunAsync("run-test", "¿por qué event sourcing?", CancellationToken.None);

        // Árbol en memoria
        Assert.Equal(3, result.NodeCount);
        Assert.False(result.Root.IsLeaf);
        Assert.Equal(2, result.Root.Children.Count);
        Assert.All(result.Root.Children, c => Assert.True(c.IsLeaf));
        Assert.Equal("síntesis integrada de ambos sub-objetivos", result.Root.Answer);
        Assert.Equal("respuesta del sub-objetivo 1", result.Root.Children[0].Answer);

        // El run quedó Completed en el event store
        var runState = await LoadRunStateAsync(store, "run-test");
        Assert.Equal(WorkflowRunStatus.Completed, runState.Status);
        Assert.Equal("síntesis integrada de ambos sub-objetivos", runState.Answer);

        // Cada nodo quedó persistido con su ciclo completo
        foreach (var stream in NodeStreams(result))
        {
            var (events, state) = await LoadNodeStateAsync(store, stream);
            Assert.Equal(3, events.Length); // Created + Planned + Completed
            Assert.Equal(WorkflowNodeStatus.Completed, state.Status);
        }

        // Total de eventos: run(2) + 3 nodos × 3 = 11
        Assert.Equal(11, await CountEventsAsync(store, result));
    }

    [Fact]
    public async Task Run_WithPlannerAlwaysDecomposing_StopsAtMaxDepth()
    {
        var store = new InMemoryEventStore();
        // Modo "smart": el planner SIEMPRE divide (devolvería subGoals hasta el infinito).
        // MaxDepth = 2 debe cortar la recursión: raíz(d0) → 2 hijos(d1) → 4 hojas(d2).
        var llm = new FakeLlmGateway(script: null);
        var runner = BuildRunner(store, llm, maxDepth: 2);

        var result = await runner.RunAsync("run-cap", "objetivo infinito", CancellationToken.None);

        // El árbol quedó acotado: 1 + 2 + 4 = 7 nodos.
        Assert.Equal(7, result.NodeCount);

        // Todas las hojas están a profundidad == MaxDepth.
        var leaves = CollectLeaves(result.Root);
        Assert.Equal(4, leaves.Count);
        Assert.All(leaves, l => Assert.Equal(2, l.Depth));

        // Y el run terminó bien.
        var runState = await LoadRunStateAsync(store, "run-cap");
        Assert.Equal(WorkflowRunStatus.Completed, runState.Status);
    }

    [Fact]
    public async Task Run_WhenLlmThrows_RunEndsFailed_InEventStore()
    {
        var store = new InMemoryEventStore();
        // Un gateway que revienta en la primera llamada: simula un provider caído.
        var llm = new ThrowingLlmGateway();
        var runner = BuildRunner(store, llm);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => runner.RunAsync("run-fail", "objetivo", CancellationToken.None));

        var runState = await LoadRunStateAsync(store, "run-fail");
        Assert.Equal(WorkflowRunStatus.Failed, runState.Status);
    }

    // ── Helpers de lectura del event store ───────────────────────────────────

    // El analyzer EVTC001 se queja de When(object?) — acá es intencional:
    // reproducimos eventos ya persistidos para verificar el estado reconstruido.
#pragma warning disable EVTC001
    static async Task<WorkflowRunState> LoadRunStateAsync(InMemoryEventStore store, string runId)
    {
        var events = await store.ReadEvents(new StreamName($"workflow-run-{runId}"),
            StreamReadPosition.Start, int.MaxValue, fromEnd: false, CancellationToken.None);
        Assert.NotEmpty(events);
        var state = new WorkflowRunState();
        foreach (var e in events) state = state.When(e.Payload!);
        return state;
    }

    static async Task<(object[] Events, WorkflowNodeState State)> LoadNodeStateAsync(
        InMemoryEventStore store, string stream)
    {
        var events = await store.ReadEvents(new StreamName(stream),
            StreamReadPosition.Start, int.MaxValue, fromEnd: false, CancellationToken.None);
        var state = new WorkflowNodeState();
        foreach (var e in events) state = state.When(e.Payload!);
        return (events.Select(e => e.Payload!).ToArray(), state);
    }
#pragma warning restore EVTC001

    static async Task<int> CountEventsAsync(InMemoryEventStore store, WorkflowResult result)
    {
        var total = 0;
        foreach (var stream in NodeStreams(result))
        {
            var events = await store.ReadEvents(new StreamName(stream),
                StreamReadPosition.Start, int.MaxValue, fromEnd: false, CancellationToken.None);
            total += events.Length;
        }
        total += (await store.ReadEvents(new StreamName($"workflow-run-{result.RunId}"),
            StreamReadPosition.Start, int.MaxValue, fromEnd: false, CancellationToken.None)).Length;
        return total;
    }

    static IEnumerable<string> NodeStreams(WorkflowResult result)
        => NodeStreams(result.Root);

    static IEnumerable<string> NodeStreams(NodeResult node)
    {
        yield return $"workflow-node-{node.NodeId}";
        foreach (var child in node.Children)
            foreach (var stream in NodeStreams(child))
                yield return stream;
    }

    static List<NodeResult> CollectLeaves(NodeResult node)
    {
        var leaves = new List<NodeResult>();
        if (node.IsLeaf) leaves.Add(node);
        foreach (var child in node.Children) leaves.AddRange(CollectLeaves(child));
        return leaves;
    }

    /// <summary>Gateway que falla SIEMPRE (simula un provider caído o timeout).</summary>
    sealed class ThrowingLlmGateway : CursoAgentes.Engine.Llm.ILlmGateway
    {
        public Task<CursoAgentes.Engine.Llm.LlmResponse> CompleteAsync(
            CursoAgentes.Engine.Llm.LlmRequest request, CancellationToken ct)
            => throw new HttpRequestException("provider caído");
    }
}
