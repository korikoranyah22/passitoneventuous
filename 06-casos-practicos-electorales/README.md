# Tres casos completos de auditoría electoral defensiva

Esta carpeta complementa la teoría del curso con tres aplicaciones ejecutables
que usan las mismas interfaces hipotéticas y el mismo dataset sintético:

| Aplicación | Control | Cuándo elegirla |
|---|---|---|
| `ElectionAudit.Pipeline` | etapas ordenadas de `PipelineRunner` | el proceso es lineal y cada paso corre una vez |
| `ElectionAudit.FixedNodes` | `WorkflowNode` + `SequenceStrategy` | hacen falta artifacts, trazas o composición de nodos |
| `ElectionAudit.Recursive` | objetivos recursivos acotados | evidencia, evaluación o crítica requieren refinamiento medible |

Leé primero [los supuestos y las fuentes](00-investigacion-y-supuestos.md).

## Flujo compartido

```text
calendario + baselines + transmisión + digitación + accesos + publicación
    → findings deterministas con evidencia
    → evaluación LLM privada
    → gate determinista de contrato y citas
    → crítica LLM independiente
    → política versionada
    → caso humano idempotente
```

Los adapters incluidos son offline y reproducibles. Simulan las interfaces que
un entorno autorizado podría ofrecer; no afirman que esas APIs existan hoy.

## Ejecutar

Desde la raíz del curso:

```powershell
dotnet run --project 06-casos-practicos-electorales/src/ElectionAudit.Pipeline
dotnet run --project 06-casos-practicos-electorales/src/ElectionAudit.FixedNodes
dotnet run --project 06-casos-practicos-electorales/src/ElectionAudit.Recursive
dotnet test 06-casos-practicos-electorales/tests/ElectionAudit.Tests
```

Cada aplicación termina en `OPEN_URGENT_HUMAN_REVIEW`, demuestra que
`AutomaticMutationAllowed` es `false` y genera un recibo idempotente. Ninguna
modifica sistemas electorales.

## Proyecto compartido

`ElectionAudit.Shared` contiene:

- contratos de observación y ports;
- adapters sintéticos;
- detector y validadores deterministas;
- perfiles de routing del LLM;
- política de decisión y case port idempotente.

Esto permite comparar las tres abstracciones de control sin cambiar el dominio
ni atribuir diferencias a datasets o políticas distintas.
