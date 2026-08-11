# Paso 6 · Gateway LLM: puerto + fake + adaptador real

## Objetivo

Definir el **puerto** `ILlmGateway` (la única cosa que el motor sabe de los
LLM) y dos **adaptadores**: uno falso (determinista, sin red) y uno real
(protocolo `/chat/completions` de OpenAI, compatible con DeepSeek, Ollama,
Groq, vLLM…).

## Concepto

El patrón **puerto/adaptador** (hexagonal): el motor depende de la
**abstracción**, y el contenedor de DI enchufa la **implementación** que diga
la config. Cambiar de LLM = cambiar config, no código. Y poder correr todo con
un **LLM falso** es lo que hace que el curso sea reproducible y los tests
deterministas.

## Código

### 1. El puerto (`src/CursoAgentes.Engine/Llm/ILlmGateway.cs`)

```csharp
public sealed record LlmMessage(string Role, string Content);

public sealed record LlmRequest(
    string SystemPrompt,
    IReadOnlyList<LlmMessage> Messages,
    float? Temperature = null,
    int? MaxTokens = null);

public sealed record LlmResponse(
    string Text, int InputTokens, int OutputTokens, string FinishReason = "stop");

public interface ILlmGateway
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}
```

### 2. El LLM falso (`src/CursoAgentes.Engine/Llm/FakeLlmGateway.cs`)

Dos modos:

- **`scripted`**: consume una cola de respuestas. Para tests donde controlás
  cada llamada (el orden real es depth-first: `planner → planner/hijo →
  worker/hijo → … → synthesizer`).
- **`smart`**: descompone cualquier objetivo en 2 sub-objetivos hasta alcanzar
  `MaxDepth`, después responde. Simula un LLM razonador sin IA.

Distingue el rol por el marcador del system prompt (`[PLANNER]`, `[WORKER]`,
`[SYNTHESIZER]`) que los agentes incluyen (definidos en `WorkflowManifest`).

```csharp
public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
{
    var role = DetectRole(request.SystemPrompt);

    if (_script is not null)
    {
        if (_script.TryDequeue(out var next))
            return Task.FromResult(new LlmResponse(next, 10, 10));
        return Task.FromResult(new LlmResponse(
            $"Respuesta genérica (fake, sin script) para «{Snippet(LastUserMessage(request))}»", 10, 10));
    }

    // Modo smart: comportamiento determinista según rol + profundidad del prompt.
    var (depth, maxDepth) = ParseDepth(LastUserMessage(request));
    return Task.FromResult(new LlmResponse(SmartText(request, depth, maxDepth), 10, 10));
}
```

### 3. El adaptador real (`src/CursoAgentes.Infrastructure/Llm/OpenAiCompatibleGateway.cs`)

```csharp
public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
{
    var url = $"{_options.BaseUrl.TrimEnd('/')}/chat/completions";

    var body = new
    {
        model = _options.Model,
        messages = BuildMessages(request),
        temperature = request.Temperature ?? 0.7f,
        max_tokens = request.MaxTokens ?? 2048,
        stream = false
    };

    using var content = new StringContent(
        JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");

    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
    if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

    using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
    // ... manejo de error HTTP, parseo de choices[0].message.content + usage ...
}
```

### 4. Opciones (`src/CursoAgentes.Infrastructure/Llm/LlmGatewayOptions.cs`)

```csharp
public sealed record LlmGatewayOptions
{
    public string BaseUrl { get; init; } = "http://localhost:11434/v1";  // Ollama local
    public string? ApiKey { get; init; }
    public string Model { get; init; } = "llama3.2";
}
```

### 5. Config (`src/CursoAgentes.App/appsettings.json`)

```jsonc
"Llm": {
  "Provider": "Fake",                    // "Fake" | "OpenAI"
  "BaseUrl": "http://localhost:11434/v1",
  "ApiKey": "",
  "Model": "llama3.2"
}
```

## Probalo

```bash
dotnet test --filter "FullyQualifiedName~OpenAiCompatibleGatewayTests"
```

El test del adaptador real usa un `HttpMessageHandler` falso (sin red):
verifica el body, el header `Authorization` y el parseo de la respuesta.

Para probar con un LLM real: poné `"Provider": "OpenAI"` y ajustá `BaseUrl` /
`ApiKey` / `Model` (Ollama local con `http://localhost:11434/v1` y modelo
descargado, o DeepSeek/Groq/vLLM con su URL compatible).

---

**Siguiente**: [Paso 7 · Motor recursivo](07-motor-recursivo.md)
