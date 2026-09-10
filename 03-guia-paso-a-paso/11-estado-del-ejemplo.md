# Paso 11 · Estado del ejemplo (qué anda, qué falta, por dónde seguir)

> Esta página es el **parte de obra** del ejemplo: verificaciones reales,
> pendientes honestos y caminos para seguir. Si algo no funciona, empezá por
> acá.

Cuando termines esta verificación, continuá con el
[Paso 12 · Caso práctico híbrido](12-caso-practico-hibrido.md), que incorpora
routing por capacidades, crítica independiente y una acción determinista, y
después con la [API asíncrona](13-api-asincrona-y-resume.md).

## ✅ Verificado en este entorno

| Verificación | Comando | Resultado |
|---|---|---|
| Compilación del núcleo y la API (net10.0) | `dotnet build tests/CursoAgentes.Tests/CursoAgentes.Tests.csproj` + build de la API | ✅ sin errores ni warnings |
| Tests de dominio (estado + guards) | `dotnet test --filter "FullyQualifiedName~WorkflowRunStateTests\|FullyQualifiedName~WorkflowRunCommandServiceTests\|FullyQualifiedName~WorkflowNodeStateTests\|FullyQualifiedName~WorkflowNodeCommandServiceTests"` | ✅ 29/29 |
| Tests del motor recursivo | `dotnet test --filter "FullyQualifiedName~RecursiveWorkflowRunnerTests"` | ✅ 7/7 |
| Tests del gateway real (HTTP stub) | `dotnet test --filter "FullyQualifiedName~OpenAiCompatibleGatewayTests"` | ✅ 2/2 |
| Tests del caso de incidentes | `dotnet test --filter "FullyQualifiedName~IncidentInvestigation"` | ✅ 17/17 |
| Tests del puerto HTTP de acciones | `dotnet test --filter "FullyQualifiedName~HttpIncidentActionPort"` | ✅ 2/2 |
| Tests del puente MiyuAgents → Eventuous | `dotnet test --filter "FullyQualifiedName~MiyuEventuousBridge"` | ✅ 3/3; pipeline y nodos convergen y sólo Eventuous ejecuta efectos |
| Tests del host API | `dotnet test --filter "FullyQualifiedName~WorkflowApiHost"` | ✅ cola/worker/árbol/idempotencia/recovery/leases, 12/12 |
| **Total del checkout actual** | `dotnet test` | ✅ **72/72**: 69 del núcleo + 3 del puente Miyu |
| Resume recursivo contra PostgreSQL | `--workflow-start` y luego `--workflow-resume` en otro proceso | ✅ 2 eventos al preparar; 15 nodos y 47 eventos al completar; repetir resume conserva 47 |
| API asíncrona contra PostgreSQL | `POST` diferido → status/audit → `POST /resume` | ✅ `202`; 2 eventos y raíz `Pending` antes; árbol `Completed` y 48 eventos/16 streams después |
| Idempotencia + recuperación HTTP contra PostgreSQL | `Idempotency-Key` → replay/conflicto; kill después del `202` → restart | ✅ mismo run, conflicto `409`; scanner completó 2047 nodos/6144 eventos con una sola solicitud |
| Lease multi-instancia contra PostgreSQL | dos APIs y un mismo candidato | ✅ un solo dueño ejecutó; 127 nodos, 384 eventos, 128 streams; lease renovado y liberado |
| Tres modos de incidentes contra PostgreSQL | `--incident`, `--incident-pipeline`, `--incident-nodes` | ✅ seis eventos por stream, read model `Completed` y checkpoints independientes al día |
| Parking HTTP contra PostgreSQL | provider `Http`, endpoint inaccesible, un intento | ✅ `IncidentActionParked(http-timeout)`, read model `ActionParked`, sin `external_id` |
| Recuperación manual contra PostgreSQL | `--incident-retry inv-0e2f0c96` con `RequestId` estable | ✅ 8 eventos: conserva parking, agrega solicitud y confirmación; repetir el comando no agrega eventos |
| **Corrida end-to-end contra Postgres real** (16.2, ver abajo) | `dotnet run --project src/CursoAgentes.App` | ✅ árbol de **15 nodos** (1→2→4→8 hojas), run `Completed`, **47 eventos** auditables, read model completo |

