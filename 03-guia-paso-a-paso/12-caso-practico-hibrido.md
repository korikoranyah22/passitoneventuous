# Paso 12 · Caso práctico: investigar y abrir un incidente

## Objetivo

Construir un workflow que combine:

1. recolección determinista de una señal;
2. análisis y crítica mediante LLM;
3. routing por capacidades;
4. retry y fallback controlados;
5. una decisión final determinista;
6. eventos auditables e idempotencia para el efecto externo.

No reemplazamos el ejemplo recursivo anterior. Lo usamos como motor de
investigación cuando el incidente requiere descubrir sub-preguntas.

## Parte A · Corré primero el pipeline de referencia

Desde la raíz del curso (`clase-eventuous-agentes-llm/`):

```bash
dotnet run --project ../angelnairav2_public/Packages/MiyuAgents/examples/routing-workflow/RoutingWorkflowExample.csproj
```

La salida esperada termina así:

```text
Pipeline aborted: False
- collect-status: raw status snapshot collected
- normalize-status: status signal normalized
- draft-incident: Candidate 'local' ...
- validate-draft: draft contract accepted
- critique-incident: Candidate 'cloud' ...
- validate-critique: critique contract accepted
- decide-action: incident-policy/v1 selected OPEN_INCIDENT:P1
- execute-action: action applied once
Final deterministic decision: OPEN_INCIDENT:P1
Idempotent action receipt: incident-<run-id>
```

El ejemplo usa gateways falsos y respuestas JSON fijas. Esto permite estudiar
la arquitectura sin claves ni red. Con gateways reales, solamente las etapas de
análisis y crítica pasan a ser no deterministas.

Cuando quieras reemplazarlos, corré primero el ejemplo
[`real-providers`](../../angelnairav2_public/Packages/MiyuAgents/examples/real-providers/).
Incluye OpenAI-compatible, Anthropic y Gemini; presets del primero cubren
OpenAI, Azure OpenAI, DeepSeek, Groq, Mistral, OpenRouter y Ollama. El cambio ocurre en la
composición de DI: perfiles, stages, gates y política del incidente no cambian.
Para probar localmente sin cambiar el workflow, el README propone rutas Ollama
rápidas, críticas, de razonamiento, código y visión con etiquetas explícitas.

## Parte B · Identificá las fronteras

Abrí `Program.cs` y clasificá cada etapa:

| Etapa | Entrada | Salida | Naturaleza |
|---|---|---|---|
| `CollectStatusStage` | servicio objetivo | `StatusSnapshot` | adaptador determinista |
| `NormalizeStatusStage` | snapshot crudo | `StatusSignal` | determinista |
| `DraftIncidentStage` | señal validada | `IncidentDraft` | no determinista |
| `ValidateDraftStage` | borrador | borrador normalizado | determinista |
| `CritiqueIncidentStage` | borrador | `IncidentCritique` | no determinista |
| `ValidateCritiqueStage` | crítica | crítica normalizada | determinista |
| `DecideActionStage` | señal + crítica | acción | determinista |
| `ExecuteActionStage` | decisión | recibo idempotente | determinista con efecto externo |

Notá que ambos resultados LLM se deserializan y después atraviesan un stage de
validación determinista independiente. Texto libre no entra directamente a la
política ni al puerto de acción.

## Parte C · Cambiá el routing sin cambiar stages

Probá estas variantes en los `LlmRouteProfile`:

1. Agregá `ExcludedTags = ["cloud"]` al crítico.
2. Registrá un segundo gateway con tags `critic`, `reasoning`, `local` y
   `structured-output`.
3. Poné ese modelo segundo en `PreferredModels`.
4. Marcá el gateway cloud como no disponible.

El stage no debe cambiar. Solo cambian candidatos y perfiles.

Comprobá en `RoutingDecision.Evaluations`:

- cuál fue seleccionado;
- qué preferencias sumaron puntaje;
- por qué se descartó cada alternativa.

## Parte D · Simulá retry y fallback

Hacé que el gateway primario:

- lance `TimeoutException` dos veces y luego responda;
- lance HTTP 401;
- lance HTTP 503 hasta agotar intentos.

