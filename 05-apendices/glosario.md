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
| **Nodo recursivo** | Un paso que puede crear sub-pasos del mismo tipo | `ResumeNodeAsync` se llama a sí mismo |
| **Hoja** | Nodo sin hijos: se responde directo | `plan.IsLeaf` |
| **Síntesis** | Integración de respuestas parciales en una conclusión | `SynthesizerAgent` |
| **Puerto / adaptador** | Interfaz (puerto) + implementaciones intercambiables (adaptadores) | `ILlmGateway` + `FakeLlmGateway` / `OpenAiCompatibleGateway` |
| **System prompt** | Instrucciones de rol al LLM (no cambian por llamada) | Los prompts del `WorkflowManifest` |
| **Temperatura** | Aleatoriedad de la respuesta (baja = más determinista) | planner 0.2, worker 0.7, synth 0.4 |
| **Manifiesto** | Política configurable del workflow; no reemplaza toda su lógica ejecutable | `WorkflowManifest` bindeado de `appsettings.json` |
| **Router de gateways** | Selecciona una ruta por restricciones y preferencias; no ejecuta la llamada | `LlmGatewayRouter.SelectByTags` en MiyuAgents |
| **Perfil de ruta** | Requisitos de capacidad y preferencias de un tipo de trabajo | `LlmRouteProfile` + `RouteRequest` |
| **Retry / fallback** | Retry repite la misma ruta ante un fallo transitorio; fallback excluye esa ruta y vuelve a seleccionar | `ILlmCallExecutor` |
| **Gate determinista** | Regla en código que valida o autoriza una transición propuesta por agentes | validadores y `IncidentPolicy` |
| **Recursión funcional** | Refinar estado del mismo tipo hasta satisfacer un gate, con presupuestos y ciclos | `RecursiveObjectiveNode<TState>` |

## CQRS y read model

| Término | Definición | En el ejemplo |
|---|---|---|
| **Read model** | Tablas optimizadas para consulta, derivadas de los eventos | `curso_readmodel.workflow_runs/nodes` |
| **Proyección** | Handler que convierte eventos en filas del read model (idempotente) | `WorkflowReadModelProjection` |
| **Upsert** | `INSERT … ON CONFLICT DO UPDATE` (escribir o actualizar) | Altas idempotentes del read model; otros eventos usan `UPDATE` idempotente |
| **Eventual consistency** | El read model va un ratito detrás del store | La demo espera a la proyección |
| **CQRS** | Separar el modelo de escritura (eventos) del de lectura (tablas) | Todo el paso 8 |
| **Aceptación durable** | Confirmar un trabajo sólo después de persistir la intención mínima para reconstruirlo | `POST /api/workflow-runs` persiste run + raíz antes del `202` |
| **Aceptación idempotente** | Repetir una recepción con la misma identidad devuelve el mismo recurso; datos incompatibles producen conflicto | `Idempotency-Key` deriva un `runId` estable; otro `goal` devuelve `409` |
| **Solicitud durable de ejecución** | Hecho persistido que distingue trabajo autorizado para ejecutar de trabajo deliberadamente diferido | `WorkflowRunExecutionRequested` se escribe antes de encolar |
| **Recovery scanner** | Proceso que redescubre trabajo solicitado tras perderse una entrega volátil y confirma el estado en la fuente de verdad | Busca runs proyectados `Running` + solicitados y relee sus streams |
| **Worker de background** | Consumidor fuera del request que avanza trabajo largo | `WorkflowRunWorker` llama `ResumeAsync` |
| **Deduplicación local** | Evitar dos entregas simultáneas dentro de una instancia; complementa, pero no reemplaza, la coordinación compartida | `WorkflowRunQueue` mantiene el `runId` hasta finalizar |
| **Lease durable** | Derecho exclusivo pero temporal sobre un recurso, adquirido y renovado en un store compartido | `IExecutionLeaseStore` + tabla `curso_coordination.execution_leases` |
| **Takeover** | Adquisición válida después de que el lease anterior venció | Otro owner recibe un `LeaseToken` mayor |
| **Fencing token** | Número monotónico que permite rechazar operaciones tardías de un dueño anterior | El lease ya lo emite; propagarlo a cada append queda como endurecimiento |

## Trampas clásicas (aprendidas en este curso)

| Trampa | Por qué pasa | Cómo se evita |
|---|---|---|
| "Me guarda pero no me lee" | Faltó registrar los tipos en el TypeMap | `TypeMap.RegisterKnownEventTypes(...)` al boot |
| Comando sobre aggregate inexistente "cuela" | El default del estado era un estado válido (`Running`) | Default = `None` (inválido); los guards lo rechazan |
| Recursión infinita del planner | El LLM siempre dice "dividí" | `Depth < MaxDepth` (freno duro) + `MaxChildrenPerNode` + JSON inválido → hoja |
| Efectos colaterales en el aggregate | Llamadas HTTP/LLM dentro del dominio | El aggregate solo valida y emite; el motor hace el I/O |
| Invariantes entre aggregates en el dominio | Un aggregate no lee el stream de otro | El orquestador aplica las invariantes cross-aggregate |
| El read model decide si se puede reanudar | La proyección puede estar atrasada | Leer el stream con `WorkflowExecutionReader`; usar el read model sólo para consultar el árbol |
| La cola en memoria se trata como fuente de verdad | Un restart borra la cola | La solicitud queda en eventos y el recovery scanner vuelve a encolarla; `/resume` permite pedirlo explícitamente |
| El lease se trata como un lock eterno | El dueño puede caer sin liberar | Duración finita + renovación; otro host toma el recurso cuando vence |
| El logging frena una suscripción en Windows | Event Log puede requerir privilegios y lanzar al escribir | Limpiar providers del host y registrar consola explícitamente |
| El fake del LLM "siempre responde genérico" | `AddSingleton<ILlmGateway, FakeLlmGateway>()` + constructor con `IEnumerable<string>? script`: el DI resuelve la colección como VACÍA → modo scripted sin script → fallback | Construir el fake a mano sin script (`AddSingleton(sp => new FakeLlmGateway(...))`) — ver trampa 1 en el paso 11 |
| El fake "smart" arrastra el prompt como objetivo | `ExtractGoal` no existía: los sub-objetivos copiaban el mensaje de usuario completo (con `Objetivo:`, `Profundidad actual:` e instrucciones) y el parseo de profundidad leía la línea anidada | Simular el contrato del mensaje: extraer el goal limpio (`^Objetivo(?: original)?:`) y parsear depth sobre el mensaje crudo — ver trampa 2 en el paso 11 |
