# Paso 1 · Entorno

## Requisitos

| Herramienta | Versión | Para qué |
|---|---|---|
| .NET SDK | **10.0** (el ejemplo usa `net10.0`) | Compilar y correr todo |
| Docker (con Compose) | — | Levantar PostgreSQL 16 local |
| (opcional) Ollama / DeepSeek / etc. | — | Probar con un LLM real |

Verificá:

```bash
dotnet --version        # 10.0.x
docker --version        # disponible
```

## Comandos base que vas a usar todo el curso

```bash
# ── Desde la carpeta 02-ejemplo ──────────────────────────────────────────

# Compilar toda la solución (sin warnings en verde)
dotnet build

# Correr los 29 tests (NO necesitan Postgres)
dotnet test

# Levantar Postgres (event store + read model en la misma base)
docker compose up -d

# Correr la demo end-to-end (usa el LLM falso por default)
dotnet run --project src/CursoAgentes.App

# Correr la demo con tu propio objetivo
dotnet run --project src/CursoAgentes.App "¿Qué es un aggregate en DDD?"

# Parar y borrar Postgres (¡borra los eventos!)
docker compose down -v
```

## Si no tenés Docker

Podés usar cualquier Postgres 16 (instalado, en la nube…). Solo ajustá la
connection string en `src/CursoAgentes.App/appsettings.json`:

```jsonc
"ConnectionStrings": {
  "EventStore": "Host=localhost;Port=5432;Database=cursoagentesdb;Username=cursoagentes;Password=curso123"
}
```

> ⚠️ **Importante**: todo el *código* (dominio, motor, tests) corre sin
> Postgres. Postgres solo se necesita para la demo end-to-end y la
> persistencia real. Podés seguir toda la guía hasta el paso 9 sin levantarlo.

## Nota sobre el formato de solución

El SDK 10 crea el nuevo formato `CursoAgentes.slnx` (XML) en vez del `.sln`
tradicional. Los comandos `dotnet build` / `dotnet test` / `dotnet sln add`
funcionan igual; solo fijate que el archivo se llame `CursoAgentes.slnx`.

---

**Siguiente**: [Paso 2 · Solución y proyectos](02-solucion-y-proyectos.md)
