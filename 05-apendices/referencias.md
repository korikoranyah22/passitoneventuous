# Referencias

## Documentación oficial

| Tema | Recurso |
|---|---|
| Eventuous (docs) | https://eventuous.dev/docs/intro/ |
| Eventuous · Command services | https://eventuous.dev/dotnet/application/app-service/ |
| Eventuous · PostgreSQL store | https://eventuous.dev/docs/infra/postgres/ |
| Eventuous · GitHub | https://github.com/Eventuous/eventuous |
| Event Sourcing (Martin Fowler) | https://martinfowler.com/eaaDev/EventSourcing.html |
| CQRS (Martin Fowler) | https://martinfowler.com/bliki/CQRS.html |
| Npgsql (driver PostgreSQL para .NET) | https://www.npgsql.org/ |
| Protocolo `/chat/completions` de OpenAI | https://platform.openai.com/docs/api-reference/chat |
| Ollama (LLM local, compatible OpenAI) | https://ollama.com/ |

## Conceptos que inspiraron el ejemplo

- **Aggregate** (DDD): "A cluster of associated objects that are treated as a
  unit for the purpose of data changes" — Eric Evans, *Domain-Driven Design*
  (2003).
- **Puertos y adaptadores** (hexagonal): Alistair Cockburn,
  https://alistair.cockburn.us/hexagonal-architecture/
- **Recursive task decomposition** en agentes: la idea de *divide-and-conquer*
  con LLM (también conocida como "plan-and-execute" o "tree of thoughts").
- **Event sourcing para workflows**: el argumento de auditoría/reanudación
  para procesos largos aparece en Greg Young, *CQRS Documents* (2010).

## MiyuAgents: continuación ejecutable

- [Routing por capacidades, perfiles y resiliencia](../../angelnairav2_public/Packages/MiyuAgents/docs/routing.md).
- [Pipeline híbrido de incidentes](../../angelnairav2_public/Packages/MiyuAgents/examples/routing-workflow/).
- [El mismo caso como grafo fijo no recursivo](../../angelnairav2_public/Packages/MiyuAgents/examples/fixed-node-workflow/).
- [Objetivos recursivos acotados](../../angelnairav2_public/Packages/MiyuAgents/examples/recursive-review-workflow/).
- [Proveedores reales y configuración segura](../../angelnairav2_public/Packages/MiyuAgents/docs/providers.md).
- [Ejemplo ejecutable con OpenAI, Azure OpenAI, Anthropic, Gemini y compatibles](../../angelnairav2_public/Packages/MiyuAgents/examples/real-providers/).
- [Auditoría defensiva electoral: pipeline, nodos y recursión](../../angelnairav2_public/Packages/MiyuAgents/examples/election-defense/).

## El repo real que inspira el curso

El ejemplo replica (a escala de curso) patrones de un sistema en producción que
usa Eventuous 0.16.3 + PostgreSQL + agentes con LLM:

- Aggregate con `State<T>` + `On<T>` y `[EventType]`.
- `AddEventuousPostgres(conn, schema, initializeDatabase: true)` +
  `AddEventStore<PostgresStore>()` + `AddPostgresCheckpointStore()`.
- Suscripciones `PostgresAllStreamSubscription` con checkpoint agresivo.
- Proyecciones idempotentes a read models en el mismo Postgres.
- Agentes como unidades con rol + contexto + llamadas al LLM por detrás de un
  puerto.

Los comentarios del código del ejemplo marcan dónde aparece cada patrón
("como en el repo real").

## Para profundizar

- **Eventuous samples** (repo oficial, hay ejemplos por store):
  https://github.com/Eventuous/eventuous-samples
- **Intro to Event Sourcing and CQRS** (conferencia de Greg Young, YouTube).
- **Building Event-Driven Microservices** (Adam Bellemare, O'Reilly) — cap.
  de event sourcing aplicado.
- **Designing Data-Intensive Applications** (Martin Kleppmann, O'Reilly) —
  cap. 11: *Unbundling Databases* (muy bueno para entender append-only logs).
