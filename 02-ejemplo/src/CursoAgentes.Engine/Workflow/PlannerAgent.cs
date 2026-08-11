using System.Text.Json;
using CursoAgentes.Engine.Agents;
using CursoAgentes.Engine.Llm;
using Microsoft.Extensions.Logging;

namespace CursoAgentes.Engine.Workflow;

// ─────────────────────────────────────────────────────────────────────────────
// PlannerAgent — el agente que DECIDE la forma del árbol. Para cada nodo le
// pregunta al LLM: "¿este objetivo se responde directo o hay que dividirlo?".
// El LLM responde JSON estructurado y el agente lo convierte en un NodePlan.
//
// Resiliencia: si el LLM devuelve algo que no es JSON válido (pasa seguido en
// producción), NO rompemos el workflow — tratamos el nodo como hoja. Una
// hoja de más es un resultado imperfecto; un crash por parseo es un fallo.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class PlannerAgent : AgentBase
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly ILlmGateway _llm;
    private readonly WorkflowManifest _manifest;
    private readonly ILogger<PlannerAgent> _logger;

    public PlannerAgent(ILlmGateway llm, WorkflowManifest manifest, ILogger<PlannerAgent> logger)
    {
        _llm = llm;
        _manifest = manifest;
        _logger = logger;
    }

    public override string AgentId => "planner";
    public override string AgentName => "Planificador";
    public override AgentRole Role => AgentRole.Planner;

    public override async Task<string?> ExecuteAsync(AgentContext context, CancellationToken ct)
        => (await PlanAsync(context, ct)).IsLeaf ? "hoja" : "dividir";

    public async Task<NodePlan> PlanAsync(AgentContext context, CancellationToken ct)
    {
        var request = new LlmRequest(
            _manifest.PlannerSystemPrompt,
            new[]
            {
                new LlmMessage("user",
                    $"Objetivo: {context.Goal}\n" +
                    $"Profundidad actual: {context.Depth} (máximo permitido: {context.MaxDepth})\n" +
                    "Respondé con el JSON indicado, sin texto adicional.")
            },
            Temperature: 0.2f,   // decisiones de estructura: baja temperatura = más determinismo
            MaxTokens: 500);

        var response = await _llm.CompleteAsync(request, ct);

        try
        {
            using var doc = JsonDocument.Parse(response.Text);
            var root = doc.RootElement;

            var isLeaf = root.TryGetProperty("isLeaf", out var leafProp) &&
                         leafProp.ValueKind == JsonValueKind.True;

            var subGoals = new List<string>();
            if (root.TryGetProperty("subGoals", out var subs) && subs.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in subs.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        subGoals.Add(item.GetString()!);
                }
            }

            var rationale = root.TryGetProperty("rationale", out var rat) &&
                            rat.ValueKind == JsonValueKind.String
                ? rat.GetString()!
                : "(sin justificación)";

            // Cortamos los sub-objetivos según MaxChildrenPerNode (protección).
            if (subGoals.Count > _manifest.MaxChildrenPerNode)
            {
                _logger.LogWarning(
                    "[Planner] {Node} pidió {N} hijos pero el máximo es {Max} — recortando.",
                    context.NodeId, subGoals.Count, _manifest.MaxChildrenPerNode);
                subGoals = subGoals.Take(_manifest.MaxChildrenPerNode).ToList();
            }

            return isLeaf || subGoals.Count == 0
                ? NodePlan.Leaf(rationale)
                : NodePlan.Decompose(subGoals, rationale);
        }
        catch (JsonException ex)
        {
            // Defensa en profundidad: JSON inválido → tratar como hoja.
            _logger.LogWarning(ex,
                "[Planner] {Node} devolvió JSON inválido — tratando el nodo como hoja.",
                context.NodeId);
            return NodePlan.Leaf("JSON inválido del planificador — hoja por seguridad.");
        }
    }
}
