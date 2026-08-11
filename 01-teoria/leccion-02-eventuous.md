# Lección 2 · Eventuous: aggregates, comandos y guards

> **Eventuous** es un framework de event sourcing para .NET que se queda con lo
> esencial: definís **eventos**, un **estado** que los aplica, y un **command
> service** que valida comandos y produce eventos. El resto (persistencia,
> serialización, suscripciones) lo resuelve el framework.

## 2.1 Las cuatro piezas de un aggregate

En el ejemplo hay dos aggregates: `WorkflowRun` (el ciclo de vida del run) y
`WorkflowNode` (un nodo del árbol). Cada uno tiene las mismas cuatro piezas:

### 1. Eventos — `WorkflowRunEvents`

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

- `[EventType("V1.Name")]` es el **nombre estable** del evento. Se guarda en
  Postgres y sirve para deserializar. Si renombrás la clase C# pero no el
  nombre, los eventos viejos siguen leyéndose.
- Los eventos son **records inmutables**: datos, nada de lógica.

### 2. Estado — `WorkflowRunState`

```csharp
public record WorkflowRunState : State<WorkflowRunState>
{
    public string RunId { get; init; } = "";
    public WorkflowRunStatus Status { get; init; } = WorkflowRunStatus.None;
    // ...

    public WorkflowRunState()
    {
        On<WorkflowRunEvents.V1.WorkflowRunCreated>((s, e) => s with
        {
            RunId = e.RunId, Goal = e.Goal, RootNodeId = e.RootNodeId,
            Status = WorkflowRunStatus.Running, CreatedAt = e.CreatedAt
        });
        On<WorkflowRunEvents.V1.WorkflowRunCompleted>((s, e) => s with { Status = ..., Answer = e.Answer });
        On<WorkflowRunEvents.V1.WorkflowRunFailed>((s, _) => s with { Status = ... });
    }
}
```

- El estado es un **fold puro**: para cada tipo de evento, una transformación
  `(estado, evento) → estado nuevo`.
- **Ojo al default**: `Status = WorkflowRunStatus.None`. Este es un detalle
  fundamental — lo explicamos en 2.3.

### 3. Comandos

```csharp
public record StartWorkflowRun(string RunId, string Goal, string RootNodeId);
public record CompleteWorkflowRun(string RunId, string Answer);
public record FailWorkflowRun(string RunId, string Reason);
```

Un comando es una **intención**: "quiero que esto pase". El aggregate decide si
puede pasar (guards) y, si puede, produce el evento correspondiente.

### 4. Command service — `WorkflowRunCommandService`

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
            throw new DomainException($"CompleteWorkflowRun: only Running can complete (was {state.Status}).");
        if (string.IsNullOrWhiteSpace(cmd.Answer))
            throw new DomainException("CompleteWorkflowRun: Answer required.");
        yield return new WorkflowRunEvents.V1.WorkflowRunCompleted(cmd.RunId, cmd.Answer, Now);
    }
    // ...
}
```

- **`InState(ExpectedState.New)`**: el stream no debe existir (crear un run).
- **`InState(ExpectedState.Existing)`**: el stream debe existir (completar,
  fallar un run ya creado).
- **`GetStream(cmd => …)`**: qué stream usa este comando.
- **`Act(handler)`**: la validación + producción de eventos. Los handlers
  estáticos con `yield` son el patrón recomendado (los analizadores de
  Eventuous te guían: EVTC001).
- **`throw new DomainException(...)`**: el guard rechazó el comando. El command
  service lo captura y devuelve un `Result` con `Success = false` — no una
  excepción escapando.

## 2.2 El flujo completo de un comando

```
Handle(CompleteWorkflowRun("run-1", "respuesta"))
   │
   ├─ 1. Carga el aggregate: lee el stream workflow-run-1 y aplica los eventos
   │      (si no existe y el estado esperado es Existing, carga un estado vacío)
   │
   ├─ 2. Ejecuta el handler con el estado reconstruido
   │      → guard: ¿Status == Running? NO → DomainException → Result{Success: false}
   │      → guard: OK → yield WorkflowRunCompleted
   │
   └─ 3. Append del evento al stream con control de versión esperada
          (optimistic concurrency: si otro proceso escribió mientras tanto, falla)
```

## 2.3 La lección del estado por defecto (¡importante!)

> **El estado por defecto de un aggregate debe ser un estado INVALIDO.**

En Eventuous 0.16.x, un comando con `InState(ExpectedState.Existing)` sobre un
stream que **no existe** NO falla antes del handler: ejecuta el handler con el
estado por defecto (recién instanciado, sin eventos) y luego intenta el append.
La única protección es tu guard.

Si el default de `Status` fuera `Running` (un estado *válido*), esto pasaría:

```
Handle(CompleteWorkflowRun("run-que-no-existe", "respuesta"))
  → carga estado vacío (Status = Running)   ← ¡default válido!
  → guard "solo Running" → PASA
  → se appendea WorkflowRunCompleted a un run que nunca fue creado. 🤦
```

Con `None` como default, el guard lo rechaza:

```
  → carga estado vacío (Status = None)      ← default inválido
  → guard "solo Running" → DomainException → Result{Success: false} ✅
```

El ejemplo tiene tests que documentan exactamente este trap:
`Complete_UnknownRun_Fails` y `Fail_UnknownRun_Fails` en
`02-ejemplo/tests/CursoAgentes.Tests/WorkflowRunTests.cs`.

## 2.4 ¿Qué NO va en el aggregate?

**Nada de efectos colaterales.** Ni llamadas HTTP al LLM, ni escritura a disco,
ni I/O. El aggregate es una máquina de estados pura: recibe intención, valida,
emite eventos. Las llamadas al LLM viven en el motor (lección 4/6), los
side-effects en la capa de aplicación. Esta separación es la que hace que los
aggregates sean **testeables sin infraestructura** (los tests aplican eventos a
un `State` en memoria, sin Postgres).

---

## 📖 En el ejemplo

- Aggregate `WorkflowRun`: `02-ejemplo/src/CursoAgentes.Domain/Workflow/WorkflowRun.cs`
- Aggregate `WorkflowNode` (el corazón del árbol): `02-ejemplo/src/CursoAgentes.Domain/Workflow/WorkflowNode.cs`
- Enums con `None`: `02-ejemplo/src/CursoAgentes.Domain/Workflow/WorkflowStatus.cs`
- Tests de estado (puros, sin infra): `02-ejemplo/tests/CursoAgentes.Tests/WorkflowRunTests.cs`
- Tests de guards (con event store en memoria): el mismo archivo, clase `WorkflowRunCommandServiceTests`