Esperado:

| Falla | Resultado |
|---|---|
| timeout y luego éxito | tres intentos sobre la misma ruta |
| HTTP 401 | un intento y fallback inmediato |
| HTTP 503 repetido | retries sobre la misma ruta y luego fallback |

Inspeccioná `LlmExecutionResult.Attempts`. Ese historial es el insumo que luego
podemos persistir o proyectar para observabilidad.

## Parte E · Llevá el caso a Eventuous

La adaptación ya es ejecutable dentro de `02-ejemplo`. El aggregate
`IncidentInvestigation` acepta:

```text
StartIncidentInvestigation
RecordIncidentSignal
RecordIncidentAnalysis
RecordIncidentCritique
DecideIncidentAction
ConfirmIncidentOpened | ConfirmHumanReviewRequested
ParkIncidentAction
RetryParkedIncidentAction
```

Y produce un único stream `incident-investigation-{id}`:

```text
IncidentInvestigationStarted
IncidentSignalRecorded
IncidentAnalysisRecorded
IncidentCritiqueRecorded
IncidentActionDecided
IncidentActionParked
IncidentActionRetryRequested
IncidentOpened | IncidentHumanReviewRequested
```

Los comandos reciben datos ya obtenidos. El aggregate no conoce collectors,
gateways, pipelines ni nodos. Valida:

- no analizar antes de tener señal;
- no decidir antes de tener crítica válida;
- contratos estructurados y auditoría mínima de routing;
- no abrir un P1 cuando la acción decidida fue revisión humana.

`IncidentPolicy` calcula la acción dentro del dominio usando señal observable y
crítica validada. La severidad sugerida por el analista queda auditada, pero no
autoriza por sí sola el efecto.

`IncidentInvestigationProcess` termina después de persistir
`IncidentActionDecided`: hasta ahí hay cinco eventos y todavía no ocurrió el
efecto. La suscripción `IncidentActions`, con checkpoint propio, entrega esa
decisión a `IncidentActionReactionProcessor`. El procesador llama al puerto
externo con `incident-investigation:{InvestigationId}` y aplica esta política:

- fallo transitorio conocido → retry acotado con backoff;
- fallo permanente o transitorio agotado → `IncidentActionParked`;
- excepción desconocida → se propaga y el checkpoint no avanza;
- éxito → persiste `IncidentOpened` o `IncidentHumanReviewRequested`.

Si el host cae antes del efecto, el checkpoint sigue detrás de la decisión. Si
hay una reentrega después del efecto, la misma clave devuelve el mismo recibo;
la confirmación repetida con esos datos también es válida. Una confirmación
conflictiva se rechaza. El adapter HTTP envía esa clave en `Idempotency-Key`:
el proveedor debe conservarla durablemente incluso si la app se reinicia.
Antes del I/O, la reacción reconstruye el aggregate; una reentrega ya
`Completed` o `ActionParked` se reconoce sin volver a llamar al puerto.

`ActionParked` es una terminal operativa, no un callejón sin salida. Después
de reparar la causa, `IncidentActionRetryProcess` envía
`RetryParkedIncidentAction`. El aggregate exige estado estacionado y persiste
`IncidentActionRetryRequested` con:

- `RequestId` provisto por quien solicita, usado para deduplicar;
- actor y motivo de la intervención;
- número monotónico de reintento manual;
- la decisión que volverá a ejecutar la suscripción.

El comando no llama al proveedor ni borra la falla anterior. La historia queda
`... → ActionParked → ActionRetryRequested → IncidentOpened`; solamente
el estado actual y la proyección limpian la falla vigente. Repetir cualquier
`RequestId` ya aplicado es un no-op, incluso después de completar.

Archivos clave:

- `src/CursoAgentes.Domain/Incidents/IncidentInvestigation.cs`;
- `src/CursoAgentes.Engine/Incidents/IncidentInvestigationProcess.cs`;
- `src/CursoAgentes.Engine/Incidents/IncidentActionReactionProcessor.cs`;
- `src/CursoAgentes.Infrastructure/Incidents/IncidentActionReaction.cs`;
- `src/CursoAgentes.Infrastructure/Incidents/HttpIncidentActionPort.cs`;
- `src/CursoAgentes.Infrastructure/Projections/IncidentReadModelStore.cs`;
- `tests/CursoAgentes.Tests/IncidentInvestigationTests.cs`.

