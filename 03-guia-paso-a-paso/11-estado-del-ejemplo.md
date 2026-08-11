# Paso 11 · Estado del ejemplo (qué anda, qué falta, por dónde seguir)

> Esta página es el **parte de obra** del ejemplo: verificaciones reales,
> pendientes honestos y caminos para seguir. Si algo no funciona, empezá por
> acá.

## ✅ Verificado en este entorno

| Verificación | Comando | Resultado |
|---|---|---|
| Compilación de la solución completa (net10.0) | `dotnet build` | ✅ sin errores ni warnings |
| Tests de dominio (estado + guards) | `dotnet test --filter "FullyQualifiedName~WorkflowRunTests \| FullyQualifiedName~WorkflowNodeTests"` | ✅ 24/24 |
| Tests del motor recursivo | `dotnet test --filter "FullyQualifiedName~RecursiveWorkflowRunnerTests"` | ✅ 3/3 |
| Tests del gateway real (HTTP stub) | `dotnet test --filter "FullyQualifiedName~OpenAiCompatibleGatewayTests"` | ✅ 2/2 |
| **Total** | `dotnet test` | ✅ **29/29** |
| **Corrida end-to-end contra Postgres real** (16.2, ver abajo) | `dotnet run --project src/CursoAgentes.App` | ✅ árbol de **15 nodos** (1→2→4→8 hojas), run `Completed`, **47 eventos** auditables, read model completo |

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
| Reanudación tras crash (releer el árbol desde el store) | Es un ejercicio propuesto (ver abajo), no una feature del ejemplo | Ver ejercicio 6 |

## 🪤 Trampas reales encontradas al verificar el e2e (y cómo se arreglaron)

Estas dos las descubrimos **corriendo** el ejemplo, no leyendo documentación.
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

Además: los warnings `Static context conversion not found … using reflections`
de la suscripción son **esperables e inofensivos** (Eventuous cae al fallback
por reflexión para mapear el contexto). No son errores.

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

6. **Reanudación post-crash**: el ejemplo persiste todo, pero no "retoma" un
   run a mitad de camino. Construí un `ResumeWorkflowRunner` que: lea los
   streams del run y de los nodos del store, reconstruya el árbol con lo ya
   completado, y continúe solo los nodos pendientes. Para esto te sirve el
   `InMemoryEventStore` como referencia de lectura, o el SQL de auditoría del
   paso 9.
7. **HTTP API**: exponé el workflow vía ASP.NET Core (`POST /api/runs` con el
   objetivo → devuelve runId; `GET /api/runs/{id}` lee el read model). El
   patrón de controller está en el repo real (lección de apéndice).

## 🧪 Checklist final del curso

- [ ] Leí las 7 lecciones de teoría
- [ ] Corrí `dotnet test` → 29 verdes
- [ ] `docker compose up -d` + demo end-to-end → auditoría visible
- [ ] Probé con un LLM real (o entendí cómo hacerlo)
- [ ] Hice al menos 2 ejercicios de `04-ejercicios/`

---

**Fin de la guía.** 🎉
