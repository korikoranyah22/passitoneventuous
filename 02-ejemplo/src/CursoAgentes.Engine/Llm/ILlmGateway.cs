namespace CursoAgentes.Engine.Llm;

// ─────────────────────────────────────────────────────────────────────────────
// Modelo de datos de las llamadas a LLM. Es un "puerto" (abstracción): el motor
// del workflow habla con ESTA interfaz y no conoce proveedores concretos
// (OpenAI, DeepSeek, Ollama, Anthropic...). Los proveedores son "adaptadores"
// que implementan ILlmGateway (ver CursoAgentes.Infrastructure.Llm y el Fake).
//
// Este es el mismo patrón de puertos y adaptadores que usan los frameworks de
// agentes: el workflow depende de la abstracción, y el contenedor de DI decide
// qué adaptador concreto se enchufa (real o fake).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Un mensaje de la conversación que se le manda al LLM.</summary>
public sealed record LlmMessage(string Role, string Content);

/// <summary>Una llamada de completado: system prompt + historial + parámetros.</summary>
public sealed record LlmRequest(
    string SystemPrompt,
    IReadOnlyList<LlmMessage> Messages,
    float? Temperature = null,
    int? MaxTokens = null);

/// <summary>La respuesta del LLM, con métricas de uso.</summary>
public sealed record LlmResponse(
    string Text,
    int InputTokens,
    int OutputTokens,
    string FinishReason = "stop");

/// <summary>
/// Puerto de llamadas a LLM. Única cosa que el workflow necesita saber:
/// "mandame estos mensajes y devolveme un texto".
/// </summary>
public interface ILlmGateway
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}
