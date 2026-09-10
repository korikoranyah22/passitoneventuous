namespace CursoAgentes.Engine.Workflow;

// ─────────────────────────────────────────────────────────────────────────────
// El resultado del workflow como ÁRBOL. Se construye en memoria durante la
// ejecución (cada ExecuteNodeAsync devuelve un NodeResult) y se le devuelve al
// llamador para imprimirlo, guardarlo o analizarlo. La VERSIÓN DURADERA de
// este árbol está en el event store (los streams workflow-node-*): el NodeResult
// es solo la vista en vivo.
// ─────────────────────────────────────────────────────────────────────────────

public sealed record NodeResult(
    string NodeId,
    string Goal,
    int Depth,
    bool IsLeaf,
    string Answer,
    IReadOnlyList<NodeResult> Children,
    string Rationale);

public sealed record WorkflowResult(
    string RunId,
    string FinalAnswer,
    NodeResult Root,
    IReadOnlyList<string> Steps)
{
    /// <summary>Cantidad total de nodos del árbol.</summary>
    public int NodeCount => CountNodes(Root);

    private static int CountNodes(NodeResult node)
        => 1 + node.Children.Sum(CountNodes);
}

public sealed record WorkflowRunHandle(
    string RunId,
    string RootNodeId,
    string Goal);
