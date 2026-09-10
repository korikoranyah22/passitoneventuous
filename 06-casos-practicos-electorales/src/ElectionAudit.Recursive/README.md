# Opción C · Objetivos recursivos acotados

La secuencia exterior sigue siendo fija. Sólo recursan tres nodos cuyo número
de refinamientos no se conoce de antemano:

| Objetivo | Estado tipado | Criterio de salida | Refinamientos del fixture |
|---|---|---|---|
| completar observaciones | `IncrementalObservationState` | seis feeds presentes | 6 |
| fundamentar evaluación | `AssessmentObjectiveState` | esquema y todas las citas válidas | 2 |
| sostener crítica | `CritiqueObjectiveState` | veredicto, confianza y cobertura | 2 |

Cada `RecursiveObjectiveNode<TState>` declara `MaxDepth`, `MaxCalls`, duración
y `cycleKey`. El trampoline de MiyuAgents evita hacer crecer el stack de CLR.
Los nodos de política y efecto no recursan porque su operación es determinista.

```powershell
dotnet run --project 06-casos-practicos-electorales/src/ElectionAudit.Recursive
```
