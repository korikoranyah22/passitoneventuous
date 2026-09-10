# Lección 4 · Agentes como workflows

> Un agente de IA no es "una llamada a un LLM". Es un **proceso** que combina:
> un objetivo, pasos de razonamiento, herramientas, memoria y llamadas al LLM
> en el medio. En esta clase modelamos ese proceso como un **workflow**: una
> secuencia de pasos con roles claros, que se puede ejecutar, auditar y
> reanudar.

## 4.1 El problema: un solo prompt no alcanza

Pedirle al LLM "resolveme esto" en una sola llamada falla cuando el objetivo es
grande:

- El contexto no entra (objetivos con muchas partes).
- La respuesta es superficial (el LLM "resume" en vez de investigar).
- No podés saber *qué pasos* se dieron ni *dónde* falló.

La solución que usa la industria (y esta clase) es **descomponer el trabajo en
pasos manejables, con roles especializados**, y dejar que los resultados se
vuelvan a integrar. Eso es un **workflow de agentes**.

## 4.2 Tres conceptos que viajan por el workflow

### El agente

Una unidad con **identidad, rol y tarea**:

```csharp
public abstract class AgentBase
{
    public abstract string AgentId { get; }      // "planner", "worker", "synthesizer"
    public abstract string AgentName { get; }    // nombre legible
    public abstract AgentRole Role { get; }      // qué tipo de tarea hace
    public abstract Task<string?> ExecuteAsync(AgentContext context, CancellationToken ct);
}
```

En el ejemplo hay tres agentes (mirá `02-ejemplo/src/CursoAgentes.Engine/Workflow/`):

| Agente | Rol | Pregunta que responde |
|---|---|---|
| `PlannerAgent` | Estructura | ¿Este objetivo se divide o se responde directo? |
| `WorkerAgent` | Ejecución | Respondé este objetivo concreto (hoja) |
| `SynthesizerAgent` | Integración | Combiná las respuestas parciales en una conclusión |

Cada agente **es** básicamente un prompt + una llamada al LLM + un parseo. Lo
interesante no es cada agente: es cómo se orquestan.

### El contexto

El "maletín" que viaja por el workflow — quién es el run, qué nodo estamos
ejecutando, qué sabemos hasta ahora:

```csharp
public sealed record AgentContext
{
    public required string RunId { get; init; }
    public required string NodeId { get; init; }
    public required string Goal { get; init; }
    public int Depth { get; init; }
    public int MaxDepth { get; init; }
    public IReadOnlyDictionary<string, string> ChildAnswers { get; init; } = ...;
    public List<string> Steps { get; } = [];
    public Dictionary<string, object> Metadata { get; } = [];
}
```

Cada paso lee del contexto, lo enriquece y lo pasa al siguiente. En el ejemplo
cada ejecución recursiva arma su propio contexto (con su nodo y su objetivo).

### La política configurable del workflow

Un workflow bien diseñado separa el **motor estable** de la **política
configurable**. El manifiesto permite cambiar límites, prompts y modelo sin
recompilar; la topología y el comportamiento ejecutable siguen siendo código
cuando corresponde.

```csharp
public sealed record WorkflowManifest
{
    public int MaxDepth { get; init; } = 3;            // tope de recursión
    public int MaxChildrenPerNode { get; init; } = 3;  // máx. hijos por nodo
    public string PlannerSystemPrompt { get; init; } = "[PLANNER] …";
    public string WorkerSystemPrompt { get; init; } = "[WORKER] …";
    public string SynthesizerSystemPrompt { get; init; } = "[SYNTHESIZER] …";
    public string? Model { get; init; }
}
```

En la app se bindea desde `appsettings.json` (sección `Workflow`): cambiar el
tope de profundidad o los prompts **no toca una línea de código**.

## 4.3 El patrón general del workflow del ejemplo

```
RunAsync(objetivo)
  ├─ crea WorkflowRun (evento WorkflowRunCreated)
  ├─ crea el nodo raíz (evento WorkflowNodeCreated)
  └─ ResumeNodeAsync(raíz)                     ← el patrón que se repite
       ├─ Planner: ¿hoja o se divide?
       ├─ si divide → para cada sub-objetivo: crea hijo + ResumeNodeAsync(hijo)  ← recursión
       │              después: Synthesizer integra las respuestas
       └─ si es hoja → Worker responde directo
  └─ completa WorkflowRun (evento WorkflowRunCompleted)
```

Cada transición se **persiste como evento**. El workflow no es un script que
corre y se olvida: es un proceso con historia.

## 4.4 ¿Dónde vive la lógica del workflow? (separación de capas)

| Capa | Qué hace | Dónde está en el ejemplo |
|---|---|---|
| **Dominio** | Invariantes DE un nodo/run (guards) | `CursoAgentes.Domain` |
| **Motor** | Orquestación, recursión, llamadas al LLM | `CursoAgentes.Engine` |
| **Infraestructura** | Event store, proyecciones, adaptadores HTTP | `CursoAgentes.Infrastructure` |
| **Aplicación** | Demo, wiring, configuración | `CursoAgentes.App` |

> Regla de oro: **el aggregate valida, el orquestador decide**. Un nodo no
> puede completarse dos veces (lo valida el aggregate). Un padre solo se
> completa cuando todos sus hijos están completos (lo garantiza el
> orquestador, que es quien conoce el árbol completo). Los aggregates no leen
> los streams de otros aggregates — eso es orquestación.

---

## 📖 En el ejemplo

- `AgentBase`, `AgentContext`, `AgentRole`: `02-ejemplo/src/CursoAgentes.Engine/Agents/`
- Los tres agentes: `02-ejemplo/src/CursoAgentes.Engine/Workflow/{PlannerAgent,WorkerAgent,SynthesizerAgent}.cs`
- Manifiesto: `02-ejemplo/src/CursoAgentes.Engine/Workflow/WorkflowManifest.cs`
- El orquestador: `02-ejemplo/src/CursoAgentes.Engine/Workflow/RecursiveWorkflowRunner.cs`
