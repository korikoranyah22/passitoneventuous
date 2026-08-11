# Lección 5 · Nodos recursivos

> La técnica estrella de esta clase: **un nodo que decide si se divide o
> responde directo, y que cuando se divide ejecuta el MISMO proceso en cada
> hijo**. El resultado es un árbol de ejecución que crece hasta que cada hoja
> es un objetivo directamente respondible. Después, las respuestas suben por el
> árbol integrándose nivel por nivel.

## 5.1 El patrón en una imagen

```
«¿Por qué los agentes necesitan event sourcing?»        ← nodo raíz (depth 0)
├── «¿Qué es event sourcing y qué problemas resuelve?»   ← hijo (depth 1)
│   ├── «¿Qué es un evento inmutable?»                   ← hoja (depth 2) → respuesta
│   └── «¿Qué es un aggregate?»                          ← hoja (depth 2) → respuesta
│       ↳ síntesis del hijo                              ← hijo responde integrando
└── «¿Qué aporta a un workflow de agentes?»              ← hijo (depth 1)
    └── «¿Cómo se reanuda un proceso que falla?»         ← hoja (depth 2) → respuesta
        ↳ síntesis del hijo
    ↳ síntesis del raíz                                  ← RESPUESTA FINAL
```

Tres fases por nodo:

1. **Planificar** (`PlannerAgent`): ¿esto se responde directo o se divide?
2. **Dividir o responder**:
   - No-hoja → crear hijos y **recursar** sobre cada uno; al final **sintetizar**.
   - Hoja → `WorkerAgent` responde directo.
3. **Completar**: el nodo persiste su respuesta.

## 5.2 Por qué recursión (y no un pipeline plano)

- **Profundidad adaptativa**: un objetivo fácil se resuelve en un nodo; uno
  complejo se profundiza todo lo que haga falta. Un pipeline plano no puede
  hacer esto.
- **Composición**: cada sub-árbol es un workflow completo en sí mismo. Podés
  testear, reejecutar o reusar una rama sin tocar el resto.
- **Traza natural**: el árbol ES la explicación de cómo se llegó a la respuesta.

## 5.3 Cómo se corta la recursión (¡obligatorio!)

Un LLM que siempre dice "dividí" haría que la recursión nunca termine. Tres
frenos, en capas:

| Freno | Dónde | Qué hace |
|---|---|---|
| `ctx.Depth < ctx.MaxDepth` | runner | **hard stop**: nunca se baja de `MaxDepth` |
| `MaxChildrenPerNode` | planner | recorta sub-objetivos a N máximo (anti-explosión) |
| JSON inválido → hoja | planner | si el LLM no responde JSON, el nodo se vuelve hoja (se responde directo) |

Mirá la condición exacta en `RecursiveWorkflowRunner.ExecuteNodeAsync`:

```csharp
if (!plan.IsLeaf && ctx.Depth < ctx.MaxDepth && plan.SubGoals.Count > 0)
{
    // crear hijos, recursar, sintetizar…
}
else
{
    // hoja: responder directo
}
```

> **Defensa en profundidad**: una hoja de más es un resultado imperfecto; un
> loop infinito es un fallo catastrófico. Siempre preferí lo primero.

## 5.4 La invariante entre nodos (y quién la aplica)

- **Un nodo** no puede completarse dos veces → lo valida el **aggregate**
  (guard del dominio).
- **Un padre** solo se completa cuando **todos sus hijos están completos** →
  esto es una invariante *entre* aggregates (un aggregate no lee el stream de
  otro) → la aplica el **orquestador**, que conoce el árbol completo.

En el código: la recursión termina antes de sintetizar (`childResult = await
ExecuteNodeAsync(childCtx, …)` y solo después `SynthesizeAsync` + 
`CompleteWorkflowNode`). El runner *garantiza por construcción* que cuando el
padre se completa, todos sus hijos ya están `Completed`.

## 5.5 Terminación garantizada — el test que lo prueba

`Run_WithPlannerAlwaysDecomposing_StopsAtMaxDepth` en
`02-ejemplo/tests/CursoAgentes.Tests/RecursiveWorkflowRunnerTests.cs` usa un LLM
falso que **siempre** divide ("hasta el infinito"), con `MaxDepth = 2`:

```
raíz (d0) → 2 hijos (d1) → 4 hojas (d2)   = 7 nodos, y el run termina Completed ✅
```

Si la recursión no estuviera frenada, este test colgaría. Es la prueba de que
el workflow **termina siempre**, sin importar qué diga el LLM.

---

## 📖 En el ejemplo

- El runner recursivo (el corazón): `02-ejemplo/src/CursoAgentes.Engine/Workflow/RecursiveWorkflowRunner.cs`
- El resultado como árbol: `02-ejemplo/src/CursoAgentes.Engine/Workflow/WorkflowResult.cs`
- El planner con JSON + defensa: `02-ejemplo/src/CursoAgentes.Engine/Workflow/PlannerAgent.cs`
- Tests de terminación y de árbol: `02-ejemplo/tests/CursoAgentes.Tests/RecursiveWorkflowRunnerTests.cs`
