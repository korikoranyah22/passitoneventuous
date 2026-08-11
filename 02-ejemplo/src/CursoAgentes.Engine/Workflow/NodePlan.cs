namespace CursoAgentes.Engine.Workflow;

// ─────────────────────────────────────────────────────────────────────────────
// El resultado de la fase de PLANIFICACIÓN de un nodo: ¿el objetivo se divide
// (subGoals) o se responde directo (isLeaf)? El rationale es la justificación
// que da el LLM — se persiste en el evento WorkflowNodePlanned, así el árbol
// queda auditable: no solo QUÉ se decidió, sino POR QUÉ.
// ─────────────────────────────────────────────────────────────────────────────

public sealed record NodePlan(bool IsLeaf, IReadOnlyList<string> SubGoals, string Rationale)
{
    public static NodePlan Leaf(string rationale) => new(true, [], rationale);

    public static NodePlan Decompose(IReadOnlyList<string> subGoals, string rationale) =>
        new(false, subGoals, rationale);
}
