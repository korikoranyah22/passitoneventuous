# Event Sourcing con Eventuous + PostgreSQL para Agentes con LLM

**Una clase completa, en español, con teoría + ejemplo funcional + ejercicios.**

> Construimos un **workflow de agentes con nodos recursivos** y luego lo
> contrastamos con un caso híbrido lineal de incidentes. En ambos, las decisiones
> importantes quedan persistidas como aggregates event-sourced en PostgreSQL;
> los efectos externos permanecen fuera del dominio y son idempotentes.

---

## 📚 Cómo está organizada la carpeta

```
clase-eventuous-agentes-llm/
├── README.md                  ← estás acá: portada, syllabus, estado del ejemplo
├── 01-teoria/                 ← 8 lecciones conceptuales (event sourcing, Eventuous,
│                                Postgres como event store, workflows, nodos recursivos,
│                                LLMs, routing y workflows híbridos)
├── 02-ejemplo/                ← el código COMPLETO y funcional (solución .NET)
├── 03-guia-paso-a-paso/       ← la guía: construí el ejemplo vos mismo, paso a paso
├── 04-ejercicios/             ← ejercicios con soluciones orientativas
└── 05-apendices/              ← glosario y referencias
```

**Ruta de estudio sugerida:**

1. Leé las lecciones de `01-teoria/` en orden (son la base conceptual).
2. Corré el ejemplo terminado (`02-ejemplo/`) para verlo funcionar.
3. Seguí `03-guia-paso-a-paso/` para reconstruirlo vos mismo (cada paso mapea a
   archivos reales del ejemplo).
4. Hacé los ejercicios de `04-ejercicios/`.

---

## 🎯 Objetivos de aprendizaje

Al terminar esta clase vas a poder:

1. **Explicar qué es event sourcing** y por qué un workflow de agentes es un
   caso de uso natural: procesos largos, caros, fallibles y que necesitan
   auditoría y reanudación.
2. **Modelar un aggregate con Eventuous** (eventos, estado, comandos, guards)
   y persistirlo en **PostgreSQL**.
3. **Diseñar un workflow de agentes** con **nodos recursivos** (planificar →
   dividir → resolver → sintetizar) usando un **LLM como pieza intercambiable**
   detrás de un puerto (`ILlmGateway`).
4. **Separar responsabilidades**: invariantes de un aggregate → dominio;
   invariantes entre aggregates → orquestador; efectos colaterales (LLM, I/O)
   → fuera del aggregate.
5. **Leer el event store como auditoría** y mantener un **read model** con
   proyecciones (CQRS en su forma más pura).
6. **Separar autonomía y control**: routing por capacidades, análisis y crítica
   no deterministas, validación y acciones deterministas.

---

## 🗓️ Syllabus

| Módulo | Lección | Archivo |
|---|---|---|
| **Teoría** | 1. Event sourcing: la historia completa | `01-teoria/leccion-01-event-sourcing.md` |
| | 2. Eventuous: aggregates, comandos y guards | `01-teoria/leccion-02-eventuous.md` |
| | 3. PostgreSQL como event store | `01-teoria/leccion-03-postgresql-como-event-store.md` |
| | 4. Agentes como workflows | `01-teoria/leccion-04-workflows-de-agentes.md` |
| | 5. Nodos recursivos | `01-teoria/leccion-05-nodos-recursivos.md` |
| | 6. LLMs en el flujo (puertos y adaptadores) | `01-teoria/leccion-06-llm-en-el-flujo.md` |
| | 7. Por qué event sourcing para agentes | `01-teoria/leccion-07-por-que-event-sourcing-para-agentes.md` |
| | 8. Workflows híbridos: routing, crítica y decisión determinista | `01-teoria/leccion-08-workflows-hibridos-y-routing.md` |
| **Ejemplo** | Solución completa verificada | `02-ejemplo/` |
| **Paso a paso** | 14 pasos: base recursiva + caso híbrido + host HTTP + auditoría defensiva | `03-guia-paso-a-paso/` |
| **Ejercicios** | 13 ejercicios + soluciones | `04-ejercicios/` |

---

## ⚡ Quickstart del ejemplo (5 minutos)

```bash
# 1. Levantar PostgreSQL (16) — es el único requisito externo
cd 02-ejemplo
docker compose up -d

# 2. Correr la demo (usa el LLM FALSO por default: no gasta tokens)
dotnet run --project src/CursoAgentes.App

# Separar persistencia y ejecución en dos procesos
dotnet run --project src/CursoAgentes.App -- \
  --workflow-start "¿Cómo se reanuda un árbol?"
# copiá el runId impreso:
dotnet run --project src/CursoAgentes.App -- --workflow-resume run-1234abcd

# API asíncrona: persiste, devuelve 202 y ejecuta fuera del request
dotnet run --project src/CursoAgentes.Api --urls http://localhost:5090

# 3. Verificar los 72 tests (69 core + 3 del puente Miyu; no necesitan Postgres)
dotnet test

# 4. Correr el caso práctico event-sourced de incidentes
dotnet run --project src/CursoAgentes.App -- --incident
dotnet run --project src/CursoAgentes.App -- --incident-pipeline
dotnet run --project src/CursoAgentes.App -- --incident-nodes
```

