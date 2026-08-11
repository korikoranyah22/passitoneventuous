# Lección 7 · Por qué event sourcing para agentes

> Esta es la lección que une todo el curso. Los workflows de agentes tienen
> cuatro propiedades que los hacen **el caso de uso natural** del event
> sourcing: son **largos**, **caros**, **fallibles** y **necesitan auditoría**.

## 7.1 Las cuatro propiedades

### 1. Son largos

Un workflow de investigación con nodos recursivos puede tardar **minutos** (o
más) y hacer decenas de llamadas al LLM. Durante ese tiempo, el proceso vive en
múltiples máquinas, con reintentos, timeouts y despliegues. Un estado en
memoria no sobrevive a nada de eso.

**Event sourcing**: el estado del árbol vive en Postgres como eventos. En
cualquier momento podés reconstruir *dónde estábamos* releyendo los streams.

### 2. Son caros

Cada llamada al LLM cuesta plata y tiempo. Si un proceso muere a mitad de
camino y no sabés qué se hizo, **repetís trabajo pagado** (y encima las
respuestas pueden variar entre intentos).

**Event sourcing**: sabés exactamente qué nodos ya se completaron y con qué
respuesta. Reanudás desde ahí, no desde cero.

### 3. Son fallibles

El LLM es **no determinista**: hoy responde una cosa, mañana otra. Y se cae.
Timeouts, rate limits, respuestas vacías, JSON inválido… falla seguido.

**Event sourcing**: el fallo es un evento más (`WorkflowNodeFailed`,
`WorkflowRunFailed`). El run queda en estado `Failed` con el rastro completo de
lo que pasó antes — y se puede inspeccionar y reintentar.

### 4. Necesitan auditoría

"¿Por qué el agente respondió esto?" es la pregunta más difícil de responder
con un sistema opaco.

**Event sourcing**: la respuesta está en el store. Cada llamada, cada decisión
de planificación, cada síntesis, quedó como evento inmutable con timestamp. La
demo imprime esa auditoría (`PrintAuditTrailAsync`): es EL argumento visual del
curso.

## 7.2 El patrón que se repite en producción

```
┌────────────┐   comando    ┌──────────────────────┐   evento   ┌──────────────────┐
│  Cliente   │ ───────────► │  CommandService      │ ─────────► │  Event Store     │
│ (API, app) │              │  (valida + emite)    │            │  (Postgres)      │
└────────────┘              └──────────────────────┘            └────────┬─────────┘
                                                                         │ suscripción
                                                            ┌────────────▼─────────┐
                                                            │  Proyección → read   │
                                                            │  model (consultas)   │
                                                            └──────────────────────┘
```

Los agentes se sientan en el medio: el **orquestador** manda comandos, recibe
eventos, y entre comando y comando hace su trabajo (llamar al LLM, decidir,
recursar).

## 7.3 Lo que el event sourcing NO te da (honestidad)

- **No es un message bus**: los eventos son historia, no mensajes entre
  servicios. Si necesitás eventos de integración, los *derivás* con
  suscripciones/proyecciones.
- **No hace mágica la reanudación**: te da los datos para reanudar; el *cómo*
  reanudar (reprocesar nodos pendientes) es lógica tuya de orquestación.
- **Cuesta más al principio**: modelar eventos + estado + guards + proyección
  es más trabajo que un CRUD. Se paga solo cuando las cuatro propiedades de
  7.1 están presentes.

## 7.4 Y la pregunta del millón: ¿cada nodo es un aggregate?

En el ejemplo, **sí**: cada nodo del árbol es un aggregate con su propio
stream (`workflow-node-{id}`). ¿Por qué?

- Cada nodo tiene **ciclo de vida propio** (Pending → Planned → Completed) y
  **guards propios** (no completar dos veces, hoja ≠ hijos).
- El stream por nodo te da **granularidad de auditoría**: podés ver la historia
  de una rama sin leer todo el árbol.
- La **concurrencia** entre nodos es natural: distintos nodos pueden ejecutarse
  en paralelo (aquí lo hacemos secuencial por simplicidad, pero el modelo lo
  permite).

¿Y el run? Un aggregate `WorkflowRun` por ejecución, que referencia el nodo
raíz. Es la "portada" del árbol.

> Esta decisión (un aggregate por nodo vs. un solo aggregate con toda la
> estructura) es el tipo de trade-off que se discute en producción. El ejemplo
> elige la opción granular porque muestra mejor el patrón y escala a
> ejecución paralela.

## 7.5 El cierre

> **Un agente es un workflow; un workflow es una máquina de estados; una
> máquina de estados con eventos inmutables en Postgres es un aggregate
> Eventuous.** Y cuando el LLM falla (va a pasar), la historia de lo que ya se
> hizo sigue ahí, esperando que la reanudes.

---

## 📖 En el ejemplo

- Demo end-to-end + auditoría: `02-ejemplo/src/CursoAgentes.App/Program.cs`
- El runner que persiste cada transición: `02-ejemplo/src/CursoAgentes.Engine/Workflow/RecursiveWorkflowRunner.cs`
- Test del run fallido (LLM caído → `WorkflowRunFailed` persistido):
  `02-ejemplo/tests/CursoAgentes.Tests/RecursiveWorkflowRunnerTests.cs` → `Run_WhenLlmThrows_RunEndsFailed_InEventStore`
