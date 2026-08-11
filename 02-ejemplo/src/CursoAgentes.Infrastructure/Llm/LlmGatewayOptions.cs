namespace CursoAgentes.Infrastructure.Llm;

// ─────────────────────────────────────────────────────────────────────────────
// Opciones del gateway LLM real (OpenAI-compatible). Se bindean desde la
// sección "Llm" de appsettings.json. Cualquier servidor que hable el protocolo
// /chat/completions de OpenAI sirve: OpenAI, DeepSeek, Groq, Ollama (localhost),
// LM Studio, vLLM, etc. Por eso el ejemplo no depende de UN proveedor.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class LlmGatewayOptions
{
    /// <summary>Base URL del endpoint compatible (sin "/chat/completions").</summary>
    public string BaseUrl { get; init; } = "http://localhost:11434/v1";

    /// <summary>API key (vacía para Ollama/LM Studio locales).</summary>
    public string? ApiKey { get; init; }

    /// <summary>Modelo a usar (p.ej. "deepseek-chat", "gpt-4o-mini", "llama3.2").</summary>
    public string Model { get; init; } = "llama3.2";

    /// <summary>Tiempo máximo por llamada.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}
