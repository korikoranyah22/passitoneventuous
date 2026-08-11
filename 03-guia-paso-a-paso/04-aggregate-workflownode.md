# Paso 4 · Aggregate `WorkflowNode`

## Objetivo

Modelar **un nodo del árbol recursivo**. Cada nodo tiene su propio ciclo de
vida y su propio stream (`workflow-node-{id}`). Este aggregate contiene los
guards más interesantes del curso.

## Concepto

```
None ──Create──► Pending ──Plan──► Planned ──Complete──► Completed
                                      │
                                      └────Fail────► Failed
```

- `CreateWorkflowNode` crea el nodo (Pending).
- `PlanWorkflowNode` decide si es **hoja** o tiene **hijos** (Planned).
- `CompleteWorkflowNode` le pone la respuesta (Completed).
- `FailWorkflowNode` lo marca fallido.

**Los guards interesantes:**

1. **Cada nodo se planifica UNA vez** (solo `Pending` → `Planned`).
2. **Hoja ≠ hijos**: o es hoja (sin hijos) o tiene hijos (no es hoja). Las dos
   cosas a la vez, o ninguna, no tienen sentido.
3. **Solo `Planned` se completa** (nunca un nodo recién creado ni uno ya
   completado).
4. **Solo `Pending` o `Planned` fallan** (un nodo completado no se descompleta).

## Código (archivo `src/CursoAgentes.Domain/Workflow/WorkflowNode.cs`)

### Eventos

```csharp
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
```

### El guard más didáctico: planificar

```csharp
static IEnumerable<object> Plan(WorkflowNodeState state, object[] _, PlanWorkflowNode cmd)
{
    // Solo un nodo Pending puede planificarse (cada nodo se planifica UNA vez).
    if (state.Status != WorkflowNodeStatus.Pending)
        throw new DomainException(
            $"PlanWorkflowNode: only Pending can be planned (was {state.Status}).");

    // Consistencia hoja/hijos: una hoja NO tiene hijos; un nodo con hijos NO es hoja.
    if (cmd.IsLeaf && cmd.ChildrenIds.Length > 0)
        throw new DomainException("PlanWorkflowNode: a leaf node cannot declare children.");
    if (!cmd.IsLeaf && cmd.ChildrenIds.Length == 0)
        throw new DomainException("PlanWorkflowNode: a non-leaf node must declare at least one child.");

    yield return new WorkflowNodeEvents.V1.WorkflowNodePlanned(
        cmd.NodeId, cmd.IsLeaf, cmd.ChildrenIds, cmd.Rationale, Now);
}
```

> Los `ChildrenIds` se guardan en el evento como `string[]`. Eventuous los
> serializa a `jsonb` en Postgres. Cuando el run se reanuda, el nodo sabe qué
> hijos creó — aunque el proceso haya muerto antes.

## La invariante que NO está acá (y por qué)

Un padre solo se completa cuando **todos sus hijos están completos**. Eso es
una invariante **entre aggregates** (el nodo padre no puede leer el stream de
sus hijos), así que NO va en este aggregate: la aplica el orquestador (paso 7).
Esta separación — invariantes de un aggregate → dominio; invariantes entre
aggregates → orquestación — es una de las lecciones centrales del curso.

## Probalo

```bash
dotnet test --filter "FullyQualifiedName~WorkflowNodeTests"
```

Cubre: transiciones de estado y los 4 guards (planificar hoja con hijos,
no-hoja sin hijos, planificar dos veces, completar sin planificar, completar
dos veces, fallar un nodo inexistente, fallar tras completar…).

---

**Siguiente**: [Paso 5 · Persistencia Eventuous + Postgres](05-persistencia-eventuous-postgres.md)
