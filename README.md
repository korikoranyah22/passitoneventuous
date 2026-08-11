# Event Sourcing con Eventuous + PostgreSQL para Agentes con LLM

**Una clase completa, en español, con teoría + ejemplo funcional + ejercicios.**

> Construimos un **workflow de agentes con nodos recursivos**: un objetivo se
> divide en sub-objetivos, cada sub-objetivo se ejecuta con llamadas a un LLM,
> y las respuestas se vuelven a integrar — mientras **cada nodo del árbol queda
> persistido como un aggregate event-sourced en PostgreSQL**.

---

## 📚 Cómo está organizada la carpeta

```
clase-eventuous-agentes-llm/
├── README.md                  ← estás acá: portada, syllabus, estado del ejemplo
├── 01-teoria/                 ← 7 lecciones conceptuales (event sourcing, Eventuous,
│                                Postgres como event store, workflows, nodos recursivos,
│                                LLMs, y por qué event sourcing para agentes)
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
| **Ejemplo** | Solución completa verificada | `02-ejemplo/` |
| **Paso a paso** | 11 pasos para reconstruirlo | `03-guia-paso-a-paso/` |
| **Ejercicios** | 8 ejercicios + soluciones | `04-ejercicios/` |

---

## ⚡ Quickstart del ejemplo (5 minutos)

```bash
# 1. Levantar PostgreSQL (16) — es el único requisito externo
cd 02-ejemplo
docker compose up -d

# 2. Correr la demo (usa el LLM FALSO por default: no gasta tokens)
dotnet run --project src/CursoAgentes.App

# 3. Verificar los tests (29 tests, no necesitan Postgres)
dotnet test
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
| `dotnet build` (solución completa, net10.0) | ✅ compila sin errores ni warnings |
| `dotnet test` (29 tests) | ✅ 29/29 verdes |
| Tests de aggregates (transiciones + guards) | ✅ cubren todos los guards, incluido el caso "comando sobre aggregate inexistente" |
| Tests del motor recursivo (con event store en memoria + LLM falso) | ✅ árbol, persistencia, terminación por MaxDepth, run fallido |
| Tests del gateway real (HTTP stub) | ✅ parseo de `/chat/completions` |
| Corrida end-to-end contra **Postgres real** (16.2) | ✅ **ejecutada y verificada**: árbol de **15 nodos** (recursión 1→2→4→8 hasta `MaxDepth=3`), run `Completed`, **47 eventos** en `curso_eventstore.messages` y read model completo en `curso_readmodel` — ver `03-guia-paso-a-paso/11-estado-del-ejemplo.md` |
| `dotnet run` (demo de consola) | ✅ corre contra Postgres real (verificado con binarios embebidos en el sandbox; con Docker: `docker compose up -d`) |

> **Transparencia**: la demo se verificó end-to-end contra Postgres real con el
> LLM falso (default). Lo único no probado en vivo es un LLM de verdad
> (requiere proveedor/API key). Las dos trampas que aparecieron al correr el
> e2e quedaron documentadas como lecciones en el paso 11 y en el glosario.

---

## 🧠 En una frase

> **Un agente es un workflow; un workflow es una máquina de estados; una
> máquina de estados con eventos inmutables en Postgres es un aggregate
> Eventuous.** Y cuando el LLM falla (va a pasar), la historia de lo que ya se
> hizo sigue ahí, esperando que la reanudes.
