# 02-ejemplo · El ejemplo funcional

Dos casos complementarios event-sourced con **Eventuous + PostgreSQL**: un
workflow de agentes con **nodos recursivos** y una investigación de incidentes
híbrida que combina artefactos LLM con política determinista.

> Esta carpeta es parte de la clase `clase-eventuous-agentes-llm/`. La teoría
> está en `../01-teoria/` y la guía para reconstruir esto paso a paso en
> `../03-guia-paso-a-paso/`.

Este ejemplo mantiene un único gateway para enseñar el puerto/adaptador sin
ruido adicional. La continuación aplicada —varios gateways, perfiles por
capacidades, retry/fallback y un flujo analista → crítico → regla— está en
[MiyuAgents/examples/routing-workflow](../../angelnairav2_public/Packages/MiyuAgents/examples/routing-workflow/)
y su representación equivalente como
[grafo fijo no recursivo](../../angelnairav2_public/Packages/MiyuAgents/examples/fixed-node-workflow/)
y se explica en el
[paso práctico 12](../03-guia-paso-a-paso/12-caso-practico-hibrido.md).

## Quickstart

```bash
# 1. Postgres (16) — único requisito externo
docker compose up -d

# 2. Suite completa: 72 tests (69 core + 3 del puente Miyu)
dotnet test

# 3. Demo end-to-end (usa el LLM FALSO por default: no gasta tokens)
dotnet run --project src/CursoAgentes.App

# Persistir ahora y continuar desde otro proceso
dotnet run --project src/CursoAgentes.App -- \
  --workflow-start "¿Cómo se reanuda un árbol?"
dotnet run --project src/CursoAgentes.App -- --workflow-resume run-1234abcd

# Host HTTP asíncrono (en otra terminal)
dotnet run --project src/CursoAgentes.Api --urls http://localhost:5090
# POST /api/workflow-runs con {"goal":"..."} e Idempotency-Key opcional

# 4. Modo incidente: aggregate, eventos, efecto idempotente y read model
dotnet run --project src/CursoAgentes.App -- --incident
dotnet run --project src/CursoAgentes.App -- --incident-pipeline
dotnet run --project src/CursoAgentes.App -- --incident-nodes

# Recuperar una acción estacionada, con identidad e idempotencia administrativa
dotnet run --project src/CursoAgentes.App -- \
  --incident-retry inv-1234abcd \
  --IncidentRetry:RequestId=retry-001 \
  --IncidentRetry:RequestedBy=course-operator \
  --IncidentRetry:Reason="provider repaired"

# 5. Con un LLM real: editá appsettings.json → Llm.Provider = "OpenAI"
#    (BaseUrl + ApiKey + Model compatibles con /chat/completions)
```

## Proyectos

```
src/
├── CursoAgentes.Domain        eventos, estado, comandos, guards (3 aggregates)
├── CursoAgentes.Engine        agentes, motor recursivo y proceso de incidentes
├── CursoAgentes.Infrastructure event store, gateway y dos read models
├── CursoAgentes.MiyuAgents    capa anti-corruption desde pipeline o nodos
├── CursoAgentes.App           demo de consola end-to-end
└── CursoAgentes.Api           API 202 + cola/worker + estado, árbol y auditoría
tests/
└── CursoAgentes.Tests         69 core + 3 del puente Miyu
```

## Cómo funciona el ejemplo (en 30 segundos)

1. Le das un objetivo al runner (`RecursiveWorkflowRunner.RunAsync`).
2. El **PlannerAgent** pregunta al LLM: ¿esto se divide o se responde directo?
   - Si se divide → crea hijos (cada uno es **un aggregate con su stream**),
     recursa sobre ellos, y el **SynthesizerAgent** integra las respuestas.
   - Si es hoja → el **WorkerAgent** responde directo.
3. **Cada transición se persiste como evento** en Postgres (`curso_eventstore`).
4. La demo imprime el árbol, la bitácora, **la auditoría de eventos crudos** y
   el read model (`curso_readmodel`).

`RunAsync` compone dos operaciones reutilizables. `StartAsync` persiste el run
y la raíz `Pending`; `ResumeAsync` reconstruye cada stream y avanza según el
estado del nodo. Los `Completed` sólo se leen, los `Pending` se planifican y los
`Planned` continúan. El plan guarda IDs y objetivos de hijos, de modo que una
caída entre planificar y crear un hijo no pierde trabajo estructural.

`CursoAgentes.Api` usa esa misma frontera: `POST /api/workflow-runs` primero
persiste run + raíz y devuelve `202 Accepted`; una cola deduplicada entrega el
`runId` a un `BackgroundService`, que llama `ResumeAsync` fuera del request.
`GET /api/workflow-runs/{id}` toma el estado fuerte desde los streams y arma el
árbol desde el read model eventualmente consistente. `/audit` consulta la
historia y `POST /resume` vuelve a encolar un run `Running`. La cola es local;
la durabilidad y los checkpoints siguen siendo eventos, no memoria del host.

