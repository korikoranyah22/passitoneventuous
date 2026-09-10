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
            new WorkflowExecutionReader(store),
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

    [Fact]
    public async Task Resume_PlannedParent_CreatesMissingChildrenFromPersistedGoals()
    {
        var store = new InMemoryEventStore();
        var runs = new WorkflowRunCommandService(store);
        var nodes = new WorkflowNodeCommandService(store);
        await Success(runs.Handle(
            new StartWorkflowRun("run-missing-children", "objetivo raíz", "root"),
            CancellationToken.None));
        await Success(nodes.Handle(
            new CreateWorkflowNode("root", "run-missing-children", null, 0, 0, "objetivo raíz"),
            CancellationToken.None));
        await Success(nodes.Handle(
            new PlanWorkflowNode(
                "root",
                IsLeaf: false,
                ["child-a", "child-b"],
                "dividir",
                ["objetivo A", "objetivo B"]),
            CancellationToken.None));

        var llm = new CountingLlmGateway(new FakeLlmGateway(
        [
            """{"isLeaf": true, "subGoals": [], "rationale": "directo A"}""",
            "respuesta A",
            """{"isLeaf": true, "subGoals": [], "rationale": "directo B"}""",
            "respuesta B",
            "síntesis recuperada",
        ]));
        var result = await BuildRunner(store, llm).ResumeAsync(
            "run-missing-children",
            CancellationToken.None);

        Assert.Equal(5, llm.Calls);
        Assert.Equal(3, result.NodeCount);
        Assert.Equal(["objetivo A", "objetivo B"], result.Root.Children.Select(x => x.Goal));
        Assert.Equal("síntesis recuperada", result.FinalAnswer);
        Assert.Contains(result.Steps, step => step.Contains("recreado desde el plan persistido"));
        Assert.Equal(11, await CountEventsAsync(store, result));
    }

    [Fact]
    public async Task Resume_RunCreatedWithoutRoot_RecreatesRootFromRunEvent()
    {
        var store = new InMemoryEventStore();
        await Success(new WorkflowRunCommandService(store).Handle(
            new StartWorkflowRun("run-missing-root", "objetivo durable", "root-missing"),
            CancellationToken.None));
        var llm = new CountingLlmGateway(new FakeLlmGateway(
        [
            """{"isLeaf": true, "subGoals": [], "rationale": "directo"}""",
            "respuesta recuperada desde el run",
        ]));

        var result = await BuildRunner(store, llm).ResumeAsync(
            "run-missing-root",
            CancellationToken.None);

        Assert.Equal(2, llm.Calls);
        Assert.Equal("root-missing", result.Root.NodeId);
        Assert.Equal("objetivo durable", result.Root.Goal);
        Assert.Equal("respuesta recuperada desde el run", result.FinalAnswer);
        Assert.Contains(result.Steps, step => step.Contains("raíz recreada"));
        Assert.Equal(5, await CountEventsAsync(store, result));
    }

    [Fact]
    public async Task Resume_AfterCancellation_SkipsCompletedSubtree()
    {
        var store = new InMemoryEventStore();
        using var cts = new CancellationTokenSource();
        var interruptedLlm = new CancelOnCallLlmGateway(
            new FakeLlmGateway(
            [
                """{"isLeaf": false, "subGoals": ["Sub 1", "Sub 2"], "rationale": "dividir"}""",
                """{"isLeaf": true, "subGoals": [], "rationale": "directo"}""",
                "respuesta ya completada",
            ]),
            cts,
            cancelOnCall: 4);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BuildRunner(store, interruptedLlm).RunAsync(
                "run-interrupted",
                "objetivo",
                cts.Token));

        var interruptedRun = await LoadRunStateAsync(store, "run-interrupted");
        Assert.Equal(WorkflowRunStatus.Running, interruptedRun.Status);
        var (_, rootBeforeResume) = await LoadNodeStateAsync(
            store,
            $"workflow-node-{interruptedRun.RootNodeId}");
        Assert.Equal(WorkflowNodeStatus.Planned, rootBeforeResume.Status);
        var (_, firstChildBeforeResume) = await LoadNodeStateAsync(
            store,
            $"workflow-node-{rootBeforeResume.ChildrenIds[0]}");
        Assert.Equal(WorkflowNodeStatus.Completed, firstChildBeforeResume.Status);

        var recoveryLlm = new CountingLlmGateway(new FakeLlmGateway(
        [
            """{"isLeaf": true, "subGoals": [], "rationale": "directo"}""",
            "respuesta recuperada",
            "síntesis final después del resume",
        ]));
        var resumed = await BuildRunner(store, recoveryLlm).ResumeAsync(
            "run-interrupted",
            CancellationToken.None);

        Assert.Equal(3, recoveryLlm.Calls);
        Assert.Equal("respuesta ya completada", resumed.Root.Children[0].Answer);
        Assert.Equal("respuesta recuperada", resumed.Root.Children[1].Answer);
        Assert.Equal("síntesis final después del resume", resumed.FinalAnswer);
        Assert.Contains(resumed.Steps, step => step.Contains("resultado restaurado desde eventos"));
        Assert.Equal(11, await CountEventsAsync(store, resumed));
    }

    [Fact]
    public async Task Resume_CompletedRun_ReconstructsTreeWithoutCallingLlmOrAppendingEvents()
    {
        var store = new InMemoryEventStore();
        var original = await BuildRunner(store, new FakeLlmGateway(
        [
            """{"isLeaf": false, "subGoals": ["Sub 1"], "rationale": "dividir"}""",
            """{"isLeaf": true, "subGoals": [], "rationale": "directo"}""",
            "respuesta persistida",
            "síntesis persistida",
        ])).RunAsync("run-completed-resume", "objetivo", CancellationToken.None);
        var eventsBefore = await CountEventsAsync(store, original);

        var restored = await BuildRunner(store, new ThrowingLlmGateway()).ResumeAsync(
            "run-completed-resume",
            CancellationToken.None);

        Assert.Equal(original.FinalAnswer, restored.FinalAnswer);
        Assert.Equal(original.NodeCount, restored.NodeCount);
        Assert.Equal(eventsBefore, await CountEventsAsync(store, restored));
        Assert.Contains(restored.Steps, step => step.Contains("cero llamadas a agentes"));
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

    static async Task<TState> Success<TState>(Task<Result<TState>> pending)
        where TState : class, new()
    {
        var result = await pending;
        Assert.True(result.Success, result.Exception?.Message);
        Assert.True(result.TryGet(out var ok));
        return ok.State;
    }

    sealed class CountingLlmGateway(ILlmGateway inner) : ILlmGateway
    {
        public int Calls { get; private set; }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Calls++;
            return inner.CompleteAsync(request, ct);
        }
    }

    sealed class CancelOnCallLlmGateway(
        ILlmGateway inner,
        CancellationTokenSource cancellation,
        int cancelOnCall) : ILlmGateway
    {
        int _calls;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            _calls++;
            if (_calls == cancelOnCall)
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return inner.CompleteAsync(request, ct);
        }
    }

    /// <summary>Gateway que falla SIEMPRE (simula un provider caído o timeout).</summary>
    sealed class ThrowingLlmGateway : CursoAgentes.Engine.Llm.ILlmGateway
    {
        public Task<CursoAgentes.Engine.Llm.LlmResponse> CompleteAsync(
            CursoAgentes.Engine.Llm.LlmRequest request, CancellationToken ct)
            => throw new HttpRequestException("provider caído");
    }
}