Los 17 tests de incidentes incluyen tres ventanas de recuperación: caída
después de `IncidentActionDecided` y falla transitoria del puerto externo. En
ambas quedan cinco eventos; el retry completa el sexto sin duplicar el efecto.
La tercera estaciona un fallo permanente, registra una solicitud manual
auditada y completa el octavo evento. También prueba el mismo `RequestId` antes
y después de completar.

La prueba PostgreSQL también dejó deliberadamente una investigación en cinco
eventos al detenerse una suscripción y reinició el host. En el siguiente
arranque `IncidentActions` recuperó la decisión y agregó `IncidentOpened`.
Una consulta final mostró cuatro streams de incidente con seis eventos y los
checkpoints `IncidentActions`, `IncidentReadModel` y `WorkflowReadModel` en la
misma posición global final, cada uno conservando su identidad independiente.

También se ejecutó el adapter HTTP contra un endpoint inaccesible. La reacción
persistió `IncidentActionParked` como sexto evento y el read model guardó
`http-timeout`, `transient=true`, `attempts=1`, sin `external_id`. Una corrida
posterior con los defaults confirmó que pipeline y nodos siguen completando
normalmente. La guarda de reentrega reconstruye el stream antes del I/O: un
estado `Completed` o `ActionParked` no vuelve a tocar el puerto.

Finalmente se recuperó el stream estacionado `inv-0e2f0c96` con el provider
en memoria restaurado. La historia conservó `IncidentActionParked` en posición
5, agregó `IncidentActionRetryRequested` en 6 e `IncidentOpened` en 7. El read
model quedó `Completed`, con `manual_retry_count=1`, actor y motivo, y sin falla
vigente. Repetir literalmente `manual-retry-001` mantuvo el stream en 8 eventos.

La reanudación recursiva también se verificó contra PostgreSQL. `StartAsync`
dejó `run-6235dd1e` en `Running` con sólo `WorkflowRunCreated` y una raíz
`Pending`. Un segundo proceso ejecutó `ResumeAsync`, construyó 15 nodos y cerró
el run con 47 eventos. Un tercer proceso restauró el árbol `Completed` con
cero llamadas a agentes y la consulta final siguió mostrando 47 eventos.

La aceptación HTTP idempotente se verificó con una clave estable: el primer
`POST` creó el run, el replay devolvió el mismo `runId` sin agregar eventos y
usar la clave con otro `goal` devolvió `409 Conflict`. Al solicitar ejecución,
`WorkflowRunExecutionRequested` quedó persistido antes de la entrega local; el
workflow normal terminó con 48 eventos (los 47 del árbol más esa solicitud).

También se forzó una caída inmediatamente después del `202 Accepted`, antes de
que el trabajo pudiera depender de la cola en memoria. Tras reiniciar el host,
el recovery scanner confirmó el candidato contra su stream y lo encoló sin
intervención del cliente. Con `MaxDepth=10` completó 2047 nodos, 6144 eventos y
2048 streams, con exactamente un `WorkflowRunExecutionRequested`.

La coordinación multi-instancia se probó levantando dos APIs contra el mismo
PostgreSQL. Ambas podían descubrir el run, pero el `INSERT … ON CONFLICT`
condicional entregó el lease a una sola. El dueño lo renovó durante un árbol de
127 nodos; la otra instancia observó el lease activo y no generó churn. El
resultado tuvo exactamente 384 eventos en 128 streams y el lease quedó
liberado. Los tests aislados agregan takeover tras vencimiento con token mayor
y cancelación local cuando una renovación deja de ser válida.

### Qué mostró la corrida end-to-end (verificada)

Con `Llm:Provider = "Fake"` (default, sin API key) y Postgres real:

1. **Event store inicializado** por Eventuous: schema `curso_eventstore` con el
   tipo compuesto `stream_message` y la tabla `messages`.
2. **Workflow recursivo completo**: la raíz se divide en 2 sub-objetivos, cada
   uno en 2 más, hasta `Workflow:MaxDepth = 3` → **15 nodos** (8 hojas). Cada
   nodo es un stream `workflow-node-*` propio.
3. **Auditoría en el event store**: por run quedan 47 eventos
   (`WorkflowRunCreated` + 15 nodos × `Created→Planned→Completed` +
   `WorkflowRunCompleted`) con payloads JSON legibles y `global_position`
   correlacionada.
4. **Read model**: la suscripción `PostgresAllStreamSubscription` alimentó la
   proyección → tablas `curso_readmodel` con el run `Completed` y los 15 nodos
   con su estado.