Con `Idempotency-Key`, el alta deriva un `runId` estable: repetir clave + goal
devuelve el mismo recurso; reutilizarla con otro goal devuelve `409`. Antes de
encolar se persiste `WorkflowRunExecutionRequested`. Un scanner periódico usa
el read model para hallar candidatos, confirma cada uno contra su stream y
recupera entregas perdidas después de reiniciar. Un run creado con
`startImmediately:false` no tiene esa solicitud y no se ejecuta por accidente.

Cada worker adquiere además un lease en `curso_coordination.execution_leases`
antes de ejecutar. PostgreSQL arbitra dueño, vencimiento y token usando su
propio reloj; el host renueva durante el trabajo y cancela cooperativamente si
pierde la propiedad. El scanner omite leases activos para evitar churn, pero la
adquisición atómica del worker sigue siendo la autoridad ante carreras.

Con `--incident`, la app toma señal, análisis y crítica ya estructurados, los
registra en `IncidentInvestigation`, aplica `incident-policy/v1` y persiste
`IncidentActionDecided`. Una suscripción separada, con checkpoint propio,
ejecuta el efecto mediante una clave idempotente y recién entonces agrega el
evento de confirmación. La auditoría y el read model muestran la ruta/modelo
efectivos sin guardar prompts ni razonamiento interno.

`IncidentActionReactionProcessor` distingue fallos externos conocidos:
reintenta los transitorios con backoff y registra `IncidentActionParked` al
agotar el presupuesto o recibir un error permanente. Los errores inesperados
siguen abortando el consumo. `HttpIncidentActionPort` implementa el contrato
realista `POST + Idempotency-Key`; el proveedor por defecto continúa siendo
`InMemory`, por lo que el quickstart no necesita otro servicio.

Una acción estacionada no queda atrapada para siempre. El modo
`--incident-retry` envía `RetryParkedIncidentAction`; el aggregate registra
`IncidentActionRetryRequested` con actor, motivo, número y `RequestId`. La
suscripción durable vuelve a ejecutar el mismo efecto idempotente. Repetir un
`RequestId` ya aplicado no agrega eventos ni toca el puerto externo.

Para usar un proveedor externo, cambiá `IncidentAction:Provider` a `Http` y
configurá `BaseUrl`, `Path`, `Timeout` y `Retry`. El endpoint recibe
`{"action":"..."}` y el header `Idempotency-Key`; responde
`{"externalId":"...","wasAlreadyApplied":false}`.

`CursoAgentes.MiyuAgents` mantiene una API pública neutral: recibe sólo el id,
el pedido y un `CancellationToken`, sin filtrar tipos concretos de los ejemplos
hacia la aplicación. La capa toma `SharedData` del
pipeline o `Artifact` del grafo fijo, preserva la auditoría del routing y crea
el mismo `IncidentInvestigationInput`. Las variantes usadas por el puente
terminan en `decide-action`; por eso el efecto ocurre sólo en la reacción
durable de Eventuous.

## Estado de verificación

| Chequeo | Estado |
|---|---|
| `dotnet build` (net10.0) | ✅ sin errores ni warnings |
| `dotnet test` | ✅ 72/72: 69 core + 3 del puente MiyuAgents → Eventuous |
| Build de API + suite core | ✅ 0 errores, 0 warnings |
| Solución completa en el checkout actual | ✅ ejemplos Miyu restaurados; 0 errores y 0 warnings |
| Demo recursiva con Postgres | ✅ verificada — ver `../03-guia-paso-a-paso/11-estado-del-ejemplo.md` |
| `--workflow-start` + `--workflow-resume` en procesos separados | ✅ 2 eventos antes; 47 al completar; segundo resume no agrega eventos |
| API `202` + status/tree + audit + resume | ✅ 2 eventos diferidos; 15 nodos/48 eventos y 16 streams al completar |
| `Idempotency-Key` + crash/restart | ✅ replay estable, conflicto `409`; scanner recuperó un árbol de 2047 nodos con una sola solicitud |
| Dos APIs + lease PostgreSQL | ✅ una sola ejecutó; 127 nodos, 384 eventos, 128 streams y lease liberado |
| Modos `--incident`, `--incident-pipeline` y `--incident-nodes` con Postgres | ✅ verificados: cinco eventos antes del efecto, seis al confirmar y checkpoints independientes |
| Proveedor HTTP inaccesible | ✅ termina en `ActionParked` con falla e intentos; no inventa `ExternalId` |
| `--incident-retry` sobre un stream estacionado | ✅ agrega solicitud + confirmación; repetir el `RequestId` no supera 8 eventos |
