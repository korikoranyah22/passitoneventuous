using Eventuous;

namespace CursoAgentes.Domain.Workflow;

// ─────────────────────────────────────────────────────────────────────────────
// Aggregate WorkflowNode — UN nodo del árbol de ejecución recursiva.
// Stream: workflow-node-{id}.
//
// Cada nodo es un aggregate propio porque cada nodo tiene su propio ciclo de
// vida: se crea (Pending), se planifica (Planned: hoja o con hijos), y se
// completa (Completed) con una respuesta — o falla (Failed).
//
// Un nodo NO-hoja se completa cuando TODOS sus hijos están completos y su
// respuesta es una SÍNTESIS de las respuestas de los hijos. Esa invariante
// CROSS-aggregate no se valida acá (un aggregate no puede leer el stream de
// otro): la valida el orquestador (RecursiveWorkflowRunner, en Engine), que es
// la pieza que conoce el árbol completo. Esta separación es una lección
// central del curso: invariantes de UN aggregate → dominio; invariantes ENTRE
// aggregates → aplicación/orquestación.
//
// Los ChildrenIds se guardan como JSON serializable (string[]) en el evento;
// Eventuous los persiste como jsonb en Postgres.
// ─────────────────────────────────────────────────────────────────────────────

public static class WorkflowNodeEvents
{
    public static class V1
    {
        [EventType("V1.WorkflowNodeCreated")]
        public record WorkflowNodeCreated(
            string NodeId, string RunId, string? ParentNodeId, int Depth, int Order,
            string Goal, string CreatedAt);

        [EventType("V1.WorkflowNodePlanned")]
        public record WorkflowNodePlanned(
            string NodeId, bool IsLeaf, string[] ChildrenIds, string Rationale, string PlannedAt);

        [EventType("V1.WorkflowNodeCompleted")]
        public record WorkflowNodeCompleted(string NodeId, string Answer, string CompletedAt);

        [EventType("V1.WorkflowNodeFailed")]
        public record WorkflowNodeFailed(string NodeId, string Reason, string FailedAt);
    }
}

public record WorkflowNodeState : State<WorkflowNodeState>
{
    public string NodeId { get; init; } = "";
    public string RunId { get; init; } = "";
    public string? ParentNodeId { get; init; }
    public int Depth { get; init; }
    public int Order { get; init; }
    public string Goal { get; init; } = "";
    // Default = None (no Pending): un aggregate recién instanciado (sin eventos)
    // NO representa un nodo válido — los guards rechazan comandos sobre él.
    public WorkflowNodeStatus Status { get; init; } = WorkflowNodeStatus.None;
    public bool IsLeaf { get; init; }
    public string[] ChildrenIds { get; init; } = [];
    public string Rationale { get; init; } = "";
    public string? Answer { get; init; }
    public string CreatedAt { get; init; } = "";

    public WorkflowNodeState()
    {
        On<WorkflowNodeEvents.V1.WorkflowNodeCreated>((s, e) => s with
        {
            NodeId = e.NodeId,
            RunId = e.RunId,
            ParentNodeId = e.ParentNodeId,
            Depth = e.Depth,
            Order = e.Order,
            Goal = e.Goal,
            Status = WorkflowNodeStatus.Pending,
            CreatedAt = e.CreatedAt
        });

        On<WorkflowNodeEvents.V1.WorkflowNodePlanned>((s, e) => s with
        {
            Status = WorkflowNodeStatus.Planned,
            IsLeaf = e.IsLeaf,
            ChildrenIds = e.ChildrenIds,
            Rationale = e.Rationale
        });

        On<WorkflowNodeEvents.V1.WorkflowNodeCompleted>((s, e) => s with
        {
            Status = WorkflowNodeStatus.Completed,
            Answer = e.Answer
        });

        On<WorkflowNodeEvents.V1.WorkflowNodeFailed>((s, _) => s with { Status = WorkflowNodeStatus.Failed });
    }
}

public record CreateWorkflowNode(
    string NodeId, string RunId, string? ParentNodeId, int Depth, int Order, string Goal);
public record PlanWorkflowNode(string NodeId, bool IsLeaf, string[] ChildrenIds, string Rationale);
public record CompleteWorkflowNode(string NodeId, string Answer);
public record FailWorkflowNode(string NodeId, string Reason);