Probalo sin Postgres:

```bash
cd 02-ejemplo
dotnet test --filter "FullyQualifiedName~IncidentInvestigation"
```

Y con auditoría y proyección PostgreSQL:

```bash
docker compose up -d
dotnet run --project src/CursoAgentes.App -- --incident
```

Para observar el parking sin desplegar otro servicio:

```bash
dotnet run --project src/CursoAgentes.App -- \
  --incident \
  --IncidentAction:Provider=Http \
  --IncidentAction:BaseUrl=http://127.0.0.1:59999/ \
  --IncidentAction:Timeout=00:00:00.250 \
  --IncidentAction:Retry:MaxAttempts=1
```

Copiá el `investigationId` estacionado que imprime esa corrida, restaurá un
provider sano y solicitá la recuperación:

```bash
dotnet run --project src/CursoAgentes.App -- \
  --incident-retry inv-1234abcd \
  --IncidentRetry:RequestId=retry-001 \
  --IncidentRetry:RequestedBy=course-operator \
  --IncidentRetry:Reason="provider configuration repaired"
```

Volvé a ejecutar exactamente el mismo comando: el stream debe conservar la
misma cantidad de eventos y el efecto no debe repetirse.

El read model conserva señal, resumen, perfil, ruta, modelo, intentos, decisión,
versión de política y recibo. Si la acción se estaciona, también conserva código
de falla, clasificación y cantidad de intentos. No persiste prompts ni
razonamiento interno. Tras una recuperación también expone cantidad de
reintentos manuales, último `RequestId`, actor y motivo.

## Parte F · Versión con nodos fijos

Antes de adaptarla, corré la implementación de referencia desde la raíz del
curso:

```bash
dotnet run --project ../angelnairav2_public/Packages/MiyuAgents/examples/fixed-node-workflow/FixedNodeWorkflowExample.csproj
```

La salida confirma explícitamente que es una secuencia fija sin recursión. El
grafo completo es:

```text
collect → normalize → draft → validate draft
        → critique → validate critique → decide → execute
```

Comparalo con el pipeline de la Parte A. Ambas variantes reutilizan la misma
fuente, los mismos contratos, perfiles de routing, política e idempotencia. Las
diferencias que deberías encontrar son:

| Pipeline | Grafo fijo |
|---|---|
| resultados en `PipelineContext.SharedData` | resultados como `Artifact` tipado |
| corte mediante `PipelineStageResult.Abort` | corte mediante `NodeSignal.Failed` |
| historial de stages | traza y transcript por nodo |
| estructura lineal directa | composición posterior con otros nodos |

El grafo fijo de MiyuAgents no persiste checkpoints por sí mismo: si necesitás
reanudarlo, agregá una estrategia explícita. En cambio, el ejemplo recursivo de
Eventuous ya implementa `StartAsync`/`ResumeAsync` leyendo sus streams. No
confundas grafo, reanudación y recursión; son tres capacidades independientes.

## Parte G · Versión recursiva

Primero observá una variante puramente recursiva de refinamiento en MiyuAgents:

```bash
dotnet run --project ../angelnairav2_public/Packages/MiyuAgents/examples/recursive-review-workflow/RecursiveReviewWorkflowExample.csproj
```

El grafo exterior conoce tres etapas, pero cada etapa es un
`RecursiveObjectiveNode<TState>` que se evalúa, se refina y recursa hasta pasar
su gate. El ejemplo demuestra una recursión **funcional** sobre estado tipado;
no crea hijos dinámicos ni persiste checkpoints.

Para una investigación que descubre subproblemas, en cambio, necesitás
recursión **estructural** y podés reutilizar `RecursiveWorkflowRunner`:

1. La raíz es “investigar degradación de payments-api”.
2. El planner propone sub-objetivos como regiones, dependencias o despliegues.
3. Las hojas llaman herramientas deterministas de observabilidad.
4. Los nodos padre sintetizan resultados.
5. Fuera de la recursión, un crítico revisa la síntesis raíz.
6. La política determinista decide la acción.

