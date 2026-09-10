# Lección 3 · PostgreSQL como event store

> Postgres es **excelente** como event store: transacciones, índices,
> consistencia y un `jsonb` perfecto para payloads de eventos. En este curso no
> necesitamos un store especializado: **el mismo Postgres guarda los eventos,
> las tablas de consulta y la coordinación operativa**, en schemas separados.

## 3.1 Los tres esquemas del ejemplo terminado

```
cursoagentesdb (una sola base Postgres)
├── curso_eventstore        ← escriben los comandos (event sourcing)
│   ├── streams             (stream_id, stream_name, version)
│   ├── messages            (message_id, message_type, stream_id, stream_position,
│   │                        global_position, json_data, json_metadata, created)
│   └── tipo compuesto stream_message
├── curso_readmodel         ← escriben las proyecciones (lectura)
│   ├── workflow_runs
│   ├── workflow_nodes
│   └── incident_investigations
└── curso_coordination      ← coordinación entre instancias
    └── execution_leases
```

- `curso_eventstore` lo crea y administra **Eventuous** (lección: "me guarda
  los eventos con nombre, posición y payload jsonb").
- `curso_readmodel` son tablas planas nuestras, escritas por la proyección
  (lección 8).
- `curso_coordination` contiene leases operativos para que dos hosts no ejecuten
  el mismo run al mismo tiempo. No es historia de negocio ni fuente de verdad:
  puede reconstruirse y sus filas vencen.

## 3.2 Qué hace Eventuous.Postgresql por vos

Con cuatro líneas en el `DependencyInjection`:

```csharp
var connectionString = configuration.GetConnectionString("EventStore")!;
services.AddEventuousPostgres(connectionString, "curso_eventstore", initializeDatabase: true);
services.AddEventStore<PostgresStore>();
services.AddPostgresCheckpointStore();
```

- **`AddEventuousPostgres(conn, schema, initializeDatabase: true)`**: registra el
  `NpgsqlDataSource` y un hosted service que **crea el schema del event store**
  al arrancar el host (tablas + tipo compuesto `stream_message`). Por eso en la
  app esperamos a que el host arranque antes de correr el workflow.
- **`AddEventStore<PostgresStore>()`**: el `IEventStore` concreto que usan los
  command services para appendear/leer.
- **`AddPostgresCheckpointStore()`**: guarda el *checkpoint* de las
  suscripciones (hasta dónde leí) también en Postgres.

> **Ojo**: `initializeDatabase` corre al arrancar el host, no al registrarse.
> La demo espera explícitamente a que existan `stream_message` y `messages`
> antes de ejecutar el workflow (mirá `WaitForEventStoreObjectsAsync` en
> `02-ejemplo/src/CursoAgentes.App/Program.cs`).

## 3.3 Cómo se ve un evento guardado

```sql
SELECT global_position, stream_name, message_type, stream_position, created, json_data::text
FROM curso_eventstore.messages m
JOIN curso_eventstore.streams s ON s.stream_id = m.stream_id
ORDER BY global_position;
```

```
#1  workflow-run-run-abc123   V1.WorkflowRunCreated    {"runId": "run-abc123", "goal": "¿Por qué…?", "rootNodeId": "n-…"}
#2  workflow-node-n-…         V1.WorkflowNodeCreated   {"nodeId": "n-…", "depth": 0, "goal": "…"}
#3  workflow-node-n-…         V1.WorkflowNodePlanned   {"isLeaf": false, "childrenIds": ["n-…","n-…"], "rationale": "…"}
…
```

- `json_data` es el payload del evento (serializado con el nombre estable de
  `[EventType]`).
- `stream_position` es la posición **dentro del stream**; `global_position` es
  el orden **global** (el "reloj" del store).

## 3.4 Optimistic concurrency: la versión esperada

Cuando un comando appendea, pasa la **versión esperada** del stream:

| Estado esperado | Versión que pasa al append | Qué significa |
|---|---|---|
| `ExpectedState.New` | `NoStream` (-1) | "creo que este stream NO existe" |
| `ExpectedState.Existing` | la última revisión leída | "creo que está en la versión N" |
| `ExpectedState.Any` | `Any` (-2) | "no me importa" |

Si la versión real no coincide, el store **rechaza el append** (excepción de
concurrencia). Esto evita que dos procesos sobrescriban el mismo stream a la
vez — dos comandos concurrentes sobre el mismo run: uno gana, el otro falla y
se reintenta. En el ejemplo, el `InMemoryEventStore` de los tests implementa
este control a mano (`02-ejemplo/tests/CursoAgentes.Tests/Testing/InMemoryEventStore.cs`)
— leerlo te muestra exactamente qué contrato cumple el `PostgresStore` real.

## 3.5 Postgres local para el curso

`02-ejemplo/docker-compose.yml` levanta `postgres:16-alpine` con la base
`cursoagentesdb` (usuario `cursoagentes`, puerto 5432 expuesto para la app de
consola):

```bash
cd 02-ejemplo
docker compose up -d
```

---

## 📖 En el ejemplo

- Wiring: `02-ejemplo/src/CursoAgentes.Infrastructure/DependencyInjection.cs`
- Demo que imprime la auditoría (SQL real contra `curso_eventstore`):
  `02-ejemplo/src/CursoAgentes.App/Program.cs` → `PrintAuditTrailAsync`
- Event store en memoria (contrato de `IEventStore` a mano):
  `02-ejemplo/tests/CursoAgentes.Tests/Testing/InMemoryEventStore.cs`
