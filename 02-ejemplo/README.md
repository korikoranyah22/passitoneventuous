# 02-ejemplo · El ejemplo funcional

Workflow de agentes con **nodos recursivos**, event-sourced con **Eventuous +
PostgreSQL**, con llamadas a **LLM** detrás de un puerto intercambiable.

> Esta carpeta es parte de la clase `clase-eventuous-agentes-llm/`. La teoría
> está en `../01-teoria/` y la guía para reconstruir esto paso a paso en
> `../03-guia-paso-a-paso/`.

## Quickstart

```bash
# 1. Postgres (16) — único requisito externo
docker compose up -d

# 2. Tests (29, no necesitan Postgres)
dotnet test

# 3. Demo end-to-end (usa el LLM FALSO por default: no gasta tokens)
dotnet run --project src/CursoAgentes.App

# 4. Con un LLM real: editá appsettings.json → Llm.Provider = "OpenAI"
#    (BaseUrl + ApiKey + Model compatibles con /chat/completions)
```

## Proyectos

```
src/
├── CursoAgentes.Domain        eventos, estado, comandos, guards (2 aggregates)
├── CursoAgentes.Engine        agentes, contexto, LLM fake, motor recursivo
├── CursoAgentes.Infrastructure event store (Eventuous+Postgres), gateway real, read model
└── CursoAgentes.App           demo de consola end-to-end
tests/
└── CursoAgentes.Tests         29 tests (sin Postgres ni LLM real)
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

## Estado de verificación

| Chequeo | Estado |
|---|---|
| `dotnet build` (net10.0) | ✅ sin errores ni warnings |
| `dotnet test` | ✅ 29/29 |
| End-to-end con Postgres | ⚠️ no corrido en el sandbox (sin Docker) — ver `../03-guia-paso-a-paso/11-estado-del-ejemplo.md` |
