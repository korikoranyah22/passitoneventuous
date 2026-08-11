# Paso 2 · Solución y proyectos

## Objetivo

Crear la solución .NET con 5 proyectos y entender **por qué** están separados
así. La separación en capas no es burocracia: es lo que permite testear el
dominio sin infraestructura y cambiar de proveedor de LLM sin tocar el motor.

## La solución

```bash
cd 02-ejemplo
dotnet new sln -n CursoAgentes                 # crea CursoAgentes.slnx
dotnet new classlib -n CursoAgentes.Domain        -o src/CursoAgentes.Domain
dotnet new classlib -n CursoAgentes.Engine        -o src/CursoAgentes.Engine
dotnet new classlib -n CursoAgentes.Infrastructure -o src/CursoAgentes.Infrastructure
dotnet new console -n CursoAgentes.App            -o src/CursoAgentes.App
dotnet new xunit   -n CursoAgentes.Tests          -o tests/CursoAgentes.Tests

dotnet sln add src/CursoAgentes.Domain src/CursoAgentes.Engine \
            src/CursoAgentes.Infrastructure src/CursoAgentes.App \
            tests/CursoAgentes.Tests
```

## Las dependencias entre proyectos

```
CursoAgentes.Domain  ← no depende de nada (solo Eventuous)
CursoAgentes.Engine  ← referencia a Domain
CursoAgentes.Infrastructure ← referencia a Domain + Engine
CursoAgentes.App     ← referencia a Infrastructure (transitivamente todo)
CursoAgentes.Tests   ← referencia a Domain + Engine + Infrastructure
```

| Proyecto | Responsabilidad | Regla de oro |
|---|---|---|
| `Domain` | Eventos, estado, comandos, guards | **Cero I/O**: ni HTTP, ni BD, ni LLM |
| `Engine` | Agentes, contexto, runner recursivo | Habla con `ILlmGateway` (puerto), nunca con un proveedor |
| `Infrastructure` | Event store, proyecciones, adaptadores HTTP | Lo único que conoce Postgres y HTTP de verdad |
| `App` | Demo, configuración, wiring | Orquesta el arranque |
| `Tests` | Todo lo anterior, sin infraestructura | Usa el event store en memoria + el LLM falso |

## Paquetes NuGet por proyecto

- **Domain** (y Tests, e Infrastructure): `Eventuous` 0.16.3,
  `Eventuous.Application` 0.16.3.
- **Infrastructure**: + `Eventuous.Postgresql` 0.16.3,
  `Eventuous.Extensions.DependencyInjection` 0.16.3, `Npgsql`,
  `Microsoft.Extensions.Http`.
- **App**: `Microsoft.Extensions.Hosting` (trae config + DI + hosted services).
- **Tests**: `xunit`, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`.

> 📌 `net10.0` en todos (`<TargetFramework>net10.0</TargetFramework>`),
> `Nullable` y `ImplicitUsings` habilitados.

## Probalo

```bash
dotnet build    # tiene que compilar (los proyectos todavía están vacíos)
```

---

**Siguiente**: [Paso 3 · Aggregate WorkflowRun](03-aggregate-workflowrun.md)
