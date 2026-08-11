namespace CursoAgentes.Engine.Workflow;

// ─────────────────────────────────────────────────────────────────────────────
// Concepto: un WORKFLOW es DATA, no código. El mismo motor recursivo corre
// cualquier workflow si le cambiás este manifiesto: tope de profundidad, máx.
// de hijos por nodo y los prompts de cada rol. Esto es lo que en frameworks de
// agentes se llama "workflow declarativo": importar un workflow nuevo = traer
// un manifiesto, no escribir código nuevo.
//
// En la app se bindea desde la sección "Workflow" de appsettings.json.
// ─────────────────────────────────────────────────────────────────────────────

public sealed record WorkflowManifest
{
    /// <summary>Tope de recursión: el árbol nunca supera esta profundidad (raíz = 0).</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>Máximo de hijos que un nodo puede crear (protege contra explosión).</summary>
    public int MaxChildrenPerNode { get; init; } = 3;

    /// <summary>System prompt del rol que decide dividir o responder directo.</summary>
    public string PlannerSystemPrompt { get; init; } =
        "[PLANNER] Sos el planificador de un workflow de investigación recursiva. " +
        "Decidí si el objetivo se responde directamente o necesita dividirse en sub-objetivos. " +
        "Respondé SOLO JSON con esta forma: {\"isLeaf\": bool, \"subGoals\": [string], \"rationale\": string}.";

    /// <summary>System prompt del rol que responde objetivos hoja.</summary>
    public string WorkerSystemPrompt { get; init; } =
        "[WORKER] Sos un investigador. Respondé el objetivo con precisión y concisión.";

    /// <summary>System prompt del rol que integra las respuestas de los hijos.</summary>
    public string SynthesizerSystemPrompt { get; init; } =
        "[SYNTHESIZER] Sos el sintetizador. Integrá las respuestas parciales en una respuesta única, " +
        "coherente y sin repeticiones para el objetivo original.";

    /// <summary>Modelo a pedir al proveedor (lo usa el gateway real; el fake lo ignora).</summary>
    public string? Model { get; init; }
}
