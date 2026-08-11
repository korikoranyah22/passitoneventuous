# Guía paso a paso — Índice

Esta guía te lleva de cero a un **workflow de agentes con nodos recursivos,
event-sourced en PostgreSQL con Eventuous y LLMs**, construyéndolo vos mismo.

**Forma de uso**: cada paso tiene un *objetivo*, el *concepto* detrás, los
*archivos* que vas a tocar (todos existen ya en `02-ejemplo/` — podés seguirlos
como referencia o escribirlos de cero), y un *probalo*.

> 💡 Si querés verlo andar antes de construirlo: `02-ejemplo/README.md` tiene el
> quickstart. Esta guía es para entenderlo pieza por pieza.

## Mapa de pasos

| Paso | Qué construís | Archivos clave |
|---|---|---|
| [01 · Entorno](01-entorno.md) | Requisitos y comandos base | — |
| [02 · Solución y proyectos](02-solucion-y-proyectos.md) | La solución .NET con 5 proyectos | `CursoAgentes.slnx`, `*.csproj` |
| [03 · Aggregate WorkflowRun](03-aggregate-workflowrun.md) | El aggregate del ciclo de vida del run | `src/CursoAgentes.Domain/Workflow/WorkflowRun.cs` |
| [04 · Aggregate WorkflowNode](04-aggregate-workflownode.md) | El aggregate de un nodo del árbol | `src/CursoAgentes.Domain/Workflow/WorkflowNode.cs` |
| [05 · Persistencia Eventuous + Postgres](05-persistencia-eventuous-postgres.md) | El wiring del event store | `src/CursoAgentes.Infrastructure/DependencyInjection.cs` |
| [06 · Gateway LLM](06-gateway-llm.md) | Puerto + LLM falso + adaptador real | `src/CursoAgentes.Engine/Llm/`, `src/CursoAgentes.Infrastructure/Llm/` |
| [07 · Motor recursivo](07-motor-recursivo.md) | El runner que orquesta el árbol | `src/CursoAgentes.Engine/Workflow/RecursiveWorkflowRunner.cs` |
| [08 · Proyecciones y read model](08-proyecciones-read-model.md) | La foto actual de los datos | `src/CursoAgentes.Infrastructure/Projections/` |
| [09 · App de demostración](09-app-demo.md) | La demo end-to-end por consola | `src/CursoAgentes.App/Program.cs` |
| [10 · Tests](10-tests.md) | 29 tests: estado, guards, motor, gateway | `tests/CursoAgentes.Tests/` |
| [11 · Estado del ejemplo](11-estado-del-ejemplo.md) | Qué anda, qué falta, por dónde seguir | — |

## Progresión mental

1. **Pasos 3-4**: modelás el dominio (eventos + estado + guards). Sin
   infraestructura, testeable en memoria.
2. **Paso 5**: enchufás Eventuous + Postgres (persistencia real).
3. **Paso 6**: definís el puerto del LLM y sus adaptadores (fake + real).
4. **Paso 7**: el motor que une dominio + LLM con recursión.
5. **Pasos 8-9**: read model y demo.
6. **Pasos 10-11**: verificación y cierre.

> ⚠️ **Ruta crítica**: los pasos 3-4 (dominio) y 7 (motor) son el corazón. Si
> te queda poco tiempo, priorizalos; el resto es infraestructura que podés
> copiar.
