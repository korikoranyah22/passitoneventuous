# Ejercicios

Trece ejercicios ordenados por dificultad. Cada uno tiene un **objetivo**, los
**archivos que vas a tocar** y una **pista**. Las soluciones orientativas están
en `soluciones-orientativas.md` (leelas después de intentarlo, no antes 😉).

---

## Nivel fácil

### Ejercicio 1 · Cambiá el tope de profundidad

**Objetivo**: entender cómo el manifiesto controla el comportamiento del
workflow sin tocar código.

1. En `src/CursoAgentes.App/appsettings.json`, poné `"Workflow": { "MaxDepth": 1 }`.
2. Corré `dotnet run --project src/CursoAgentes.App`.
3. Observá el árbol: ¿cuántos niveles tiene? ¿Y con `MaxDepth: 5`?

**Preguntas**:
- ¿Por qué `MaxDepth: 1` sigue produciendo 3 nodos (raíz + 2 hijos)?
- ¿Qué pasaría con `MaxDepth: 0`? Probálo.

### Ejercicio 2 · El LLM falso contra el real

**Objetivo**: ver la diferencia entre simulación y producción.

1. Corré la demo con el fake (default) y guardá la salida.
2. Si tenés Ollama: cambiá `"Llm": { "Provider": "OpenAI", "BaseUrl": "http://localhost:11434/v1", "Model": "llama3.2" }` y corré de nuevo.
3. Compará: árbol, respuestas, y la *forma* de las respuestas (el fake es determinista; el real no).
4. Como variante, usá
   `MiyuAgents/examples/real-providers` para repetir la llamada con Anthropic,
   Gemini o cualquier preset OpenAI-compatible sin modificar el workflow.

**Pregunta**: ¿en qué partes del flujo el determinismo del fake oculta problemas que sí tendrías en producción? (Pista: JSON del planner.)

---

## Nivel medio

### Ejercicio 3 · Parallelizá los hijos

**Objetivo**: que los hijos de un nodo corran en paralelo.

En `RecursiveWorkflowRunner.ResumeNodeAsync`, el loop actual es:

```csharp
for (var i = 0; i < state.ChildrenIds.Length; i++)
{
    // lee o crea el hijo desde el plan persistido y lo reanuda con await
}
```

Cambialo para ejecutar los hijos con `Task.WhenAll`. Cuidado con:

- `List<string>` no acepta escrituras concurrentes: usá una bitácora por hijo
  y unilas al terminar.
- El orden de los eventos en el store (los hijos ya no terminan en orden).
- Las invariantes: ¿sigue garantizándose "el padre completa después que sus hijos"?

**Pista**: guardá las tareas en una lista, `await Task.WhenAll(tasks)`, y recién
después armá `children`/`childAnswers` recorriendo las tareas terminadas.

### Ejercicio 4 · Costo por run

**Objetivo**: llevar la telemetría de tokens al read model.

`LlmResponse` trae `InputTokens` y `OutputTokens`. Hoy se descartan. Agregá un
evento `WorkflowNodeLlmCallRecorded(NodeId, InputTokens, OutputTokens,
Model)` emitido por el motor (o mejor: un agregado de telemetría), y mostrá en
la demo el total de tokens (y un costo estimado) por run.

**Preguntas**:
- ¿Dónde emitís el evento: dentro del aggregate de nodo o en un aggregate
  aparte? ¿Por qué?
- ¿Qué pasa con los tokens si reintentás una llamada? (Esto es *exactamente*
  el problema que resuelve la auditoría por eventos.)

### Ejercicio 5 · Reintentos con backoff

**Objetivo**: hacer resiliente el gateway sin tocar el motor.

Creá un `RetryingLlmGateway : ILlmGateway` que **envuelva** a otro gateway
(patrón decorator):

```csharp
public sealed class RetryingLlmGateway : ILlmGateway
{
    private readonly ILlmGateway _inner;
    private readonly int _maxRetries;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        // intentá _maxRetries veces con backoff exponencial;
        // si el proveedor responde 429/5xx, esperá y reintentá
    }
}
```

Registralo en DI envolviendo al gateway real. El motor no cambia ni una línea.
Este decorator es un primer ejercicio local. Para varios gateways, clasificación
de fallos y fallback entre rutas, comparalo después con `ILlmCallExecutor` de
MiyuAgents en el ejercicio 9.

**Pregunta**: ¿qué garantiza este retry respecto de los eventos y qué no
garantiza respecto del costo o la no determinación del LLM? (Pista: ver
ejercicio 4.)

---

## Nivel difícil

### Ejercicio 6 · Reanudación post-crash (la joya)

**Objetivo**: demostrar el valor del event sourcing: retomar un run a mitad de
camino.

La solución base ya incluye `WorkflowExecutionReader` y
`RecursiveWorkflowRunner.ResumeAsync`. Revisalos y verificá que:

1. Lea el stream del run (`workflow-run-{id}`) y los de sus nodos.
2. Reconstruya el árbol: qué nodos están `Completed` (con su respuesta), cuáles
   `Planned` sin completar, cuáles `Pending`.
