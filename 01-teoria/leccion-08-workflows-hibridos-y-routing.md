# Lección 8 · Workflows híbridos: routing, crítica y decisión determinista

> Un sistema de agentes útil no intenta que el LLM controle todo. Le entrega al
> modelo las tareas donde aporta interpretación y mantiene en código las
> restricciones, validaciones y acciones que deben ser repetibles.

En esta lección usamos un caso operativo concreto:

> Recolectar señales de salud de un servicio, producir un diagnóstico, hacer
> que otro agente lo critique y decidir mediante reglas si se abre un incidente.

## 8.1 El flujo completo

```text
Recolectar estado
      ↓
Validar y normalizar datos
      ↓
Analista LLM redacta un diagnóstico
      ↓
Crítico LLM busca errores y evidencia faltante
      ↓
Validar las salidas estructuradas
      ↓
Regla determinista: abrir incidente o pedir revisión humana
      ↓
Persistir eventos y ejecutar la acción una sola vez
```

La distinción importante no es “agente versus código”. Es esta:

| Parte | Naturaleza | Por qué |
|---|---|---|
| Consultar status page, métricas o API | Determinista | Mismo origen, mismo dato observable |
| Normalizar y validar campos | Determinista | El contrato no debe depender de interpretación |
| Explicar qué podría estar pasando | No determinista | Requiere síntesis e interpretación contextual |
| Criticar el diagnóstico | No determinista | Conviene diversidad de razonamiento |
| Verificar JSON, rangos y evidencia mínima | Determinista | Es una frontera de seguridad |
| Abrir un incidente P1 | Determinista e idempotente | Es un efecto externo importante |

El LLM puede **proponer** una severidad. La política decide si esa propuesta,
junto con señales verificables, alcanza para ejecutar una acción.

## 8.2 Por qué usar dos agentes

Analista y crítico cumplen objetivos distintos:

- El analista intenta construir la mejor explicación con la evidencia disponible.
- El crítico intenta refutarla, detectar saltos lógicos y señalar datos faltantes.
- Ninguno abre el incidente.

No buscamos que dos LLM “voten la verdad”. Buscamos producir mejores insumos
para una decisión que conserva restricciones explícitas:

```csharp
var openIncident = signal.FailedChecks >= 3
    && signal.AffectedRegions >= 2
    && critique.Verdict == "confirmed"
    && critique.Confidence >= 0.80;
```

La regla es fácil de testear, auditar y cambiar. También deja visible qué parte
del resultado vino del mundo y cuál vino de una interpretación probabilística.

## 8.3 Routing: elegir por capacidad, no por nombre de proveedor

Un agente no debería decir “usá Claude” u “Ollama”. Debería declarar qué
necesita:

```text
analista:
  required:  structured-output, analyst
  preferred: fast, private

crítico:
  required:  structured-output, critic, reasoning
  preferred: high-quality, long-context
```

El host configura qué gateways cumplen esas capacidades. Así se pueden cambiar
modelos, deshabilitar una ruta o preferir ejecución local sin modificar los
agentes ni los nodos.

En MiyuAgents esto se expresa mediante `LlmRouteProfile`:

```csharp
new LlmRouteProfile
{
    Name = "critical-judge",
    Route = new RouteRequest
    {
        RequiredTags = ["critic", "reasoning", "structured-output"],
        PreferredTags = new Dictionary<string, int>
        {
            ["high-quality"] = 50,
            ["long-context"] = 20
        }
    },
    PreferredModels = ["quality-model", "local-reasoner"]
};
```

La selección produce una decisión explicable: candidato elegido, puntaje,
preferencias satisfechas y motivos de rechazo de las demás rutas.

## 8.4 Retry y fallback no son lo mismo

```text
timeout transitorio
    → retry en la MISMA ruta

401, configuración inválida o retries agotados
    → excluir la ruta
    → tomar una NUEVA decisión de routing
```

Esta separación evita dos errores comunes:

1. Cambiar de modelo ante cualquier timeout, aunque el proveedor pudiera
   recuperarse en el siguiente intento.
2. Reintentar tres veces una credencial inválida o un modelo inexistente.

`ILlmCallExecutor` centraliza esta política para llamadas no streaming. No
reproduce automáticamente un stream que ya mostró tokens: podría duplicar texto
visible al usuario.

## 8.5 La versión pipeline

Un pipeline es apropiado cuando la forma general del proceso es conocida:

```text
[100] CollectStatusStage       adaptador determinista
[200] NormalizeStatusStage     normalización determinista
[300] DraftIncidentStage       LLM, perfil fast-private-analysis
[350] ValidateDraftStage       validación determinista
[400] CritiqueIncidentStage    LLM, perfil critical-judge
[450] ValidateCritiqueStage    validación determinista
[600] DecideActionStage        política determinista versionada
[700] ExecuteActionStage       efecto idempotente
```

Cada stage puede detener el flujo si falta un contrato. El pipeline controla el
orden; los LLM controlan solamente el contenido de sus respuestas.

