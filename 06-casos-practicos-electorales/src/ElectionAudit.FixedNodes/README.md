# Opción B · Nodos fijos no recursivos

Este ejecutable expresa las mismas ocho etapas como `INodeAgent` dentro de un
`WorkflowNode` gobernado por `SequenceStrategy`.

Los resultados viajan como artifacts tipados. Esto habilita composición,
señales, trazas y transcripts de MiyuAgents, pero no introduce recursión: el
roster y el orden se conocen antes de ejecutar.

Un contrato inválido produce `NodeSignal.Failed`; el padre corta el grafo antes
de abrir un caso. Esta opción conviene cuando se necesitan capacidades de nodo,
aunque el control siga siendo lineal.

```powershell
dotnet run --project 06-casos-practicos-electorales/src/ElectionAudit.FixedNodes
```
