# Paso 14 · Auditoría defensiva de sistemas electorales

## Objetivo

Traducir la idea de una "IA inversa" defensiva a tres aplicaciones completas,
auditables y comparables, sin convertir al modelo en autoridad electoral ni
enseñar fases de ataque.

El código está en
[`06-casos-practicos-electorales`](../06-casos-practicos-electorales/).
Las tres opciones consumen los mismos ports, observaciones sintéticas, detector,
perfiles LLM y política. Sólo cambia la abstracción de control.

## Antes del código: qué se investigó

La [investigación y sus fuentes oficiales](../06-casos-practicos-electorales/00-investigacion-y-supuestos.md)
fija cuatro límites del modelo:

1. El Código Electoral permite **proyectar** la elección general nacional para
   el 24 de octubre de 2027. El ejemplo marca esa fecha como
   `ProjectedFromCurrentLaw`: no inventa un cronograma oficial.
2. El ciclo constitucional exige modelar la fórmula presidencial, la mitad de
   Diputados y un tercio de los distritos del Senado; el detalle oficial de
   bancas y distritos no se inventa y entra por el calendario.
3. La elección nacional usa Boleta Única de Papel. No se modela voto
   electrónico ni preferencias individuales.
4. Recuento provisorio y escrutinio definitivo son procesos distintos. El
   workflow observa sistemas auxiliares del provisorio; nunca decide validez.
5. La suspensión de PASO fue específica de 2025. Las fases de 2027 entran por
   un port configurable y no quedan hardcodeadas como hecho jurídico.

El simulacro oficial 2025 incluyó transmisión, recepción, carga/digitación,
procesamiento, fiscalización, totalización y difusión. Esas fases inspiran los
ports, pero el curso no afirma que exista hoy una API pública con estos nombres.

## Interfaces hipotéticas de sólo lectura

| Port | Observación mínima | Autoridad que no concede |
|---|---|---|
| `IElectionCalendarPort` | contiendas, fases, ventanas y fuente | cambiar el cronograma o bancas |
| `ISoftwareBaselinePort` | releases aprobadas y mediciones | desplegar software |
| `ITransmissionObservationPort` | recibos y digests de sobres | leer o alterar votos |
| `IDigitizationAuditPort` | doble carga, digest y visibilidad | corregir resultados |
| `IAccessAuditPort` | accesos seudonimizados | obtener credenciales |
| `IPublicationCheckpointPort` | secuencia, cantidad y digest | publicar resultados |
| `IHumanReviewCasePort` | alta idempotente de revisión | contener automáticamente |

Los adapters de `SyntheticElectionEnvironment` implementan estos contratos
offline. Un host autorizado podría reemplazarlos por adaptadores reales con
autenticación mutua, mínimo privilegio, retención y logs inmutables sin cambiar
el workflow.

## Frontera entre agencia y control

```text
ports de sólo lectura
  → detección determinista con evidence IDs
  → evaluación LLM privada
  → gate determinista de esquema y citas
  → crítica LLM independiente
  → gate determinista de crítica
  → política versionada
  → caso humano idempotente
```

La evaluación y la crítica pueden variar. No pueden crear evidencia válida:
`ElectionAuditContracts` exige que citen **todos y sólo** los finding IDs del
detector. La acción se calcula con `ElectionAuditPolicy`, que siempre devuelve:

```text
RequiresHumanApproval = true
AutomaticMutationAllowed = false
```

## Opción A · Pipeline normal

[`ElectionAudit.Pipeline`](../06-casos-practicos-electorales/src/ElectionAudit.Pipeline/)
implementa ocho `IPipelineStage` ordenados por prioridad. Los datos pasan por
`PipelineContext.SharedData`; un contrato inválido aborta la cadena antes del
efecto.

```powershell
dotnet run --project 06-casos-practicos-electorales/src/ElectionAudit.Pipeline
```

Elegí esta opción si el orden se conoce, cada etapa corre una vez y no necesitás
semántica de nodo. Es el baseline de menor complejidad.

## Opción B · Nodos fijos

[`ElectionAudit.FixedNodes`](../06-casos-practicos-electorales/src/ElectionAudit.FixedNodes/)
convierte las mismas ocho etapas en `INodeAgent`. Un `WorkflowNode` y una
`SequenceStrategy` controlan la ejecución; los resultados pasan como artifacts
tipados y un fallo se expresa como `NodeSignal.Failed`.

```powershell
dotnet run --project 06-casos-practicos-electorales/src/ElectionAudit.FixedNodes
```

El grafo es deliberadamente no recursivo. Los nodos se justifican por
composición, señales, trazas y transcripts, no porque todo proceso agéntico
deba recursar.

## Opción C · Objetivos recursivos

[`ElectionAudit.Recursive`](../06-casos-practicos-electorales/src/ElectionAudit.Recursive/)
mantiene una secuencia exterior fija y recursa sólo objetivos con progreso
medible:

| Nodo | Estado | Gate | Resultado del fixture |
|---|---|---|---|
| completar feeds | `IncrementalObservationState` | seis fuentes presentes | 6 refinamientos |
| fundamentar evaluación | `AssessmentObjectiveState` | esquema y citas completas | 2 refinamientos |
| sostener crítica | `CritiqueObjectiveState` | supported, ≥ 0.85, sin claims huérfanos | 2 refinamientos |

```powershell
dotnet run --project 06-casos-practicos-electorales/src/ElectionAudit.Recursive
```

Cada `RecursiveObjectiveNode<TState>` declara profundidad, llamadas, duración y
`cycleKey`. El detector, la política y el case port continúan como nodos
normales porque su salida debe ser reproducible y no mejora al recursar.

## Comparación verificable

| Propiedad | Pipeline | Nodos fijos | Recursivo |
|---|---|---|---|
| pasos conocidos | sí | sí | secuencia exterior |
| artifacts/señales de nodo | no | sí | sí |
| número dinámico de refinamientos | no | no | sí, acotado |
| detector y política compartidos | sí | sí | sí |
| mutación automática | no | no | no |
| case port idempotente | sí | sí | sí |

La suite comprueba el contrato compartido y evita que una diferencia de control
se confunda con una diferencia de negocio:

```powershell
dotnet test 06-casos-practicos-electorales/tests/ElectionAudit.Tests
```

## Routing privado

El fixture ofrece dos perfiles independientes:

- `private-provisional-triage`: `private, triage, structured-output`;
- `private-provisional-critic`: `private, critic, reasoning, structured-output`.

`AllowFallback = false` impide que telemetría no sanitizada caiga por accidente
en una ruta sin la capability requerida. En un laboratorio se pueden sustituir
los gateways scripted por Ollama u otro proveedor aprobado sin cambiar ports,
contratos ni política.

## Extensión event-sourced

Para sobrevivir reinicios, persistí como eventos el hash y ventana del lote,
findings y evidencia, ruta/modelo efectivos, evaluaciones rechazadas, criterio
recursivo incumplido, versión de política, decisión, intención y recibo del caso.
No hace falta guardar razonamiento privado; sí poder reconstruir qué evidencia,
contrato y política produjeron una revisión.

## Límites

Estos ejemplos no son una certificación electoral ni una integración oficial.
No incluyen credenciales, escaneo activo, explotación, malware, contenido de
telegramas ni acciones de contención. El ejemplo compacto de referencia del
framework permanece en
[`MiyuAgents/examples/election-defense`](../../angelnairav2_public/Packages/MiyuAgents/examples/election-defense/);
la carpeta `06-casos-practicos-electorales` es la versión pedagógica completa.
