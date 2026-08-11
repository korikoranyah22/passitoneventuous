using CursoAgentes.Domain.Workflow;
using CursoAgentes.Tests.Testing;

namespace CursoAgentes.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Tests del aggregate WorkflowRun — estilo "State.When(event)" como en el repo
// real que inspira el curso: aplicamos eventos al state y verificamos el
// resultado. No hace falta Postgres: el State es puro.
// ─────────────────────────────────────────────────────────────────────────────

public class WorkflowRunStateTests
{
    private static readonly string Now = DateTime.UtcNow.ToString("O");

    private static WorkflowRunState Run() => new WorkflowRunState()
        .When(new WorkflowRunEvents.V1.WorkflowRunCreated("run-1", "¿objetivo?", "n-1", Now));

    [Fact]
    public void WhenCreated_StateIsRunning_WithGoalAndRoot()
    {
        var state = new WorkflowRunState().When(
            new WorkflowRunEvents.V1.WorkflowRunCreated("run-1", "¿objetivo?", "n-1", Now));

        Assert.Equal("run-1", state.RunId);
        Assert.Equal("¿objetivo?", state.Goal);
        Assert.Equal("n-1", state.RootNodeId);
        Assert.Equal(WorkflowRunStatus.Running, state.Status);
    }

    [Fact]
    public void WhenCompleted_StateIsCompleted_WithAnswer()
    {
        var state = Run()
            .When(new WorkflowRunEvents.V1.WorkflowRunCompleted("run-1", "respuesta final", Now));

        Assert.Equal(WorkflowRunStatus.Completed, state.Status);
        Assert.Equal("respuesta final", state.Answer);
    }

    [Fact]
    public void WhenFailed_StateIsFailed()
    {
        var state = Run()
            .When(new WorkflowRunEvents.V1.WorkflowRunFailed("run-1", "llm caído", Now));

        Assert.Equal(WorkflowRunStatus.Failed, state.Status);
    }
}

public class WorkflowRunCommandServiceTests
{
    private readonly InMemoryEventStore _store = new();
    private readonly WorkflowRunCommandService _commands;

    public WorkflowRunCommandServiceTests()
    {
        EventTypes.EnsureRegistered();
        _commands = new WorkflowRunCommandService(_store);
    }

    [Fact]
    public async Task StartThenComplete_IsHappyPath()
    {
        var started = await _commands.Handle(
            new StartWorkflowRun("run-1", "¿objetivo?", "n-1"), CancellationToken.None);
        Assert.True(started.Success, started.Exception?.Message);

        var completed = await _commands.Handle(
            new CompleteWorkflowRun("run-1", "respuesta"), CancellationToken.None);
        Assert.True(completed.Success, completed.Exception?.Message);
    }

    [Fact]
    public async Task Start_RequiresGoal()
    {
        var result = await _commands.Handle(
            new StartWorkflowRun("run-1", "  ", "n-1"), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("Goal required", result.Exception?.Message);
    }

    [Fact]
    public async Task Complete_UnknownRun_Fails()
    {
        var result = await _commands.Handle(
            new CompleteWorkflowRun("no-existe", "respuesta"), CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Fail_UnknownRun_Fails()
    {
        // El estado por defecto de un aggregate inexistente es None (no Running):
        // si el default fuera un estado válido, este comando "colaría" un append.
        var result = await _commands.Handle(
            new FailWorkflowRun("no-existe", "razón"), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("only Running", result.Exception?.Message);
    }

    [Fact]
    public async Task Complete_Twice_Fails()
    {
        await _commands.Handle(new StartWorkflowRun("run-1", "objetivo", "n-1"), CancellationToken.None);
        var first = await _commands.Handle(
            new CompleteWorkflowRun("run-1", "respuesta"), CancellationToken.None);
        Assert.True(first.Success);

        // Invariante del aggregate: un run Completed no se completa de nuevo.
        var second = await _commands.Handle(
            new CompleteWorkflowRun("run-1", "otra respuesta"), CancellationToken.None);
        Assert.False(second.Success);
        Assert.Contains("only Running", second.Exception?.Message);
    }

    [Fact]
    public async Task Fail_AfterCompleted_Fails()
    {
        await _commands.Handle(new StartWorkflowRun("run-1", "objetivo", "n-1"), CancellationToken.None);
        await _commands.Handle(new CompleteWorkflowRun("run-1", "respuesta"), CancellationToken.None);

        var fail = await _commands.Handle(
            new FailWorkflowRun("run-1", "tarde"), CancellationToken.None);
        Assert.False(fail.Success);
    }

    [Fact]
    public async Task Start_Twice_Fails_ByExpectedStateNew()
    {
        await _commands.Handle(new StartWorkflowRun("run-1", "objetivo", "n-1"), CancellationToken.None);
        var again = await _commands.Handle(
            new StartWorkflowRun("run-1", "otro", "n-2"), CancellationToken.None);
        Assert.False(again.Success);
    }
}