Agregá dos límites al manifiesto además de `MaxDepth` y
`MaxChildrenPerNode`:

```csharp
public int MaxToolCalls { get; init; } = 20;
public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(2);
```

El planner propone ramas; el motor decide si todavía existe presupuesto para
crearlas.

## Parte H · Tests mínimos

La solución final distribuye estas verificaciones entre el ejemplo Eventuous y
MiyuAgents:

1. señal insuficiente + crítica positiva → no abre P1;
2. señal suficiente + crítica con baja confianza → revisión humana;
3. señal suficiente + crítica confirmada → abre una sola vez;
4. timeout transitorio → retry en la misma ruta;
5. error permanente → usa una ruta distinta;
6. replay de eventos → cero llamadas al LLM y cero efectos externos;
7. planner que siempre divide → termina en `MaxDepth` o presupuesto.
8. caída o error después de decidir → quedan cinco eventos y la reacción puede
   completar el sexto al reanudarse.
9. acción estacionada + recuperación manual → agrega solicitud y luego
   confirmación, sin borrar la historia de la falla.
10. mismo `RequestId` antes o después de completar → cero eventos y efectos
    adicionales.

Los puntos 1, 2, 3 y 6 están en `IncidentInvestigationTests`; 4 y 5 en los
tests de `LlmCallExecutor`; 7 en `RecursiveWorkflowRunnerTests` y
`RecursiveWorkflowNodeTests`.

## Parte I · Puente MiyuAgents → Eventuous

La solución incluye una capa anti-corruption ejecutable:

```text
PipelineContext.SharedData ─┐
                            ├─→ IncidentInvestigationInput ─→ 5 eventos
WorkflowNode.Artifacts ─────┘                                  │
                                                              ▼
                                            IncidentActions subscription
                                                              │
                                                   efecto + 1 confirmación
```

`MiyuIncidentInvestigationMapper` conoce ambos contratos y preserva señal,
análisis, crítica, perfil, route id, proveedor, modelo e intentos. También
vuelve a calcular la política del dominio y rechaza el mapeo si la decisión de
Miyu y la de Eventuous divergen.

Para evitar doble delivery, el puente usa
`CreateOfflineForEventSourcing()` en ambas orquestaciones. Esas variantes
terminan después de `decide-action`: los action ports de Miyu quedan en cero y
`IncidentActionReaction`, su procesador y `IncidentActionHandler` forman la
única autoridad para el efecto externo.

Archivos:

- `src/CursoAgentes.MiyuAgents/MiyuIncidentInvestigationBridge.cs`;
- `tests/CursoAgentes.Tests/MiyuEventuousBridgeTests.cs`.

La app permite recorrer el puente completo contra Postgres con cualquiera de
las dos representaciones. En ambos casos imprime
`efectos ejecutados por MiyuAgents=0`; el efecto confirmado pertenece a
Eventuous:

```bash
cd 02-ejemplo
dotnet run --project src/CursoAgentes.App -- --incident-pipeline
dotnet run --project src/CursoAgentes.App -- --incident-nodes
```

Verificación:

```bash
cd 02-ejemplo
dotnet test --filter "FullyQualifiedName~MiyuEventuousBridge"
```

Los tests prueban que pipeline y nodos producen inputs equivalentes, persisten
cinco eventos sin ejecutar efectos y sólo llegan al sexto evento cuando la
reacción Eventuous procesa `IncidentActionDecided`.

## Criterio de finalización

El ejercicio está completo cuando podés responder, para cualquier ejecución:

- qué datos vinieron de sistemas externos;
- qué partes propuso cada LLM;
- qué gateway y modelo efectivos se usaron;
- qué falló y cuántas veces se intentó;
- qué regla autorizó la acción;
- cómo evitás repetirla al reanudar o hacer replay.

---

**Siguiente**: [Paso 13 · API asíncrona](13-api-asincrona-y-resume.md), donde
el workflow recursivo se acepta con `202`, se ejecuta en un worker y expone
estado, árbol, auditoría y resume.
