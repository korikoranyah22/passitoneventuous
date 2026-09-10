# Paso 10 · Tests

## Objetivo

Entender la estrategia de tests del ejemplo: **72 tests que corren SIN Postgres
y SIN LLM real**. Son 69 tests autocontenidos del núcleo y 3 que integran los
ejemplos de MiyuAgents con Eventuous. Eso es posible gracias a dos artefactos de
enseñanza que ya conocés de pasos anteriores.

## Los dos artefactos clave

### 1. `InMemoryEventStore` — un event store a mano

`tests/CursoAgentes.Tests/Testing/InMemoryEventStore.cs` implementa
`IEventStore` (~130 líneas). Es un **artefacto pedagógico**: implementar el
contrato a mano te muestra qué tiene que cumplir un event store de verdad
(append con versión esperada, lectura en orden, existencia de streams).

```csharp
// Optimistic concurrency en 10 líneas:
var currentVersion = existing is null ? -1 : existing[^1].Revision;
if (expectedValue != -2 && expectedValue != currentVersion)
    throw new AppendToStreamException(…);
```

> El `PostgresStore` real (Eventuous.Postgresql) hace exactamente lo mismo,
> pero con tablas, transacciones y el chequeo de versión resuelto por SQL.

### 2. `FakeLlmGateway` — el LLM determinista

Ya lo viste en el paso 6. Para los tests se usa en modo **scripted**: una cola
de respuestas que controla exactamente qué devuelve cada llamada.

## Los 72 tests, agrupados

| Clase | Qué verifica | Cantidad |
|---|---|---|
| `WorkflowRunStateTests` | Transiciones puras de estado (`.When(evento)`), incluida la solicitud durable de ejecución | 4 |
| `WorkflowRunCommandServiceTests` | Guards del run: completar inexistente, solicitud idempotente/conflictiva, fallar tras completar, etc. | 10 |
| `WorkflowNodeStateTests` | Transiciones puras del nodo | 4 |
| `WorkflowNodeCommandServiceTests` | Guards del nodo: hoja≠hijos, objetivos persistidos, planificar dos veces, completar sin planificar… | 11 |
| `RecursiveWorkflowRunnerTests` | El motor: árbol, persistencia, terminación, run fallido y reanudación selectiva | 7 |
| `OpenAiCompatibleGatewayTests` | El adaptador real con HTTP stub | 2 |
| `IncidentInvestigationStateTests` | Replay puro y política determinista | 2 |
| `IncidentInvestigationCommandServiceTests` | Orden, contratos, recuperación, retry, parking, recuperación manual y reentrega idempotente | 15 |
| `HttpIncidentActionPortTests` | Contrato HTTP idempotente y clasificación de respuestas | 2 |
| `MiyuEventuousBridgeTests` | Equivalencia pipeline/nodos, lifecycle y autoridad única del efecto | 3 |
| `WorkflowApiHostTests` | Cola/worker/árbol, aceptación, recovery y lease multi-instancia | 12 |

Los 3 tests de `MiyuEventuousBridgeTests` se detectan automáticamente al
compilar. El checkout actual incluye `incident-response`, `routing-workflow` y
`fixed-node-workflow`, por lo que corren los **72**. La detección condicional se
conserva para que una copia aislada del curso todavía pueda ejecutar sus **69
tests autocontenidos** sin el repositorio externo.

### Los tests del motor (los más valiosos)

```csharp
[Fact]
public async Task FullRun_WithScriptedLlm_BuildsTree_AndPersistsEverything()
{
    var script = new[]
    {
        """{"isLeaf": false, "subGoals": ["Sub 1", "Sub 2"], "rationale": "dividir"}""", // planner(root)
        """{"isLeaf": true,  "subGoals": [], "rationale": "directo"}""",                  // planner(child1)
        "respuesta del sub-objetivo 1",                                                   // worker(child1)
        """{"isLeaf": true,  "subGoals": [], "rationale": "directo"}""",                  // planner(child2)
        "respuesta del sub-objetivo 2",                                                   // worker(child2)
        "síntesis integrada de ambos sub-objetivos"                                       // synthesizer(root)
    };
    var llm = new FakeLlmGateway(script);
    var runner = BuildRunner(store, llm);

    var result = await runner.RunAsync("run-test", "¿por qué event sourcing?", CancellationToken.None);

    Assert.Equal(3, result.NodeCount);
    Assert.Equal("síntesis integrada de ambos sub-objetivos", result.Root.Answer);

    // El run quedó Completed en el event store…
    var runState = await LoadRunStateAsync(store, "run-test");
    Assert.Equal(WorkflowRunStatus.Completed, runState.Status);

    // …y cada nodo quedó persistido con su ciclo completo (3 eventos).
    foreach (var stream in NodeStreams(result))
    {
        var (events, state) = await LoadNodeStateAsync(store, stream);
        Assert.Equal(3, events.Length);   // Created + Planned + Completed
        Assert.Equal(WorkflowNodeStatus.Completed, state.Status);
    }

    Assert.Equal(11, await CountEventsAsync(store, result)); // run(2) + 3 nodos × 3
}
```

> **Ojo al orden del script**: la recursión es depth-first, así que cada hijo se
> completa entero (planner → worker) antes de pasar al siguiente hermano. El
> comentario del test lo documenta.

### La prueba de terminación

```csharp
[Fact]
public async Task Run_WithPlannerAlwaysDecomposing_StopsAtMaxDepth()
{
    var llm = new FakeLlmGateway(script: null);   // modo smart: SIEMPRE divide
    var runner = BuildRunner(store, llm, maxDepth: 2);

    var result = await runner.RunAsync("run-cap", "objetivo infinito", CancellationToken.None);

    Assert.Equal(7, result.NodeCount);            // 1 + 2 + 4 — acotado por MaxDepth
    var leaves = CollectLeaves(result.Root);
    Assert.Equal(4, leaves.Count);
    Assert.All(leaves, l => Assert.Equal(2, l.Depth));  // todas las hojas en MaxDepth
}
```

Si la recursión no tuviera freno, este test colgaría para siempre. Es la
garantía de terminación del workflow.

## Probalo

```bash
dotnet test                     # 72: 69 core + 3 del puente Miyu
dotnet test --filter "FullyQualifiedName~WorkflowNodeStateTests|FullyQualifiedName~WorkflowNodeCommandServiceTests"  # solo nodos
dotnet test --filter "FullyQualifiedName~RecursiveWorkflowRunnerTests"  # solo motor
dotnet test --filter "FullyQualifiedName~IncidentInvestigation"  # caso práctico
dotnet test --filter "FullyQualifiedName~MiyuEventuousBridge"    # integración completa
```

---

**Siguiente**: [Paso 11 · Estado del ejemplo](11-estado-del-ejemplo.md)
