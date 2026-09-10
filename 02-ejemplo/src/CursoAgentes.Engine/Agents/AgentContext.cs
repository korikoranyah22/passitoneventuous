namespace CursoAgentes.Engine.Agents;

// ─────────────────────────────────────────────────────────────────────────────
// Concepto: el CONTEXTO del agente. Es el "maletín" que viaja por el workflow:
// quién es el run, qué nodo estamos ejecutando, con qué objetivo, a qué
// profundidad, y qué sabemos hasta ahora (respuestas de los hijos + bitácora).
//
// En arquitecturas de agentes con pipeline (como la que inspira este curso)
// cada etapa RECIBE el contexto, lo enriquece y lo pasa a la siguiente. Acá el
// contexto se re-crea por nodo (cada ejecución recursiva arma el suyo) y se
// usa para darle al LLM todo lo que necesita saber en cada llamada.
// ─────────────────────────────────────────────────────────────────────────────

public sealed record AgentContext
{
    /// <summary>Id del run (stream workflow-run-{id}).</summary>
    public required string RunId { get; init; }

    /// <summary>Id del nodo actual (stream workflow-node-{id}).</summary>
    public required string NodeId { get; init; }

    /// <summary>El objetivo que este nodo debe resolver.</summary>
    public required string Goal { get; init; }

    /// <summary>Profundidad del nodo en el árbol (raíz = 0).</summary>
    public int Depth { get; init; }

    /// <summary>Tope de profundidad configurado (corta la recursión).</summary>
    public int MaxDepth { get; init; }

    /// <summary>Respuestas de los hijos (nodeId → respuesta), para la síntesis.</summary>
    public IReadOnlyDictionary<string, string> ChildAnswers { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Bitácora legible del run (la imprime la demo).</summary>
    public List<string> Steps { get; init; } = [];

    /// <summary>Metadatos libres (telemetría, tags, lo que cada workflow necesite).</summary>
    public Dictionary<string, object> Metadata { get; } = [];
}
