namespace CursoAgentes.Engine.Agents;

// ─────────────────────────────────────────────────────────────────────────────
// Concepto (inspirado en arquitecturas de agentes con pipeline): cada agente
// tiene UN rol bien definido dentro del workflow. El rol decide QUÉ hace con
// el LLM y QUÉ produce. Separar roles es lo que permite probar cada pieza por
// separado y reutilizarlos en workflows distintos.
// ─────────────────────────────────────────────────────────────────────────────

public enum AgentRole
{
    /// <summary>Decide si un objetivo se divide o se responde directo (planea el árbol).</summary>
    Planner,

    /// <summary>Responde un objetivo "hoja" (una llamada directa al LLM).</summary>
    Worker,

    /// <summary>Combina las respuestas de los hijos en la respuesta del padre.</summary>
    Synthesizer
}