El wiring de Eventuous + Postgres (`AddEventuousPostgres` +
`AddEventStore<PostgresStore>` + `AddPostgresCheckpointStore` +
`PostgresAllStreamSubscription`) queda así **verificado contra Postgres real**,
no solo compilado.

### Cómo reproducir la corrida (dos caminos)

**Camino A — Docker (el canónico del curso):**

```bash
cd 02-ejemplo
docker compose up -d        # levanta postgres:16-alpine
dotnet run --project src/CursoAgentes.App
```

**Camino B — binarios embebidos, sin Docker** (el que usamos en el sandbox):
si no tenés Docker pero sí red, podés bajar un Postgres portable y correrlo
como usuario normal (sin root):

```bash
mkdir -p /tmp/pg && cd /tmp/pg
curl -sO https://repo1.maven.org/maven2/io/zonky/test/postgres/embedded-postgres-binaries-linux-amd64/16.2.0/embedded-postgres-binaries-linux-amd64-16.2.0.jar
python3 -m zipfile -e embedded-postgres-binaries-linux-amd64-16.2.0.jar .
tar xf postgres-linux-x86_64.txz
./bin/initdb -D /tmp/pg/data -U cursoagentes --auth=trust -E UTF8
./bin/pg_ctl -D /tmp/pg/data -o "-p 5432" -l /tmp/pg/postgres.log start
# crear la base (sin psql a mano: un mini helper C# con Npgsql sirve)
#   CREATE DATABASE cursoagentesdb;
cd 02-ejemplo && dotnet run --project src/CursoAgentes.App
```

Nota: el bundle trae solo `initdb`/`pg_ctl`/`postgres` (no `psql`); para crear
la base usá `dotnet run` sobre un proyecto de 3 líneas con `Npgsql` o el
`createdb` de tu instalación local.

## ⚠️ NO verificado en este entorno (y por qué)

| Pendiente | Motivo | Cómo verificarlo vos |
|---|---|---|
| Demo con **LLM real** | Requiere un proveedor (Ollama/DeepSeek/…) | Cambiar `Llm:Provider` a `"OpenAI"` y ajustar `BaseUrl`/`ApiKey`/`Model` |
| Puerto de acciones contra una API HTTP real | El contrato y sus errores se verificaron con HTTP stub, no con un proveedor desplegado | Cambiar `IncidentAction:Provider` a `"Http"` y configurar `BaseUrl` |
| Llamada en vivo desde el caso híbrido | Los adapters OpenAI-compatible, Anthropic y Gemini se verificaron con HTTP stubs; no se usaron credenciales reales | Configurar `examples/real-providers` y conservar los mismos perfiles y contratos |

## 🪤 Trampas reales encontradas al verificar el e2e (y cómo se arreglaron)

Estas cinco las descubrimos **corriendo** el ejemplo, no leyendo documentación.
Quedan documentadas como lecciones:

1. **El DI y `IEnumerable<T>` en el constructor del fake.** La primera corrida
   produjo un árbol de **1 solo nodo** (todo caía a "hoja por seguridad") porque
   el `FakeLlmGateway` quedaba en modo *scripted con cola vacía*. La causa: su
   constructor recibe `IEnumerable<string>? script = null` y el DI resuelve
   `IEnumerable<string>` como la lista de **todos los `string` registrados**
   → colección vacía → `script is not null` era `true` → siempre fallback
   genérico. Fix: registrarlo a mano con factory y **sin script** (modo
   "smart") — ver `DependencyInjection.cs`. Lección general: un parámetro
   opcional `IEnumerable<T>` + registro por tipo es una bomba de tiempo; o lo
   construís vos con `AddSingleton<T>(sp => …)` o usás un tipo propio (no una
   colección de BCL) para la config.
2. **El fake "smart" que arrastraba el prompt como objetivo.** Los sub-objetivos
   salían como `Aspecto 1 de «Objetivo: ¿Por qué…\nProfundidad actual: 0…»` y el
   parseo de profundidad leía la línea anidada del texto (el fake "veía" depth 0
   siempre). Fix: `ExtractGoal()` saca el objetivo limpio del mensaje de
   usuario (`Objetivo: {goal}` / `Objetivo original: {goal}`) y `ParseDepth`
   corre sobre el mensaje crudo — ver `FakeLlmGateway.cs`. Lección general:
   cuando simulás un LLM, simulá también el **contrato del mensaje**, no solo la
   respuesta; si no, el fake valida un camino que el real no recorre.
