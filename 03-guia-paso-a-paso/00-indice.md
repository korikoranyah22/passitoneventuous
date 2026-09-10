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
| [02 · Solución y proyectos](02-solucion-y-proyectos.md) | 6 proyectos de producto + tests | `CursoAgentes.slnx`, `*.csproj` |
| [03 · Aggregate WorkflowRun](03-aggregate-workflowrun.md) | El aggregate del ciclo de vida del run | `src/CursoAgentes.Domain/Workflow/WorkflowRun.cs` |
| [04 · Aggregate WorkflowNode](04-aggregate-workflownode.md) | El aggregate de un nodo del árbol | `src/CursoAgentes.Domain/Workflow/WorkflowNode.cs` |
| [05 · Persistencia Eventuous + Postgres](05-persistencia-eventuous-postgres.md) | El wiring del event store | `src/CursoAgentes.Infrastructure/DependencyInjection.cs` |
| [06 · Gateway LLM](06-gateway-llm.md) | Puerto + LLM falso + adaptador real | `src/CursoAgentes.Engine/Llm/`, `src/CursoAgentes.Infrastructure/Llm/` |
| [07 · Motor recursivo](07-motor-recursivo.md) | El runner que orquesta el árbol | `src/CursoAgentes.Engine/Workflow/RecursiveWorkflowRunner.cs` |
| [08 · Proyecciones y read model](08-proyecciones-read-model.md) | La foto actual de los datos | `src/CursoAgentes.Infrastructure/Projections/` |
| [09 · App de demostración](09-app-demo.md) | La demo end-to-end por consola | `src/CursoAgentes.App/Program.cs` |
| [10 · Tests](10-tests.md) | 72 tests: 69 del núcleo + 3 del puente MiyuAgents → Eventuous | `tests/CursoAgentes.Tests/` |
| [11 · Estado del ejemplo](11-estado-del-ejemplo.md) | Qué anda, qué falta, por dónde seguir | — |
| [12 · Caso práctico híbrido](12-caso-practico-hibrido.md) | Comparar pipeline y grafo fijo al recolectar, criticar y actuar | MiyuAgents `routing-workflow/` + `fixed-node-workflow/` |
| [13 · API asíncrona](13-api-asincrona-y-resume.md) | Aceptar con `202`, ejecutar en background, consultar árbol/auditoría y reanudar | `src/CursoAgentes.Api/` |
| [14 · Auditoría defensiva electoral](14-auditoria-defensiva-electoral.md) | Comparar pipeline, nodos fijos y recursión acotada sobre telemetría sintética | MiyuAgents `election-defense/` |

## Progresión mental

1. **Pasos 3-4**: modelás el dominio (eventos + estado + guards). Sin
   infraestructura, testeable en memoria.
2. **Paso 5**: enchufás Eventuous + Postgres (persistencia real).
3. **Paso 6**: definís el puerto del LLM y sus adaptadores (fake + real).
4. **Paso 7**: el motor que une dominio + LLM con recursión.
5. **Pasos 8-9**: read model y demo.
6. **Pasos 10-11**: verificación de la base recursiva.
7. **Paso 12**: aplicación práctica comparando pipeline, nodos fijos y objetivos
   recursivos, con routing, crítica y decisión determinista; después,
   adaptación al árbol event-sourced.
8. **Paso 13**: llevar el motor durable a un host HTTP sin bloquear el request
   ni confundir la cola local con la fuente de verdad.
9. **Paso 14**: aplicar las tres formas de control a una misión defensiva de
   alto riesgo, manteniendo evidencia, política e intervención humana fuera del
   juicio autónomo del LLM.

> ⚠️ **Ruta crítica**: los pasos 3-4 (dominio) y 7 (motor) son el corazón. Si
> te queda poco tiempo, priorizalos; el resto es infraestructura que podés
> copiar.
