using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CursoAgentes.Engine.Llm;

// ─────────────────────────────────────────────────────────────────────────────
// EL LLM FALSO — la pieza más importante del curso para poder correr TODO sin
// gastar tokens ni depender de internet. Implementa la misma interfaz
// (ILlmGateway) que el gateway real, así el workflow no sabe (ni le importa)
// si está hablando con un LLM de verdad o con este simulador determinista.
//
// Dos modos:
//  • "scripted": consume una cola de respuestas pre-cargadas (ideal para tests
//    donde querés controlar EXACTAMENTE qué devuelve cada llamada).
//  • "smart": descompone cualquier objetivo en 2 sub-objetivos hasta alcanzar
//    MaxDepth y luego responde — simula un LLM "razonador" genérico sin IA.
//
// Para distinguir el rol de cada llamada, los agentes marcan su system prompt
// con [PLANNER], [WORKER] o [SYNTHESIZER] (ver los agentes concretos).
// ─────────────────────────────────────────────────────────────────────────────

public sealed class FakeLlmGateway : ILlmGateway
{
    public const string PlannerMarker = "[PLANNER]";
    public const string WorkerMarker = "[WORKER]";
    public const string SynthesizerMarker = "[SYNTHESIZER]";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly Queue<string>? _script;
    private readonly ILogger? _logger;

    /// <summary>Crea un gateway falso en modo "scripted".</summary>
    public FakeLlmGateway(IEnumerable<string>? script = null, ILogger? logger = null)
    {
        if (script is not null)
            _script = new Queue<string>(script);
        _logger = logger;
    }

    public bool IsScripted => _script is not null;

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var role = DetectRole(request.SystemPrompt);

        if (_script is not null)
        {
            if (_script.TryDequeue(out var next))
            {
                _logger?.LogInformation("[FakeLlm/scripted] {Role} → '{Next}'", role, Snippet(next));
                return Task.FromResult(new LlmResponse(next, 10, 10));
            }

            // La cola se acabó: responder como hoja genérica (no romper el flujo).
            var fallback = $"Respuesta genérica (fake, sin script) para «{Snippet(LastUserMessage(request))}»";
            _logger?.LogWarning("[FakeLlm/scripted] cola vacía para {Role} — usando fallback.", role);
            return Task.FromResult(new LlmResponse(fallback, 10, 10));
        }

        // ── Modo "smart": comportamiento determinista basado en el rol ──────
        // OJO: el planner y el synthesizer arman el mensaje de usuario con
        // "Objetivo: {goal}\n…instrucciones…"; el goal LIMPIO lo sacamos con
        // ExtractGoal. Si usáramos el mensaje completo, los sub-objetivos
        // arrastrarían texto del prompt (y el parseo de profundidad leería la
        // línea anidada equivocada → el fake nunca "vería" la profundidad real).
        var rawMessage = LastUserMessage(request);
        var (depth, maxDepth) = ParseDepth(rawMessage);
        var goal = ExtractGoal(rawMessage);

        string text;
        if (request.SystemPrompt.Contains(PlannerMarker, StringComparison.Ordinal))
            text = PlanJson(depth, maxDepth, goal);
        else if (request.SystemPrompt.Contains(SynthesizerMarker, StringComparison.Ordinal))
            text = $"Síntesis (fake) de «{goal}»: integro las sub-respuestas en una conclusión única.";
        else
            text = $"Respuesta (fake) a «{goal}»: este es un texto determinista generado sin llamar a ningún LLM real.";

        return Task.FromResult(new LlmResponse(text, 10, 10));
    }

    /// <summary>Genera el JSON de plan que el PlannerAgent espera parsear.</summary>
    private static string PlanJson(int depth, int maxDepth, string goal)
    {
        var isLeaf = depth >= maxDepth;
        var subGoals = isLeaf
            ? Array.Empty<string>()
            : new[]
            {
                $"Aspecto 1 de «{goal}»",
                $"Aspecto 2 de «{goal}»"
            };

        var plan = new
        {
            isLeaf,
            subGoals,
            rationale = "modo fake: divido en 2 aspectos hasta alcanzar MaxDepth"
        };
        return JsonSerializer.Serialize(plan, _json);
    }

    private static string DetectRole(string systemPrompt)
    {
        if (systemPrompt.Contains(PlannerMarker, StringComparison.Ordinal)) return "planner";
        if (systemPrompt.Contains(SynthesizerMarker, StringComparison.Ordinal)) return "synthesizer";
        return "worker";
    }

    private static string LastUserMessage(LlmRequest request)
    {
        for (var i = request.Messages.Count - 1; i >= 0; i--)
        {
            if (request.Messages[i].Role == "user")
                return request.Messages[i].Content;
        }
        return "(sin mensaje de usuario)";
    }

    /// <summary>
    /// Extrae el objetivo LIMPIO del mensaje de usuario. El planner y el
    /// synthesizer arman su mensaje como "Objetivo: {goal}\n…instrucciones…"
    /// y "Objetivo original: {goal}\n…". El fake trabaja con el goal pelado
    /// (si arrastra el prompt, los sub-objetivos se vuelven ilegibles y
    /// rompen el parseo de profundidad — ver el comentario en CompleteAsync).
    /// </summary>
    private static string ExtractGoal(string userMessage)
    {
        var match = Regex.Match(userMessage, @"^Objetivo(?: original)?:\s*(.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : userMessage;
    }

    /// <summary>Extrae "Profundidad actual: N (máximo permitido: M)" del prompt del planner.</summary>
    private static (int Depth, int MaxDepth) ParseDepth(string userMessage)
    {
        var match = Regex.Match(userMessage, @"Profundidad actual:\s*(\d+)\s*\(máximo permitido:\s*(\d+)\)");
        if (!match.Success) return (0, 0);
        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }

    private static string Snippet(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= 80 ? s : s[..80] + "…";
    }
}
