using CursoAgentes.Engine.Agents;
using CursoAgentes.Engine.Llm;
using Microsoft.Extensions.Logging;

namespace CursoAgentes.Engine.Workflow;

// ─────────────────────────────────────────────────────────────────────────────
// WorkerAgent — el agente que responde los objetivos HOJA. Es la llamada más
// simple del sistema: un prompt de sistema + el objetivo → un texto.
// Es el "operario" del workflow: hace el trabajo fino que el planner decidió
// que no hacía falta dividir.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class WorkerAgent : AgentBase
{
    private readonly ILlmGateway _llm;
    private readonly WorkflowManifest _manifest;
    private readonly ILogger<WorkerAgent> _logger;

    public WorkerAgent(ILlmGateway llm, WorkflowManifest manifest, ILogger<WorkerAgent> logger)
    {
        _llm = llm;
        _manifest = manifest;
        _logger = logger;
    }

    public override string AgentId => "worker";
    public override string AgentName => "Investigador";
    public override AgentRole Role => AgentRole.Worker;

    public override async Task<string?> ExecuteAsync(AgentContext context, CancellationToken ct)
        => await AnswerAsync(context, ct);

    public async Task<string> AnswerAsync(AgentContext context, CancellationToken ct)
    {
        var request = new LlmRequest(
            _manifest.WorkerSystemPrompt,
            new[] { new LlmMessage("user", context.Goal) },
            Temperature: 0.7f,
            MaxTokens: 1024);

        var response = await _llm.CompleteAsync(request, ct);

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            _logger.LogWarning("[Worker] {Node} devolvió una respuesta vacía.", context.NodeId);
            return "(el investigador no produjo respuesta)";
        }

        return response.Text.Trim();
    }
}
