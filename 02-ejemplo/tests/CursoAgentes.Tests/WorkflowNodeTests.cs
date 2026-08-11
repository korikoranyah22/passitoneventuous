using CursoAgentes.Domain.Workflow;
using CursoAgentes.Tests.Testing;

namespace CursoAgentes.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Tests del aggregate WorkflowNode — el corazón del árbol recursivo.
// Cubrimos las transiciones de estado y TODOS los guards del dominio:
//   • hoja ≠ hijos (o es una cosa o la otra)
//   • cada nodo se planifica UNA vez
//   • solo un nodo Planned se completa
//   • un nodo Completed no falla
// ─────────────────────────────────────────────────────────────────────────────

public class WorkflowNodeStateTests
{
    private static readonly string Now = DateTime.UtcNow.ToString("O");

    private static WorkflowNodeState Node() => new WorkflowNodeState()
        .When(new WorkflowNodeEvents.V1.WorkflowNodeCreated(
            "n-1", "run-1", null, 0, 0, "objetivo raíz", Now));

    [Fact]
    public void WhenCreated_StateIsPending_WithDepthAndParent()
    {
        var state = new WorkflowNodeState().When(
            new WorkflowNodeEvents.V1.WorkflowNodeCreated(
                "n-2", "run-1", "n-1", 1, 0, "sub objetivo", Now));

        Assert.Equal(WorkflowNodeStatus.Pending, state.Status);
        Assert.Equal(1, state.Depth);
        Assert.Equal("n-1", state.ParentNodeId);
        Assert.Equal("sub objetivo", state.Goal);
    }

    [Fact]
    public void WhenPlannedAsLeaf_StateIsPlanned_Leaf()
    {
        var state = Node()
            .When(new WorkflowNodeEvents.V1.WorkflowNodePlanned("n-1", true, [], "directo", Now));

        Assert.Equal(WorkflowNodeStatus.Planned, state.Status);
        Assert.True(state.IsLeaf);
        Assert.Empty(state.ChildrenIds);
    }

    [Fact]
    public void WhenPlannedWithChildren_StateIsPlanned_WithChildIds()
    {
        var state = Node()
            .When(new WorkflowNodeEvents.V1.WorkflowNodePlanned(
                "n-1", false, ["n-2", "n-3"], "dividir", Now));

        Assert.Equal(WorkflowNodeStatus.Planned, state.Status);
        Assert.False(state.IsLeaf);
        Assert.Equal(["n-2", "n-3"], state.ChildrenIds);
    }

    [Fact]
    public void WhenCompleted_StateIsCompleted_WithAnswer()
    {
        var state = Node()
            .When(new WorkflowNodeEvents.V1.WorkflowNodePlanned("n-1", true, [], "directo", Now))
            .When(new WorkflowNodeEvents.V1.WorkflowNodeCompleted("n-1", "respuesta", Now));

        Assert.Equal(WorkflowNodeStatus.Completed, state.Status);
        Assert.Equal("respuesta", state.Answer);
    }
}

public class WorkflowNodeCommandServiceTests
{
    private readonly InMemoryEventStore _store = new();
    private readonly WorkflowNodeCommandService _commands;

    public WorkflowNodeCommandServiceTests()
    {
        EventTypes.EnsureRegistered();
        _commands = new WorkflowNodeCommandService(_store);
    }

    private Task<Eventuous.Result<WorkflowNodeState>> Create(string nodeId = "n-1", string? goal = null)
        => _commands.Handle(
            new CreateWorkflowNode(nodeId, "run-1", ParentNodeId: null, Depth: 0, Order: 0,
                goal ?? "objetivo"), CancellationToken.None);

    [Fact]
    public async Task Create_RequiresGoal()
    {
        var result = await Create(goal: "   ");
        Assert.False(result.Success);
        Assert.Contains("Goal required", result.Exception?.Message);
    }

    [Fact]
    public async Task Create_RejectsNegativeDepth()
    {
        var result = await _commands.Handle(
            new CreateWorkflowNode("n-1", "run-1", null, -1, 0, "objetivo"),
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("Depth cannot be negative", result.Exception?.Message);
    }

    [Fact]
    public async Task Plan_NonLeaf_WithoutChildren_Fails()
    {
        await Create();
        var result = await _commands.Handle(
            new PlanWorkflowNode("n-1", IsLeaf: false, [], "dividir"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("must declare at least one child", result.Exception?.Message);
    }

    [Fact]
    public async Task Plan_Leaf_WithChildren_Fails()
    {
        await Create();
        var result = await _commands.Handle(
            new PlanWorkflowNode("n-1", IsLeaf: true, ["n-2"], "¿hoja con hijos?"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("cannot declare children", result.Exception?.Message);
    }

    [Fact]
    public async Task Plan_Twice_Fails()
    {
        await Create();
        var first = await _commands.Handle(
            new PlanWorkflowNode("n-1", IsLeaf: true, [], "directo"), CancellationToken.None);
        Assert.True(first.Success);

        var second = await _commands.Handle(
            new PlanWorkflowNode("n-1", IsLeaf: true, [], "otra vez"), CancellationToken.None);
        Assert.False(second.Success);
        Assert.Contains("only Pending", second.Exception?.Message);
    }

    [Fact]
    public async Task Complete_BeforePlan_Fails()
    {
        await Create();
        var result = await _commands.Handle(
            new CompleteWorkflowNode("n-1", "respuesta"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("only Planned", result.Exception?.Message);
    }

    [Fact]
    public async Task Fail_UnknownNode_Fails()
    {
        // Mismo trap que en el run: el default del aggregate es None (inválido),
        // así que fallar un nodo inexistente es rechazado por el dominio.
        var result = await _commands.Handle(
            new FailWorkflowNode("no-existe", "razón"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("only Pending or Planned", result.Exception?.Message);
    }

    [Fact]
    public async Task Complete_AfterPlan_Succeeds()
    {
        await Create();
        await _commands.Handle(
            new PlanWorkflowNode("n-1", IsLeaf: true, [], "directo"), CancellationToken.None);
        var result = await _commands.Handle(
            new CompleteWorkflowNode("n-1", "respuesta"), CancellationToken.None);

        Assert.True(result.Success, result.Exception?.Message);
    }

    [Fact]
    public async Task Complete_Twice_Fails()
    {
        await Create();
        await _commands.Handle(
            new PlanWorkflowNode("n-1", IsLeaf: true, [], "directo"), CancellationToken.None);
        await _commands.Handle(
            new CompleteWorkflowNode("n-1", "respuesta"), CancellationToken.None);

        var second = await _commands.Handle(
            new CompleteWorkflowNode("n-1", "otra"), CancellationToken.None);
        Assert.False(second.Success);
    }

    [Fact]
    public async Task Fail_AfterComplete_Fails()
    {
        await Create();
        await _commands.Handle(
            new PlanWorkflowNode("n-1", IsLeaf: true, [], "directo"), CancellationToken.None);
        await _commands.Handle(
            new CompleteWorkflowNode("n-1", "respuesta"), CancellationToken.None);

        var result = await _commands.Handle(
            new FailWorkflowNode("n-1", "tarde"), CancellationToken.None);
        Assert.False(result.Success);
    }
}
