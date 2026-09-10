# Paso 9 · App de demostración

## Objetivo

La demo end-to-end por consola (`src/CursoAgentes.App/Program.cs`): conecta con
Postgres, prepara los read models, arranca el host (Eventuous crea el schema), corre el workflow y
muestra **cuatro vistas** del mismo hecho: el árbol en memoria, la bitácora, la
auditoría de eventos crudos y el read model.

## El flujo del programa

```csharp
// 0. Registrar los tipos de eventos en el TypeMap.
//    Sin esto Eventuous no deserializa los eventos guardados en Postgres
//    (el clásico "me guarda pero no me lee").
TypeMap.RegisterKnownEventTypes(typeof(WorkflowRunEvents.V1.WorkflowRunCreated).Assembly);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCursoAgentesInfrastructure(builder.Configuration);
using var host = builder.Build();

// 1. Postgres disponible? (poll con mensaje amable)
await EnsurePostgresAsync(connectionString, ct);

// 2. Inicializar las tablas de lectura antes de las suscripciones (idempotente).
var readModel = host.Services.GetRequiredService<WorkflowReadModelStore>();
var incidentReadModel = host.Services.GetRequiredService<IncidentReadModelStore>();
await readModel.InitializeAsync(ct);
await incidentReadModel.InitializeAsync(ct);

// 3. Arrancar el host → Eventuous crea curso_eventstore y arrancan las
//    suscripciones. Preparar las tablas antes evita una carrera durante replay.
await host.StartAsync(ct);
await WaitForEventStoreObjectsAsync(connectionString, ct);

// 4. Correr el workflow.
var runner = host.Services.GetRequiredService<RecursiveWorkflowRunner>();
var result = await runner.RunAsync(runId, goal, ct);

// 5a. Árbol + bitácora + respuesta final.
PrintTree(result.Root, "");
// 5b. Auditoría: los eventos crudos de curso_eventstore.messages.
await PrintAuditTrailAsync(connectionString, runId, result, ct);
// 5c. Read model (esperando a que la proyección alcance).
await WaitForProjectionAsync(readModel, runId, TimeSpan.FromSeconds(10), ct);
```

## Los tres detalles que importan

### 1. El TypeMap antes que todo

```csharp
TypeMap.RegisterKnownEventTypes(typeof(WorkflowRunEvents.V1.WorkflowRunCreated).Assembly);
```

Eventuous guarda los eventos con su **nombre estable** (`V1.WorkflowRunCreated`
— el `[EventType]`). El TypeMap dice "este nombre → esta clase C#". Sin el
registro, el store guarda pero no puede volver a deserializar.

### 2. Esperar al schema del event store

`AddEventuousPostgres(…, initializeDatabase: true)` crea el schema **cuando
arranca el host** (es un hosted service). Las tablas propias se preparan antes;
después la demo inicia el host y espera el schema del event store:

```csharp
await readModel.InitializeAsync(ct);
await incidentReadModel.InitializeAsync(ct);
await host.StartAsync(ct);
await WaitForEventStoreObjectsAsync(connectionString, ct);  // poll hasta 60s
```

Espera a que existan el tipo `stream_message` y la tabla `messages` antes de
correr el workflow. Es el mismo patrón del repo real que inspira el curso.

### 3. La auditoría como argumento visual

```sql
SELECT m.global_position, s.stream_name, m.message_type, m.stream_position, m.created, m.json_data::text
  FROM curso_eventstore.messages m
  JOIN curso_eventstore.streams s ON s.stream_id = m.stream_id
 WHERE s.stream_name = ANY(@streams)
 ORDER BY m.global_position
```

Imprime cada evento del run: stream, tipo, posición y payload. Es la
**prueba concreta** de que el workflow quedó persistido como historia — no como
un estado volátil.

## Probalo

```bash
docker compose up -d
dotnet run --project src/CursoAgentes.App
# con objetivo propio:
dotnet run --project src/CursoAgentes.App "¿Qué es CQRS?"

# separar inicio y continuación en dos procesos:
dotnet run --project src/CursoAgentes.App -- \
  --workflow-start "¿Cómo se reanuda un árbol?"
dotnet run --project src/CursoAgentes.App -- --workflow-resume run-1234abcd
```

El primer comando persiste dos eventos y no llama agentes. El segundo usa
`WorkflowExecutionReader` sobre el event store, no sobre la proyección, y
continúa el nodo raíz `Pending`. Repetir el segundo comando cuando el run ya
está `Completed` reconstruye el árbol y no agrega eventos.

Salida esperada (resumida):

```
✔ Conexión a Postgres establecida.
✔ Event store inicializado (stream_message + messages).
▶ Ejecutando workflow para: «¿Por qué los agentes necesitan event sourcing?»
── ÁRBOL DE NODOS ──
▪ n-…  profundidad=0  «¿Por qué los agentes necesitan event sourcing?»
  ↳ …
── AUDITORÍA: eventos crudos del event store ──
  #1 workflow-run-run-abc123  V1.WorkflowRunCreated  (pos 0)
  #2 workflow-node-n-…        V1.WorkflowNodeCreated (pos 0)
  …
── READ MODEL ──
  run=run-abc123  estado=Completed  objetivo=«…»
✅ Demo completa.
```

> **Configuración de logging**: en `appsettings.json`,
> `"CursoAgentes": "Information"` muestra la bitácora del motor y de los
> gateways; `"Default": "Warning"` mantiene el resto silencioso.

---

**Siguiente**: [Paso 10 · Tests](10-tests.md)