public sealed class WorkflowNodeCommandService : CommandService<WorkflowNodeState>
{
    // Handlers estáticos con yield — mismo patrón que WorkflowRunCommandService.
    public WorkflowNodeCommandService(IEventStore store) : base(store)
    {
        On<CreateWorkflowNode>().InState(ExpectedState.New)
            .GetStream(cmd => Stream(cmd.NodeId)).Act(Create);
        On<PlanWorkflowNode>().InState(ExpectedState.Existing)
            .GetStream(cmd => Stream(cmd.NodeId)).Act(Plan);
        On<CompleteWorkflowNode>().InState(ExpectedState.Existing)
            .GetStream(cmd => Stream(cmd.NodeId)).Act(Complete);
        On<FailWorkflowNode>().InState(ExpectedState.Existing)
            .GetStream(cmd => Stream(cmd.NodeId)).Act(Fail);
    }

    static IEnumerable<object> Create(CreateWorkflowNode cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.NodeId))
            throw new DomainException("CreateWorkflowNode: NodeId required.");
        if (string.IsNullOrWhiteSpace(cmd.RunId))
            throw new DomainException("CreateWorkflowNode: RunId required.");
        if (string.IsNullOrWhiteSpace(cmd.Goal))
            throw new DomainException("CreateWorkflowNode: Goal required.");
        if (cmd.Depth < 0)
            throw new DomainException("CreateWorkflowNode: Depth cannot be negative.");
        if (cmd.Order < 0)
            throw new DomainException("CreateWorkflowNode: Order cannot be negative.");
        yield return new WorkflowNodeEvents.V1.WorkflowNodeCreated(
            cmd.NodeId, cmd.RunId, cmd.ParentNodeId, cmd.Depth, cmd.Order, cmd.Goal, Now);
    }

    static IEnumerable<object> Plan(WorkflowNodeState state, object[] _, PlanWorkflowNode cmd)
    {
        // Solo un nodo Pending puede planificarse (cada nodo se planifica UNA vez).
        if (state.Status != WorkflowNodeStatus.Pending)
            throw new DomainException(
                $"PlanWorkflowNode: only Pending can be planned (was {state.Status}).");
        // Consistencia hoja/hijos: una hoja NO tiene hijos; un nodo con hijos NO es hoja.
        if (cmd.IsLeaf && cmd.ChildrenIds.Length > 0)
            throw new DomainException(
                "PlanWorkflowNode: a leaf node cannot declare children.");
        if (!cmd.IsLeaf && cmd.ChildrenIds.Length == 0)
            throw new DomainException(
                "PlanWorkflowNode: a non-leaf node must declare at least one child.");
        yield return new WorkflowNodeEvents.V1.WorkflowNodePlanned(
            cmd.NodeId, cmd.IsLeaf, cmd.ChildrenIds, cmd.Rationale, Now);
    }

    static IEnumerable<object> Complete(WorkflowNodeState state, object[] _, CompleteWorkflowNode cmd)
    {
        // Solo un nodo Planned puede completarse (nunca uno Pending o ya Completed).
        if (state.Status != WorkflowNodeStatus.Planned)
            throw new DomainException(
                $"CompleteWorkflowNode: only Planned can complete (was {state.Status}).");
        if (string.IsNullOrWhiteSpace(cmd.Answer))
            throw new DomainException("CompleteWorkflowNode: Answer required.");
        yield return new WorkflowNodeEvents.V1.WorkflowNodeCompleted(cmd.NodeId, cmd.Answer, Now);
    }

    static IEnumerable<object> Fail(WorkflowNodeState state, object[] _, FailWorkflowNode cmd)
    {
        // Solo un nodo Pending o Planned puede fallar (nunca uno Completed, Failed o inexistente).
        if (state.Status is not (WorkflowNodeStatus.Pending or WorkflowNodeStatus.Planned))
            throw new DomainException(
                $"FailWorkflowNode: only Pending or Planned can fail (was {state.Status}).");
        if (string.IsNullOrWhiteSpace(cmd.Reason))
            throw new DomainException("FailWorkflowNode: Reason required.");
        yield return new WorkflowNodeEvents.V1.WorkflowNodeFailed(cmd.NodeId, cmd.Reason, Now);
    }

    static StreamName Stream(string id) => new($"workflow-node-{id}");
    static string Now => DateTime.UtcNow.ToString("O");
}