3. **Npgsql desalineado con Eventuous.Postgresql.** Forzar `Npgsql 10.0.2`
   sobre el `10.0.1` usado por Eventuous rompió el mapeo del tipo compuesto
   `NewPersistedEvent[]`. El append fallaba antes de guardar el primer evento.
   Fix: mantener ambas versiones alineadas en el `.csproj`. Lección general:
   los drivers que registran tipos binarios son parte del contrato del adapter,
   no una dependencia que siempre pueda actualizarse de forma aislada.
4. **Windows Event Log como efecto accidental del logging.** El host agregaba
   ese provider por defecto; sin permisos elevados, un warning de la
   suscripción lanzaba una excepción y frenaba el checkpoint. Fix: limpiar los
   providers y registrar consola explícitamente. Lección general: tampoco el
   logging de una reacción durable debe depender de un efecto privilegiado.
5. **Un plan con IDs pero sin objetivos no alcanza para reanudar.** El padre
   persistía `ChildrenIds` y después creaba cada stream hijo. Un crash entre
   ambas operaciones dejaba un ID cuyo objetivo sólo vivía en memoria. Fix:
   `WorkflowNodePlanned` persiste también `ChildGoals`; el resume puede crear
   cualquier hijo faltante desde ese checkpoint. Lección general: un
   checkpoint debe contener todos los datos necesarios para la fase siguiente.

Además: los warnings `Static context conversion not found … using reflections`
son esperables e inofensivos. La app filtra esa categoría por debajo de
`Error`; si quitás el filtro, Eventuous usa reflexión y continúa normalmente.

## 🗺️ Por dónde seguir (ideas ordenadas por dificultad)

### Fácil (config o pocas líneas)

1. **Probar con un LLM real**: configurá Ollama local
   (`BaseUrl: http://localhost:11434/v1`, `Model: llama3.2`) o DeepSeek y corré
   la demo. Compará la respuesta del fake vs. la real.
2. **Jugar con el manifiesto**: cambiá `Workflow:MaxDepth` (¿qué pasa con 1? ¿y
   con 5?), `MaxChildrenPerNode`, y los prompts. Re-corré la demo y los tests.

### Media (una feature nueva)

3. **Paralelizar la ejecución de hijos**: hoy los hijos corren secuencialmente
   (`for` + `await`). Con `Task.WhenAll` corren en paralelo. Atención: el
   orden de los eventos deja de ser determinista — ¿qué invariantes se
   mantienen igual? (Hint: el aggregate por nodo ya las protege.)
4. **Agregar el modelo de costo**: cada `LlmResponse` trae `InputTokens` /
   `OutputTokens`. Agregá un evento `WorkflowNodeLlmCallRecorded` (o un campo
   en el read model) y mostrá el costo total por run en la demo.
5. **Reintentos con backoff**: el gateway real no reintenta. Agregá un
   `RetryingLlmGateway` que envuelva a otro gateway y reintente N veces con
   backoff (patrón decorator — el motor no cambia).

### Difícil (la joya)

6. **Extender el resume a fallos explícitos**: el resume post-crash acepta
   `Running` y reconstruye `Completed`, pero no reabre `Failed`. Diseñá un
   comando auditado con request id, actor y motivo, similar a la recuperación
   manual de incidentes, y definí qué nodos fallidos pueden volver a ejecutarse.
7. **Agregar fencing estricto al lease**: la API ya coordina varias instancias,
   renueva y cancela al perder propiedad. Propagá el token hasta cada escritura
   para que PostgreSQL rechace también a un proceso pausado que despierte tarde;
   ver el ejercicio 7.

## 🧪 Checklist final del curso

- [ ] Leí las 8 lecciones de teoría
- [ ] Corrí `dotnet test` → 72 verdes (69 core + 3 del puente Miyu)
- [ ] `docker compose up -d` + demo end-to-end → auditoría visible
- [ ] Probé con un LLM real (o entendí cómo hacerlo)
- [ ] Hice al menos 2 ejercicios de `04-ejercicios/`

---

**Siguiente**: [Paso 12 · Caso práctico híbrido](12-caso-practico-hibrido.md) y
luego [Paso 13 · API asíncrona](13-api-asincrona-y-resume.md).