3. **Continúe solo desde los nodos pendientes** (planificar/completar lo que
   falta) sin re-ejecutar lo ya hecho.

**Pista**: prestá atención a `ChildGoals` en `WorkflowNodePlanned`. Un nodo
`Planned` puede referenciar un hijo cuyo stream todavía no fue creado; sin el
objetivo persistido no sería posible recrearlo.

**Para probar**: usá `--workflow-start "objetivo"` y copiá el `runId` en
`--workflow-resume`. Después repetí el resume sobre el run `Completed`: no debe
agregar eventos. Como extensión, agregá un checkpoint intermedio para la
respuesta del worker y cerrá también la ventana “LLM respondió pero Complete
todavía no se persistió”.

### Ejercicio 7 · Takeover y fencing estricto

**Objetivo**: cerrar la carrera que queda si un proceso se pausa, pierde el
lease y después despierta cuando otro dueño ya está ejecutando.

Partí de `CursoAgentes.Api`, que ya tiene lease PostgreSQL, renovación,
expiración, takeover y cancelación cooperativa. Extendelo con:

- propagación de `LeaseToken` en un contexto de ejecución;
- una guarda durable que rechace escrituras con un token menor al vigente;
- una prueba donde A se pausa, vence, B adquiere un token mayor y A despierta;
- métricas de adquisición fallida, renovación, pérdida y takeover.

**Preguntas**:
- ¿Por qué cancelar un `CancellationToken` no detiene instantáneamente un proceso pausado?
- ¿En qué frontera validarías el fencing token: antes del LLM, en cada append o ambas?
- ¿Qué parte es coordinación operacional y qué parte merece auditoría de negocio?

### Ejercicio 8 · Proyección de resumen

**Objetivo**: una segunda proyección para una consulta distinta.

Hoy el read model tiene por-run y por-nodo. Agregá una tabla `workflow_stats`
con una fila por día: `fecha, runs_completados, runs_fallidos,
total_nodos, total_tokens` (si hiciste el ejercicio 4). Necesitás una segunda
suscripción o extender la proyección existente.

**Pregunta**: ¿por qué tener DOS proyecciones sobre los MISMOS eventos es
natural en event sourcing y sería raro en CRUD?

### Ejercicio 9 · Routing por perfiles y failover

**Objetivo**: dejar de elegir proveedores dentro de los agentes.

Partí del ejemplo `MiyuAgents/examples/routing-workflow`:

1. Registrá dos gateways compatibles con el perfil `critical-judge`.
2. El primario debe tener mayor prioridad y fallar con HTTP 503.
3. Configurá dos intentos por ruta y dos rutas máximas.
4. El segundo gateway debe responder correctamente.
5. Verificá que `LlmExecutionResult.Attempts` contenga:
   `primary, primary, backup`.

Después cambiá el fallo primario a HTTP 401. Debe quedar:
`primary, backup`, sin retry inútil.

**Preguntas**:

- ¿Qué información pertenece al perfil y cuál al gateway?
- ¿Por qué una ruta explícita todavía debe respetar tags requeridas y excluidas?
- ¿Qué parte debería configurar y persistir el producto host y cuál debería
  seguir siendo genérica en MiyuAgents?

### Ejercicio 10 · Frontera determinista e idempotencia

**Objetivo**: convertir el workflow de incidentes en un proceso event-sourced
que pueda reanudarse sin repetir efectos.

Revisá `IncidentInvestigation` y extendelo con eventos para:

1. inicio de investigación;
2. señal recolectada;
3. diagnóstico producido;
4. crítica completada;
5. acción decidida;
6. incidente externo abierto;
7. acción estacionada tras un fallo permanente o retries agotados;
8. recuperación manual solicitada con actor, motivo y clave idempotente.

Implementá un test que reconstruya el aggregate desde esos eventos y pruebe que:

- el replay no llama al collector ni al LLM;
- la misma confirmación se acepta como replay idempotente, pero una constancia
  conflictiva se rechaza;
- una crítica positiva no alcanza si las señales deterministas están debajo del
  umbral;
- la clave idempotente del sistema externo deriva del `InvestigationId`;
- HTTP 503 se reintenta, HTTP 400 se estaciona sin retry y una excepción
  desconocida deja pendiente el checkpoint.
- una acción estacionada puede reactivarse, pero repetir el mismo `RequestId`
  no agrega eventos ni repite el efecto externo.

Como extensión, agregá `AcknowledgedBy` a la rama de revisión humana sin tocar
el pipeline ni el grafo de nodos que produjeron los artefactos.

**Pregunta**: ¿guardarías el razonamiento interno completo del LLM? Justificá la
respuesta considerando privacidad, tamaño, auditoría y reproducibilidad.

### Ejercicio 11 · Pipeline versus grafo fijo

**Objetivo**: elegir una abstracción de control por necesidad concreta, sin usar
recursión como opción predeterminada.

Corré y compará:

- `MiyuAgents/examples/routing-workflow`;
- `MiyuAgents/examples/fixed-node-workflow`.

