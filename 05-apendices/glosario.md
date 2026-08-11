# Glosario

## Event sourcing

| Término | Definición | En el ejemplo |
|---|---|---|
| **Event sourcing** | Patrón donde el estado se deriva de una historia inmutable de eventos, en vez de guardar el estado actual | Todo el curso |
| **Evento** | Un hecho del pasado, inmutable, nombrado en pasado (`Created`, `Completed`), autocontenido | `WorkflowRunCreated`, `WorkflowNodePlanned`… |
| **Stream** | La secuencia de eventos de UNA entidad, en orden | `workflow-run-{id}`, `workflow-node-{id}` |
| **Aggregate** | El estado actual de una entidad, reconstruido aplicando sus eventos en orden | `WorkflowRunState`, `WorkflowNodeState` |
| **Reproyección / replay** | Re-aplicar eventos (a un aggregate o a una proyección) para reconstruir estado | Los tests aplican eventos con `.When(...)` |
| **Event store** | El almacén de eventos (append-only) | Schema `curso_eventstore` en Postgres |
| **Checkpoint** | Hasta dónde leyó una suscripción (para reanudar tras un crash) | `AddPostgresCheckpointStore` |
| **Optimistic concurrency** | Control de versiones: el append falla si la versión esperada no coincide con la real | `ExpectedStreamVersion` en el store |

## Eventuous

| Término | Definición | En el ejemplo |
|---|---|---|
| **Command service** | Clase que recibe comandos, valida y emite eventos | `WorkflowRunCommandService`, `WorkflowNodeCommandService` |
| **Comando** | Una intención ("que pase esto"); no es un hecho | `StartWorkflowRun`, `CompleteWorkflowNode` |
| **Guard** | Validación del dominio que rechaza transiciones inválidas | "solo Running puede completarse", "hoja ≠ hijos" |
| **`[EventType]`** | Nombre estable del evento para serialización | `[EventType("V1.WorkflowRunCreated")]` |
| **`ExpectedState`** | `New` (stream no existe) / `Existing` (existe) / `Any` | `InState(ExpectedState.New)` en `Start` |
| **TypeMap** | Registro nombre-estable → clase C# (deserialización) | `TypeMap.RegisterKnownEventTypes(...)` en la demo |
| **Suscripción** | Proceso que escucha eventos del store y los despacha | `PostgresAllStreamSubscription` → proyección |

## Agentes y LLM

| Término | Definición | En el ejemplo |
|---|---|---|
| **Workflow** | Secuencia de pasos con roles, ejecutable y auditable | planificar → dividir/responder → sintetizar |
| **Nodo recursivo** | Un paso que puede crear sub-pasos del mismo tipo | `ExecuteNodeAsync` se llama a sí mismo |
| **Hoja** | Nodo sin hijos: se responde directo | `plan.IsLeaf` |
| **Síntesis** | Integración de respuestas parciales en una conclusión | `SynthesizerAgent` |
| **Puerto / adaptador** | Interfaz (puerto) + implementaciones intercambiables (adaptadores) | `ILlmGateway` + `FakeLlmGateway` / `OpenAiCompatibleGateway` |
| **System prompt** | Instrucciones de rol al LLM (no cambian por llamada) | Los prompts del `WorkflowManifest` |
| **Temperatura** | Aleatoriedad de la respuesta (baja = más determinista) | planner 0.2, worker 0.7, synth 0.4 |
| **Manifiesto** | Workflow como datos (config), no como código | `WorkflowManifest` bindeado de `appsettings.json` |

## CQRS y read model

| Término | Definición | En el ejemplo |
|---|---|---|
| **Read model** | Tablas optimizadas para consulta, derivadas de los eventos | `curso_readmodel.workflow_runs/nodes` |
| **Proyección** | Handler que convierte eventos en filas del read model (idempotente) | `WorkflowReadModelProjection` |
| **Upsert** | `INSERT … ON CONFLICT DO UPDATE` (escribir o actualizar) | Todos los handlers del read model |
| **Eventual consistency** | El read model va un ratito detrás del store | La demo espera a la proyección |
| **CQRS** | Separar el modelo de escritura (eventos) del de lectura (tablas) | Todo el paso 8 |

## Trampas clásicas (aprendidas en este curso)

| Trampa | Por qué pasa | Cómo se evita |
|---|---|---|
| "Me guarda pero no me lee" | Faltó registrar los tipos en el TypeMap | `TypeMap.RegisterKnownEventTypes(...)` al boot |
| Comando sobre aggregate inexistente "cuela" | El default del estado era un estado válido (`Running`) | Default = `None` (inválido); los guards lo rechazan |
| Recursión infinita del planner | El LLM siempre dice "dividí" | `Depth < MaxDepth` (freno duro) + `MaxChildrenPerNode` + JSON inválido → hoja |
| Efectos colaterales en el aggregate | Llamadas HTTP/LLM dentro del dominio | El aggregate solo valida y emite; el motor hace el I/O |
| Invariantes entre aggregates en el dominio | Un aggregate no lee el stream de otro | El orquestador aplica las invariantes cross-aggregate |
| El fake del LLM "siempre responde genérico" | `AddSingleton<ILlmGateway, FakeLlmGateway>()` + constructor con `IEnumerable<string>? script`: el DI resuelve la colección como VACÍA → modo scripted sin script → fallback | Construir el fake a mano sin script (`AddSingleton(sp => new FakeLlmGateway(...))`) — ver trampa 1 en el paso 11 |
| El fake "smart" arrastra el prompt como objetivo | `ExtractGoal` no existía: los sub-objetivos copiaban el mensaje de usuario completo (con `Objetivo:`, `Profundidad actual:` e instrucciones) y el parseo de profundidad leía la línea anidada | Simular el contrato del mensaje: extraer el goal limpio (`^Objetivo(?: original)?:`) y parsear depth sobre el mensaje crudo — ver trampa 2 en el paso 11 |
