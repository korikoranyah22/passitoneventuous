# Lección 1 · Event Sourcing: la historia completa

> **Idea central**: en vez de guardar *cómo está el mundo ahora* (una fila que
> se actualiza), guardamos **todo lo que pasó** (una lista inmutable de
> eventos). El estado actual se puede reconstruir en cualquier momento
> releyendo la historia.

## 1.1 El problema con el CRUD clásico

Pensemos en un objeto típico de base de datos relacional:

```sql
UPDATE workflow_runs SET status = 'Completed', answer = '...' WHERE run_id = 'run-1';
```

Después de este `UPDATE`, la información de que el run **estuvo** `Running`, de
cuánto tardó, de qué nodos se ejecutaron antes, **se perdió para siempre**. La
tabla solo sabe el presente.

Para un workflow de agentes eso es grave:

- **Auditoría**: ¿qué le pedimos al LLM? ¿cuántas llamadas se hicieron? ¿cuánto
  costó cada una? El CRUD no lo sabe.
- **Reanudación**: si el proceso muere a mitad de camino (el proveedor de LLM
  cayó, se cortó la luz), con CRUD solo tenemos un estado a medias, sin saber
  qué nodos alcanzaron a completarse.
- **Depuración**: cuando el LLM produce una respuesta rara, necesitamos poder
  reproducir *exactamente* qué inputs recibió. Con CRUD, no hay rastro.

## 1.2 La idea: los eventos son la fuente de verdad

En event sourcing, **la escritura es un append, nunca un update**:

```
workflow-run-1
  ├─ #1 WorkflowRunCreated   (goal: "¿Por qué los agentes necesitan event sourcing?", root: n-1)
  └─ #2 WorkflowRunCompleted (answer: "Porque son procesos largos, caros y fallibles…")

workflow-node-n-1
  ├─ #1 WorkflowNodeCreated  (depth 0, goal: …)
  ├─ #2 WorkflowNodePlanned  (isLeaf: false, children: [n-2, n-3])
  ├─ #3 WorkflowNodeCompleted (answer: "…respuesta sintetizada…")
```

Cada evento es:

- **Inmutable**: ya pasó, no se edita ni se borra.
- **Nombrado en pasado**: `Created`, `Planned`, `Completed` — nunca `Update`.
- **Autocontenido**: lleva los datos necesarios para reconstruir lo que pasó.

El **estado actual** (el aggregate) es una *derivación*: se obtiene aplicando
los eventos en orden. Nada más.

```
estado = fold(estado_inicial, eventos_en_orden)
```

## 1.3 Tres términos que vas a escuchar siempre

| Término | Qué es | Analogía |
|---|---|---|
| **Event** | Un hecho que ya ocurrió, inmutable | La entrada del diario |
| **Stream** | La secuencia de eventos de UNA entidad | El capítulo del diario de esa entidad |
| **Aggregate** | El estado actual, reconstruido desde el stream | Tu memoria hoy, reconstruida de lo que anotaste |

## 1.4 ¿Cuándo sí y cuándo no?

Event sourcing brilla cuando:

- ✅ El historial importa (finanzas, auditoría, workflows, máquinas de estado).
- ✅ El proceso es **largo y fallible** (un workflow de agentes con LLM: minutos,
  costoso, se cae).
- ✅ Necesitás **reproducir** estados pasados o **reanudar** después de un crash.

Es excesivo cuando:

- ❌ Solo necesitás el último valor y nadie pregunta por el historial.
- ❌ El equipo no está dispuesto a pagar la complejidad del modelo mental.

> **Para el curso**: un workflow de agentes es *el* caso de uso perfecto.
> Largo, caro, con decisiones de LLM que querés auditar, y con necesidad
> real de reanudación. Por eso el ejemplo de esta clase es exactamente eso.

---

## 📖 En el ejemplo

Mirá el aggregate `WorkflowRun` en
`02-ejemplo/src/CursoAgentes.Domain/Workflow/WorkflowRun.cs`: tres eventos
(`WorkflowRunCreated`, `WorkflowRunCompleted`, `WorkflowRunFailed`), un stream
por run (`workflow-run-{id}`), y un estado que se reconstruye aplicándolos.

En la lección 2 vemos cómo se modela esto con Eventuous.