Ambos ejemplos comparten datos, contratos, routing, política y puerto de acción.
Completá una tabla indicando dónde queda en cada uno:

1. el resultado de `normalize`;
2. el corte por un `IncidentDraft` inválido;
3. la ruta efectiva del analista;
4. la decisión determinista;
5. el recibo de la acción idempotente.

Después agregá una etapa determinista `EnrichOwner` entre normalización y
análisis en ambas variantes. No cambies `IncidentPolicy` ni el servicio LLM.

**Preguntas**:

- ¿Cuál variante requirió menos estructura para agregar un paso lineal?
- ¿Cuál ofrece una frontera más natural si luego querés anidar una rama?
- ¿Qué requisito nuevo justificaría migrar a `resumable`?
- ¿Qué requisito nuevo justificaría recursión real?

### Ejercicio 12 · Objetivos recursivos acotados en MiyuAgents

**Objetivo**: implementar recursión porque existe un criterio de progreso, no
porque el workflow tenga varios pasos.

Corré el ejemplo:

```bash
dotnet run --project ../angelnairav2_public/Packages/MiyuAgents/examples/recursive-review-workflow/RecursiveReviewWorkflowExample.csproj
```

El grafo exterior tiene tres etapas conocidas, pero cada etapa es un
`RecursiveObjectiveNode<TState>` que evalúa, refina y vuelve a evaluarse:

1. evidencia hasta cubrir costo, confiabilidad y operación;
2. borrador hasta tener recomendación, riesgos y citas;
3. red-team hasta que no queden bloqueos.

Hacé estas modificaciones de a una:

1. hacé que `CollectNextAsync` devuelva el mismo estado: verificá que corta por
   ciclo;
2. desactivá `DetectCycles` y verificá que corta por `MaxDepth` o `MaxCalls`;
3. agregá el criterio determinista `rollback-plan` al evaluador del borrador;
4. adaptá el refiner offline para producirlo en una revisión posterior;
5. reemplazá sólo el refiner del borrador por un `LlmCallExecutor` con un perfil
   `structured-brief-writer`; no cambies el evaluador ni la política recursiva.

Agregá tests que prueben cantidad de refinamientos, resultado final, ciclo y
presupuesto agotado.

**Preguntas**:

- ¿Por qué el grafo exterior sigue siendo una secuencia normal?
- ¿Qué diferencia hay entre anidar nodos y recursar estado funcionalmente?
- ¿Qué dato demuestra progreso en cada una de las tres etapas?
- ¿Qué persistirías para reanudar este loop tras un crash?
- ¿Por qué el LLM puede refinar pero no debería autorizar el corte final?

### Ejercicio 13 · IA defensiva con autoridad acotada

**Objetivo**: diseñar una defensa asistida por agentes sin delegar al modelo una
decisión irreversible.

Leé primero los [supuestos y fuentes oficiales](../06-casos-practicos-electorales/00-investigacion-y-supuestos.md)
y después corré las tres aplicaciones completas:

```bash
dotnet run --project 06-casos-practicos-electorales/src/ElectionAudit.Pipeline
dotnet run --project 06-casos-practicos-electorales/src/ElectionAudit.FixedNodes
dotnet run --project 06-casos-practicos-electorales/src/ElectionAudit.Recursive
dotnet test 06-casos-practicos-electorales/tests/ElectionAudit.Tests
```

Después resolvé, de a una, estas modificaciones:

1. eliminá primero el mismatch de release y verificá que la política deje de
   elegir la revisión urgente aunque persistan findings de otra categoría;
2. hacé que el assessment cite `finding-inventado` y verificá que el gate lo
   rechace antes de la crítica;
3. hacé que el refiner recursivo devuelva dos veces el mismo assessment y
   comprobá el corte por ciclo o presupuesto;
4. ejecutá dos veces el pipeline con el mismo `runId` y luego con IDs distintos;
   explicá por qué debe crear uno y dos casos respectivamente;
5. agregá una nueva categoría `configuration-drift` con detector determinista,
   artifact, criterio recursivo y test;
6. reemplazá los gateways offline por rutas Ollama privadas sin cambiar
   contratos, política ni case port.

**Preguntas**:

- ¿Qué datos puede inventar el LLM y cuál gate impide que se vuelvan evidencia?
- ¿Por qué el acuerdo entre analista y crítico no equivale a autorización?
- ¿Qué acciones permitirías automáticamente y cuáles exigirían dos personas?
- ¿Qué parte debe ser reproducible meses después de la elección?
- ¿Cómo evitarías enviar telemetría sensible a una ruta cloud por fallback?

---

## Rúbrica de autoevaluación

| Nivel | Hito |
|---|---|
| ✅ Básico | Ejercicios 1-2: entendés el rol del manifiesto y del fake |
| ✅ Intermedio | Ejercicios 3-5: tocás el motor y la resiliencia sin romper invariantes |
| ✅ Avanzado | Ejercicios 6-8: usás el event store como fuente para reanudar, exponer y derivar |
| ✅ Producción | Ejercicios 9-13: separás routing, resiliencia, autorización, idempotencia, control y recursión acotada |
