# Lección 6 · LLMs en el flujo: puertos y adaptadores

> El LLM es una pieza más del sistema — y tiene que ser **intercambiable**.
> El workflow no debe saber si está hablando con DeepSeek, Ollama local o un
> simulador de tests. Para eso usamos el patrón **puerto/adaptador**: una
> interfaz (el puerto) y varias implementaciones (los adaptadores).

## 6.1 El puerto: `ILlmGateway`

```csharp
public sealed record LlmMessage(string Role, string Content);

public sealed record LlmRequest(
    string SystemPrompt,
    IReadOnlyList<LlmMessage> Messages,
    float? Temperature = null,
    int? MaxTokens = null);

public sealed record LlmResponse(string Text, int InputTokens, int OutputTokens, string FinishReason = "stop");

public interface ILlmGateway
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}
```

Es la **única** cosa que el workflow necesita saber del LLM: "mandame estos
mensajes y devolveme un texto". Ni HTTP, ni proveedores, ni autenticación.

## 6.2 Los adaptadores

### `FakeLlmGateway` — el LLM falso (¡la pieza clave del curso!)

Implementa la misma interfaz pero **no llama a ninguna API**: es determinista.
Dos modos:

- **`scripted`**: consume una cola de respuestas pre-cargadas. Ideal para
  tests donde controlás EXACTAMENTE qué devuelve cada llamada.
- **`smart`**: descompone cualquier objetivo en 2 sub-objetivos hasta alcanzar
  `MaxDepth` y luego responde. Simula un LLM "razonador" sin gastar tokens.

Para distinguir el rol de cada llamada, los agentes marcan su system prompt con
`[PLANNER]`, `[WORKER]` o `[SYNTHESIZER]` (mirá los prompts en
`WorkflowManifest`).

**Por qué importa**: podés correr TODO el curso sin API key, sin internet y sin
gastar plata. Y los tests son deterministas (nunca dependen de lo que un LLM
real decida). Cuando querés ver el flujo con un LLM de verdad, cambiás una
config y nada más.

### `OpenAiCompatibleGateway` — el LLM real

Habla el protocolo `/chat/completions` de OpenAI, el **estándar de facto**:
DeepSeek, Ollama, Groq, vLLM, LM Studio y casi todos lo implementan. Cambiar de
proveedor es cambiar `BaseUrl` + `ApiKey` + `Model` en `appsettings.json`.

```csharp
var url = $"{_options.BaseUrl.TrimEnd('/')}/chat/completions";
// POST { model, messages, temperature, max_tokens, stream: false }
// → choices[0].message.content + usage.prompt_tokens/completion_tokens
```

## 6.3 Cómo se elige el adaptador (DI + config)

En `CursoAgentes.Infrastructure/DependencyInjection.cs`:

```csharp
services.AddHttpClient<OpenAiCompatibleGateway>(client =>
    client.Timeout = configuration.GetValue("Llm:Timeout", TimeSpan.FromMinutes(2)));

var provider = configuration.GetValue<string>("Llm:Provider") ?? "Fake";
if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
    services.AddSingleton<ILlmGateway, OpenAiCompatibleGateway>();
else
    services.AddSingleton<ILlmGateway, FakeLlmGateway>();
```

El motor pide `ILlmGateway` y el contenedor decide. **Cero cambios de código**
para pasar de simulación a producción.

## 6.4 Buenas prácticas de prompting en el ejemplo

- **System prompt por rol**: cada agente tiene el suyo (definidos en el
  manifiesto). El rol no se mezcla con el contenido.
- **Temperatura por tarea**: decisiones de estructura (planner) → 0.2 (más
  determinista); respuestas (worker) → 0.7; síntesis → 0.4.
- **JSON con contrato**: el planner pide JSON con forma fija
  (`{"isLeaf": bool, "subGoals": [string], "rationale": string}`) y lo valida.
- **Defensa contra LLM descarrilado**:
  - JSON inválido → el nodo se trata como hoja (nunca se rompe el flujo).
  - Respuesta vacía → mensaje por defecto, no crash.
  - `MaxChildrenPerNode` recorta sub-objetivos desmedidos.
- **Telemetría mínima**: los gateways loguean el rol, el modelo y los tokens.
  En producción agregarías métricas reales (OpenTelemetry, costos por llamada).

## 6.5 El test del adaptador real (sin red)

`OpenAiCompatibleGatewayTests` usa un `HttpMessageHandler` falso que devuelve
un JSON de `/chat/completions` armado a mano, y verifica: que el body salió
bien formado, que el header `Authorization: Bearer` se mandó, y que el parseo
de `choices[0].message.content` + `usage` funciona. O sea: probamos el
adaptador **sin tocar internet**.

---

## 📖 En el ejemplo

- Puerto + modelos: `02-ejemplo/src/CursoAgentes.Engine/Llm/ILlmGateway.cs`
- LLM falso (scripted + smart): `02-ejemplo/src/CursoAgentes.Engine/Llm/FakeLlmGateway.cs`
- Gateway real: `02-ejemplo/src/CursoAgentes.Infrastructure/Llm/OpenAiCompatibleGateway.cs`
- Opciones: `02-ejemplo/src/CursoAgentes.Infrastructure/Llm/LlmGatewayOptions.cs`
- Test del adaptador con HTTP stub: `02-ejemplo/tests/CursoAgentes.Tests/OpenAiCompatibleGatewayTests.cs`
- Config: `02-ejemplo/src/CursoAgentes.App/appsettings.json` (sección `Llm`)
