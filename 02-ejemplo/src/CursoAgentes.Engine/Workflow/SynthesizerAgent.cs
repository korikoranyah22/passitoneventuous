using System.Text;
using CursoAgentes.Engine.Agents;
using CursoAgentes.Engine.Llm;
using Microsoft.Extensions.Logging;

namespace CursoAgentes.Engine.Workflow;

// ─────────────────────────────────────────────────────────────────────────────
// SynthesizerAgent — el agente que integra. Cuando un nodo NO-hoja termina de
// ejecutar a todos sus hijos, este agente le pide al LLM que combine las
// respuestas parciales en UNA respuesta para el objetivo del nodo.
//
// La síntesis es lo que hace que el resultado final sea más que la suma de las
// partes: el LLM ve el objetivo original + todas las respuestas de los hijos y
// produce una conclusión única. Sin este paso, el workflow sería solo un árbol
// de respuestas sueltas sin conclusión.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SynthesizerAgent : AgentBase
{
    private readonly ILlmGateway _llm;
    private readonly WorkflowManifest _manifest;
    private readonly ILogger<SynthesizerAgent> _logger;

    public SynthesizerAgent(ILlmGateway llm, WorkflowManifest manifest, ILogger<SynthesizerAgent> logger)
    {
        _llm = llm;
        _manifest = manifest;
        _logger = logger;
    }

    public override string AgentId => "synthesizer";
    public override string AgentName => "Sintetizador";
    public override AgentRole Role => AgentRole.Synthesizer;

    public override async Task<string?> ExecuteAsync(AgentContext context, CancellationToken ct)
        => await SynthesizeAsync(context, ct);

    public async Task<string> SynthesizeAsync(AgentContext context, CancellationToken ct)
    {
        var childrenBlock = new StringBuilder();
        foreach (var (childId, answer) in context.ChildAnswers)
        {
            childrenBlock.AppendLine($"--- Respuesta del sub-objetivo {childId} ---");
            childrenBlock.AppendLine(answer);
            childrenBlock.AppendLine();
        }

        var request = new LlmRequest(
            _manifest.SynthesizerSystemPrompt,
            new[]
            {
                new LlmMessage("user",
                    $"Objetivo original: {context.Goal}\n\n" +
                    $"Sub-respuestas a integrar:\n{childrenBlock}\n" +
                    "Producí una única respuesta integrada.")
            },
            Temperature: 0.4f,
            MaxTokens: 1024);

        var response = await _llm.CompleteAsync(request, ct);

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            _logger.LogWarning("[Synthesizer] {Node} devolvió una síntesis vacía.", context.NodeId);
            return "(el sintetizador no produjo respuesta)";
        }

        return response.Text.Trim();
    }
}