La demo imprime: el **árbol de nodos** del workflow, la **bitácora**, los
**eventos crudos** del event store (¡la auditoría!) y el **read model**.

### Usar un LLM real

Editá `src/CursoAgentes.App/appsettings.json`:

```jsonc
"Llm": {
  "Provider": "OpenAI",                    // "Fake" (default) | "OpenAI"
  "BaseUrl": "http://localhost:11434/v1",  // cualquier API compatible con OpenAI:
                                           //   Ollama local, DeepSeek, Groq, vLLM…
  "ApiKey": "",                            // vacío para Ollama local
  "Model": "llama3.2"
}
```

---

## ✅ Estado del ejemplo (verificado)

| Qué | Estado |
|---|---|
| Build core: Domain + Engine + Infrastructure + API + Tests (net10.0) | ✅ compila sin errores ni warnings |
| `dotnet test` | ✅ 72/72 verdes: 69 core + 3 del puente MiyuAgents → Eventuous |
| `dotnet build` de la solución completa en el checkout actual | ✅ incluye `incident-response`, `routing-workflow` y `fixed-node-workflow`; 0 errores y 0 warnings |
| Tests de aggregates (transiciones + guards) | ✅ cubren todos los guards, incluido el caso "comando sobre aggregate inexistente" |
| Tests de investigación de incidentes | ✅ recuperación, retry/backoff, parking, HTTP idempotente y reentrega segura |
| Tests del puente MiyuAgents → Eventuous | ✅ pipeline y nodos producen el mismo input y sólo Eventuous ejecuta el efecto |
| Ejemplo de objetivos recursivos MiyuAgents | ✅ tres etapas recursivas acotadas; evidencia 3 vueltas, borrador 3 y crítica 2 |
| Tres modos de incidentes contra PostgreSQL | ✅ Eventuous directo, pipeline y nodos completan seis eventos mediante la reacción durable |
| Parking HTTP contra PostgreSQL | ✅ timeout clasificado → `IncidentActionParked`, sin recibo ni efecto confirmado |
| Recuperación manual contra PostgreSQL | ✅ `ActionParked → ActionRetryRequested → IncidentOpened`; repetir el `RequestId` conserva 8 eventos |
| Tests del motor recursivo (con event store en memoria + LLM falso) | ✅ árbol, persistencia, terminación, cancelación y resume selectivo |
| Resume recursivo contra PostgreSQL | ✅ start-only deja 2 eventos; otro proceso completa 15 nodos/47 eventos; repetir resume conserva 47 |
| API asíncrona contra PostgreSQL | ✅ alta diferida deja 2 eventos; dispatch durable agrega `ExecutionRequested`; árbol termina `Completed` con 48 eventos/16 streams |
| Idempotencia y recovery HTTP | ✅ misma clave → mismo run; goal conflictivo → `409`; crash/restart recuperó automáticamente 2047 nodos, 6144 eventos y una sola solicitud |
| Lease multi-instancia | ✅ dos APIs compartieron PostgreSQL; sólo una ejecutó, 127 nodos/384 eventos exactos y lease liberado |
| Tests del gateway real (HTTP stub) | ✅ parseo de `/chat/completions` |
| Gateways reales de MiyuAgents | ✅ OpenAI/Azure OpenAI y compatibles, Anthropic y Gemini; complete/stream/tools/usage y embeddings aplicables verificados con HTTP stubs |
| Auditoría defensiva electoral | ✅ pipeline normal, grafo fijo y recursión acotada convergen en política determinista; 6 tests offline y sin mutación automática |
| Corrida end-to-end contra **Postgres real** (16.2) | ✅ **ejecutada y verificada**: árbol de **15 nodos** (recursión 1→2→4→8 hasta `MaxDepth=3`), run `Completed`, **47 eventos** en `curso_eventstore.messages` y read model completo en `curso_readmodel` — ver `03-guia-paso-a-paso/11-estado-del-ejemplo.md` |
| `dotnet run` (demo de consola) | ✅ corre contra Postgres real (verificado con binarios embebidos en el sandbox; con Docker: `docker compose up -d`) |

> **Transparencia**: la demo se verificó end-to-end contra Postgres real con el
> LLM falso y action port en memoria (defaults). No se probaron en vivo un LLM
> de verdad ni un proveedor HTTP externo de acciones. Las cinco trampas que aparecieron al correr el
> e2e quedaron documentadas como lecciones en el paso 11 y en el glosario.

---

## 🧠 En una frase

> **Un agente es un workflow; un workflow es una máquina de estados; una
> máquina de estados con eventos inmutables en Postgres es un aggregate
> Eventuous.** Y cuando el LLM falla (va a pasar), la historia de lo que ya se
> hizo sigue ahí, esperando que la reanudes.
