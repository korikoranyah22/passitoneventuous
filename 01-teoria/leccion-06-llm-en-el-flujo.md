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
    services.AddSingleton<ILlmGateway>(sp =>
        new FakeLlmGateway(
            logger: sp.GetRequiredService<ILogger<FakeLlmGateway>>()));
```

El motor pide `ILlmGateway` y el contenedor decide. **Cero cambios de código**
para pasar de simulación a producción. La factory del fake es intencional:
evita que DI resuelva su script opcional como una colección vacía y active el
modo equivocado.

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

## 6.6 Un gateway no es un router

El ejemplo base registra un único `ILlmGateway` porque su objetivo es enseñar el
puerto/adaptador. En producción suelen convivir varias rutas:

- un modelo local para datos privados;
- uno rápido para clasificación o JSON;
- uno de razonamiento para planificación y crítica;
- una ruta alternativa cuando el proveedor principal está caído.

Elegir “el primer gateway registrado” o escribir el nombre del proveedor dentro
del agente mezcla responsabilidades. El agente debería pedir capacidades:

```text
required:  reasoning, structured-output
preferred: private, fast
excluded:  cloud
```

Un selector determinista filtra restricciones duras, puntúa preferencias y
devuelve una decisión explicable. Un ejecutor separado hace retries transitorios
sobre la misma ruta y vuelve a rutear cuando la ruta queda descartada.

Esta capacidad se implementa en MiyuAgents mediante `RouteRequest`,
`LlmRouteProfile`, `LlmGatewayRouter.SelectByTags` e `ILlmCallExecutor`. La
lección 8 muestra el caso completo.

> Diseño importante: la política específica —privacidad, proveedores
> permitidos, kill-switch y modelos disponibles— pertenece al host. El
> framework aporta contratos, selección, resiliencia y trazabilidad.

## 6.7 Proveedores reales incluidos en MiyuAgents

El gateway pequeño de este curso enseña el patrón. MiyuAgents agrega adapters
HTTP reutilizables para las tres familias de wire más comunes:

| Adapter | Proveedores |
|---|---|
| `OpenAiCompatibleGateway` | OpenAI, Azure OpenAI, DeepSeek, Groq, Mistral, OpenRouter, Ollama y servidores compatibles |
| `AnthropicGateway` | Messages API de Anthropic |
| `GeminiGateway` | `generateContent`, streaming y embeddings de Gemini |

Los nombres de modelo, claves y etiquetas de producto siguen perteneciendo al
host. Los adapters anuncian capacidades de protocolo —`chat`, `streaming`,
`tools`, `vision`, `embeddings`— pero no inventan políticas como
`approved-for-pii` a partir de la marca del proveedor.

Los tres traducen el mismo `LlmRequest`, preservan usage y herramientas, y
producen errores HTTP clasificables por `ILlmCallExecutor`. El ejemplo
[`real-providers`](../../angelnairav2_public/Packages/MiyuAgents/examples/real-providers/)
lee secretos y modelos desde variables de entorno. Incluye una configuración
actual de DeepSeek V4 y perfiles locales orientativos para Llama 3.2, Qwen 3,
DeepSeek R1, Qwen Coder y Gemma 3 sobre Ollama.

Las capacidades se declaran por ruta, no por marca: si un modelo local es
textual, `ExcludedBuiltInTags = ["vision"]` evita que el selector lo considere
para una tarea multimodal aunque el adapter OpenAI-compatible sepa serializar
imágenes.

---

## 📖 En el ejemplo

- Puerto + modelos: `02-ejemplo/src/CursoAgentes.Engine/Llm/ILlmGateway.cs`
- LLM falso (scripted + smart): `02-ejemplo/src/CursoAgentes.Engine/Llm/FakeLlmGateway.cs`
- Gateway real: `02-ejemplo/src/CursoAgentes.Infrastructure/Llm/OpenAiCompatibleGateway.cs`
- Opciones: `02-ejemplo/src/CursoAgentes.Infrastructure/Llm/LlmGatewayOptions.cs`
- Test del adaptador con HTTP stub: `02-ejemplo/tests/CursoAgentes.Tests/OpenAiCompatibleGatewayTests.cs`
- Config: `02-ejemplo/src/CursoAgentes.App/appsettings.json` (sección `Llm`)
- [Routing y resiliencia de producción](../../angelnairav2_public/Packages/MiyuAgents/docs/routing.md)
- [Configuración de proveedores reales](../../angelnairav2_public/Packages/MiyuAgents/docs/providers.md)
