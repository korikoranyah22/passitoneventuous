# Opción A · Pipeline normal

Este ejecutable representa un proceso conocido de ocho etapas. Cada etapa corre
una vez y comparte datos mediante `PipelineContext.SharedData`:

1. recolectar observaciones de sólo lectura;
2. detectar findings reproducibles;
3. pedir una evaluación al perfil LLM de triage;
4. validar esquema y citas;
5. pedir una crítica independiente;
6. validar la crítica;
7. aplicar una política versionada;
8. abrir un caso humano con idempotencia.

Es la opción más simple cuando no hay refinamiento ni branching dinámico. Un
contrato inválido produce `PipelineStageResult.Abort` y las etapas posteriores
no se ejecutan.

```powershell
dotnet run --project 06-casos-practicos-electorales/src/ElectionAudit.Pipeline
```
