# Paso 3 · Aggregate `WorkflowRun`

## Objetivo

Modelar el **ciclo de vida de una ejecución del workflow**: se crea con un
objetivo, y termina `Completed` (con respuesta final) o `Failed` (con motivo).
Es el aggregate más chico del ejemplo — y el que introduce las cuatro piezas de
Eventuous que después repetís en todos lados.

## Concepto

Un run es una **máquina de estados**:

```
None ──Start──► Running ──Complete──► Completed
                  │
                  └────Fail────► Failed
```

- `None` es el estado por defecto del aggregate **recién instanciado** (no
  existe). Ningún guard acepta `None`: así un comando dirigido a un run
  inexistente es rechazado (lección 2.3 de la teoría).
- Las transiciones inválidas (completar dos veces, fallar un run completado)
  son rechazadas por **guards** del dominio.

## Código (archivo `src/CursoAgentes.Domain/Workflow/WorkflowRun.cs`)

### 1. Eventos

```csharp
public static class WorkflowRunEvents
{
    public static class V1
    {
        [EventType("V1.WorkflowRunCreated")]
        public record WorkflowRunCreated(string RunId, string Goal, string RootNodeId, string CreatedAt);

        [EventType("V1.WorkflowRunCompleted")]
        public record WorkflowRunCompleted(string RunId, string Answer, string CompletedAt);

        [EventType("V1.WorkflowRunFailed")]
        public record WorkflowRunFailed(string RunId, string Reason, string FailedAt);
    }
}
```

### 2. Estado

```csharp
public record WorkflowRunState : State<WorkflowRunState>
{
    public string RunId { get; init; } = "";
    public string Goal { get; init; } = "";
    public string RootNodeId { get; init; } = "";
    public WorkflowRunStatus Status { get; init; } = WorkflowRunStatus.None;  // ← ¡default inválido!
    public string? Answer { get; init; }
    public string CreatedAt { get; init; } = "";

    public WorkflowRunState()
    {
        On<WorkflowRunEvents.V1.WorkflowRunCreated>((s, e) => s with
        {
            RunId = e.RunId, Goal = e.Goal, RootNodeId = e.RootNodeId,
            Status = WorkflowRunStatus.Running, CreatedAt = e.CreatedAt
        });
        On<WorkflowRunEvents.V1.WorkflowRunCompleted>((s, e) => s with
        {
            Status = WorkflowRunStatus.Completed, Answer = e.Answer
        });
        On<WorkflowRunEvents.V1.WorkflowRunFailed>((s, _) => s with { Status = WorkflowRunStatus.Failed });
    }
}
```

### 3. Comandos

```csharp
public record StartWorkflowRun(string RunId, string Goal, string RootNodeId);
public record CompleteWorkflowRun(string RunId, string Answer);
public record FailWorkflowRun(string RunId, string Reason);
```

### 4. Command service con guards

```csharp
public sealed class WorkflowRunCommandService : CommandService<WorkflowRunState>
{
    public WorkflowRunCommandService(IEventStore store) : base(store)
    {
        On<StartWorkflowRun>().InState(ExpectedState.New)
            .GetStream(cmd => Stream(cmd.RunId)).Act(Start);
        On<CompleteWorkflowRun>().InState(ExpectedState.Existing)
            .GetStream(cmd => Stream(cmd.RunId)).Act(Complete);
        On<FailWorkflowRun>().InState(ExpectedState.Existing)
            .GetStream(cmd => Stream(cmd.RunId)).Act(Fail);
    }

    static IEnumerable<object> Complete(WorkflowRunState state, object[] _, CompleteWorkflowRun cmd)
    {
        if (state.Status != WorkflowRunStatus.Running)
            throw new DomainException(
                $"CompleteWorkflowRun: only Running can complete (was {state.Status}).");
        if (string.IsNullOrWhiteSpace(cmd.Answer))
            throw new DomainException("CompleteWorkflowRun: Answer required.");
        yield return new WorkflowRunEvents.V1.WorkflowRunCompleted(cmd.RunId, cmd.Answer, Now);
    }
    // Start y Fail: mirá el archivo completo.

    static StreamName Stream(string id) => new($"workflow-run-{id}");
    static string Now => DateTime.UtcNow.ToString("O");
}
```

### El enum de estado (archivo `WorkflowStatus.cs`)

```csharp
public enum WorkflowRunStatus
{
    None = 0,      // ← default: el aggregate NO existe todavía
    Running,
    Completed,
    Failed
}
```

## Probalo

```bash
dotnet test --filter "FullyQualifiedName~WorkflowRunTests"
```

Cubre: transiciones de estado (puras, con `.When(evento)`) y guards del command
service (completar run inexistente, completar dos veces, fallar tras completar,
etc.). Estos tests **no tocan Postgres** — el `InMemoryEventStore` de
`tests/CursoAgentes.Tests/Testing/` implementa `IEventStore` a mano.

---

**Siguiente**: [Paso 4 · Aggregate WorkflowNode](04-aggregate-workflownode.md)
