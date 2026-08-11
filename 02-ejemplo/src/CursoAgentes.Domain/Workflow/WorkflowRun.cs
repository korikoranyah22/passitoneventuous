using Eventuous;

namespace CursoAgentes.Domain.Workflow;

// ─────────────────────────────────────────────────────────────────────────────
// Aggregate WorkflowRun — una ejecución COMPLETA del workflow de nodos recursivos.
// Stream: workflow-run-{id}.
//
// Este aggregate es chico a propósito: solo registra el ciclo de vida del run
// (creado → completado/fallado) y valida las transiciones. Todo el "trabajo"
// (llamadas al LLM, recursión, síntesis) vive FUERA del aggregate, en el motor
// (CursoAgentes.Engine). El aggregate es el contrato: nadie puede marcar un run
// como completado si no existe, ni completarlo dos veces.
//
// Lección del curso: los aggregates son FUENTE DE VERDAD y VALIDADORES DE
// INVARIANTES, no lugar para efectos colaterales (llamadas HTTP, I/O, LLM).
// ─────────────────────────────────────────────────────────────────────────────

public static class WorkflowRunEvents
{
    public static class V1
    {
        [EventType("V1.WorkflowRunCreated")]
        public record WorkflowRunCreated(
            string RunId, string Goal, string RootNodeId, string CreatedAt);

        [EventType("V1.WorkflowRunCompleted")]
        public record WorkflowRunCompleted(string RunId, string Answer, string CompletedAt);

        [EventType("V1.WorkflowRunFailed")]
        public record WorkflowRunFailed(string RunId, string Reason, string FailedAt);
    }
}

public record WorkflowRunState : State<WorkflowRunState>
{
    public string RunId { get; init; } = "";
    public string Goal { get; init; } = "";
    public string RootNodeId { get; init; } = "";
    // Default = None (no Running): un aggregate recién instanciado (sin eventos)
    // NO representa un run válido — los guards rechazan comandos sobre él.
    public WorkflowRunStatus Status { get; init; } = WorkflowRunStatus.None;
    public string? Answer { get; init; }
    public string CreatedAt { get; init; } = "";

    public WorkflowRunState()
    {
        On<WorkflowRunEvents.V1.WorkflowRunCreated>((s, e) => s with
        {
            RunId = e.RunId,
            Goal = e.Goal,
            RootNodeId = e.RootNodeId,
            Status = WorkflowRunStatus.Running,
            CreatedAt = e.CreatedAt
        });

        On<WorkflowRunEvents.V1.WorkflowRunCompleted>((s, e) => s with
        {
            Status = WorkflowRunStatus.Completed,
            Answer = e.Answer
        });

        On<WorkflowRunEvents.V1.WorkflowRunFailed>((s, _) => s with { Status = WorkflowRunStatus.Failed });
    }
}

public record StartWorkflowRun(string RunId, string Goal, string RootNodeId);
public record CompleteWorkflowRun(string RunId, string Answer);
public record FailWorkflowRun(string RunId, string Reason);

public sealed class WorkflowRunCommandService : CommandService<WorkflowRunState>
{
    // Handlers estáticos con yield (patrón EVTC001 de Eventuous): las guardas con
    // throw/yield break van dentro del IEnumerable<object>, no como lambdas inline.
    public WorkflowRunCommandService(IEventStore store) : base(store)
    {
        On<StartWorkflowRun>().InState(ExpectedState.New)
            .GetStream(cmd => Stream(cmd.RunId)).Act(Start);
        On<CompleteWorkflowRun>().InState(ExpectedState.Existing)
            .GetStream(cmd => Stream(cmd.RunId)).Act(Complete);
        On<FailWorkflowRun>().InState(ExpectedState.Existing)
            .GetStream(cmd => Stream(cmd.RunId)).Act(Fail);
    }

    static IEnumerable<object> Start(StartWorkflowRun cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.RunId))
            throw new DomainException("StartWorkflowRun: RunId required.");
        if (string.IsNullOrWhiteSpace(cmd.Goal))
            throw new DomainException("StartWorkflowRun: Goal required.");
        if (string.IsNullOrWhiteSpace(cmd.RootNodeId))
            throw new DomainException("StartWorkflowRun: RootNodeId required.");
        yield return new WorkflowRunEvents.V1.WorkflowRunCreated(
            cmd.RunId, cmd.Goal, cmd.RootNodeId, Now);
    }

    static IEnumerable<object> Complete(WorkflowRunState state, object[] _, CompleteWorkflowRun cmd)
    {
        // Invariante del aggregate: un run solo se completa si está Running.
        // (Si ya está Completed, alguien está duplicando comandos → rechazar.)
        if (state.Status != WorkflowRunStatus.Running)
            throw new DomainException(
                $"CompleteWorkflowRun: only Running can complete (was {state.Status}).");
        if (string.IsNullOrWhiteSpace(cmd.Answer))
            throw new DomainException("CompleteWorkflowRun: Answer required.");
        yield return new WorkflowRunEvents.V1.WorkflowRunCompleted(cmd.RunId, cmd.Answer, Now);
    }

    static IEnumerable<object> Fail(WorkflowRunState state, object[] _, FailWorkflowRun cmd)
    {
        // Solo un run Running puede fallar (nunca un Completed, un Failed, ni uno que no existe).
        if (state.Status != WorkflowRunStatus.Running)
            throw new DomainException(
                $"FailWorkflowRun: only Running can fail (was {state.Status}).");
        if (string.IsNullOrWhiteSpace(cmd.Reason))
            throw new DomainException("FailWorkflowRun: Reason required.");
        yield return new WorkflowRunEvents.V1.WorkflowRunFailed(cmd.RunId, cmd.Reason, Now);
    }

    static StreamName Stream(string id) => new($"workflow-run-{id}");
    static string Now => DateTime.UtcNow.ToString("O");
}
