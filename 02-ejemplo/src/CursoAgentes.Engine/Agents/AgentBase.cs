namespace CursoAgentes.Engine.Agents;

// ─────────────────────────────────────────────────────────────────────────────
// Concepto: la unidad mínima de un sistema de agentes. Un agente tiene una
// identidad (AgentId/AgentName), un rol y un método de ejecución que recibe el
// contexto y devuelve una respuesta (o null si no pudo producir una).
//
// Este es el "esqueleto" que en arquitecturas reales de agentes se combina con
// un orquestador: el orquestador decide QUÉ agente corre y CUÁNDO; el agente
// solo sabe hacer SU parte. Los tres agentes concretos de este curso
// (PlannerAgent, WorkerAgent, SynthesizerAgent) heredan de acá.
// ─────────────────────────────────────────────────────────────────────────────

public abstract class AgentBase
{
    /// <summary>Identidad estable del agente (para logs y telemetría).</summary>
    public abstract string AgentId { get; }

    /// <summary>Nombre legible del agente.</summary>
    public abstract string AgentName { get; }

    /// <summary>Rol dentro del workflow (Planner | Worker | Synthesizer).</summary>
    public abstract AgentRole Role { get; }

    /// <summary>Ejecuta el trabajo del agente para el nodo descrito por el contexto.</summary>
    /// <returns>La respuesta producida, o null si el agente no pudo producir una.</returns>
    public abstract Task<string?> ExecuteAsync(AgentContext context, CancellationToken ct);
}