El ejemplo ejecutable está en
[MiyuAgents/examples/routing-workflow](../../angelnairav2_public/Packages/MiyuAgents/examples/routing-workflow/).

## 8.6 La versión con nodos no recursivos

El mismo caso está implementado como un grafo fijo de ocho nodos:

```text
collect → normalize → draft → validate draft
        → critique → validate critique → decide → execute
```

Conviene usar nodos cuando necesitamos:

- Estado y traza por paso.
- Reanudar desde un punto intermedio.
- Ejecutar ramas en paralelo.
- Persistir artefactos y señales entre pasos.

La semántica sigue siendo la misma: `AnalyzeNode` y `CritiqueNode` son
probabilísticos; `PolicyNode` y `ActionNode` son deterministas.

El ejemplo ejecutable está en
[MiyuAgents/examples/fixed-node-workflow](../../angelnairav2_public/Packages/MiyuAgents/examples/fixed-node-workflow/).
Reutiliza los mismos contratos, adaptadores, servicio LLM ruteado y política del
pipeline. Solamente cambia la orquestación: `SharedData` se reemplaza por
artefactos tipados y cada paso obtiene señales y trazas de nodo.

Esto también deja una distinción importante: **usar nodos no implica usar
recursión**. Este grafo tiene forma fija y usa la estrategia `sequence`.

## 8.7 Cuándo tiene sentido la recursión

No todo incidente necesita un árbol. La recursión aporta valor cuando el número
de fuentes o preguntas se descubre durante la ejecución:

```text
Investigar incidente
├── Revisar región us-east
│   ├── consultar errores HTTP
│   └── consultar latencia de base de datos
├── Revisar región eu-west
│   ├── consultar errores HTTP
│   └── consultar saturación de workers
└── Comparar con despliegues recientes
```

El planner puede proponer sub-investigaciones, pero el runner conserva límites
duros:

- `MaxDepth`.
- `MaxChildrenPerNode`.
- Presupuesto de tiempo, tokens o llamadas.
- Lista de herramientas permitidas.
- Dedupe de objetivos equivalentes.

La apertura del incidente **no debe ser recursiva**. Se ejecuta una sola vez,
después de sintetizar y criticar el árbol completo.

## 8.8 Qué persiste Eventuous

El aggregate no llama al LLM. Recibe comandos construidos con resultados ya
obtenidos y valida transiciones deterministas.

La implementación del curso usa esta historia:

```text
IncidentInvestigationStarted
IncidentSignalRecorded
IncidentAnalysisRecorded
IncidentCritiqueRecorded
IncidentActionDecided
IncidentActionParked                    # si el efecto no puede completarse
IncidentActionRetryRequested            # recuperación manual auditada, opcional
IncidentOpened | IncidentHumanReviewRequested
```

Para auditoría conviene guardar:

- Identidad de la señal y momento de recolección.
- Artefacto estructurado del analista.
- Veredicto estructurado del crítico.
- Perfil, ruta y modelo efectivos.
- Historial de intentos sin secretos ni prompts sensibles.
- Versión de la política determinista.
- Clave de idempotencia de la acción externa.
- Identidad, motivo y `RequestId` de cada recuperación manual.

Durante un replay se reaplican eventos. No se vuelve a consultar el status page,
no se llama otra vez al LLM y no se abre nuevamente el incidente.

El aggregate y su proceso de aplicación están en
[`02-ejemplo`](../02-ejemplo/src/CursoAgentes.Domain/Incidents/IncidentInvestigation.cs).
La salida externa escucha una decisión ya persistida y tolera reentregas usando
una clave derivada del identificador de investigación. Los fallos externos
clasificados como transitorios se reintentan con un presupuesto finito; un
fallo permanente o agotado se estaciona como evento auditable. Una excepción
desconocida no se transforma en dato de negocio: detiene el consumidor.
Un operador puede pedir otro intento después de reparar la causa mediante un
comando idempotente. Ese comando no ejecuta I/O: agrega
`IncidentActionRetryRequested` y la misma reacción durable vuelve a procesar
la decisión.

## 8.9 Regla práctica

> Usá agentes para ampliar, interpretar, proponer y criticar. Usá código para
> validar, limitar, autorizar, persistir y ejecutar efectos externos.

El resultado es menos “mágico”, pero considerablemente más operable.

---

## 📖 Continuación práctica

- [Pipeline ejecutable](../../angelnairav2_public/Packages/MiyuAgents/examples/routing-workflow/)
- [Grafo fijo de nodos ejecutable](../../angelnairav2_public/Packages/MiyuAgents/examples/fixed-node-workflow/)
- [Puente MiyuAgents → Eventuous](../02-ejemplo/src/CursoAgentes.MiyuAgents/MiyuIncidentInvestigationBridge.cs)
- [Guía de routing](../../angelnairav2_public/Packages/MiyuAgents/docs/routing.md)
- [Adaptación paso a paso al curso](../03-guia-paso-a-paso/12-caso-practico-hibrido.md)
