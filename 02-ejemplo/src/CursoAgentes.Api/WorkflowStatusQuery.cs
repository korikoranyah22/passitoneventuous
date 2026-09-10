using CursoAgentes.Engine.Workflow;
using CursoAgentes.Infrastructure.Projections;

namespace CursoAgentes.Api;

public sealed class WorkflowStatusQuery(
    WorkflowExecutionReader executionReader,
    WorkflowReadModelStore readModel,
    WorkflowRunQueue queue)
{
    public async Task<WorkflowStatusResponse?> GetAsync(
        string runId,
        CancellationToken ct)
    {
        var source = await executionReader.ReadRunAsync(runId, ct);
        if (source is null) return null;

        var projected = await readModel.GetRunDetailsAsync(runId, ct);
        var nodes = await readModel.GetNodeDetailsAsync(runId, ct);
        var root = WorkflowTreeBuilder.Build(source.RootNodeId, nodes);

        return new WorkflowStatusResponse(
            source.RunId,
            source.Goal,
            source.RootNodeId,
            source.Status.ToString(),
            source.Answer,
            source.ExecutionRequested,
            queue.IsScheduled(runId),
            projected?.Status,
            root is not null,
            root);
    }
}

public static class WorkflowTreeBuilder
{
    public static WorkflowNodeResponse? Build(
        string rootNodeId,
        IReadOnlyList<WorkflowNodeReadModel> nodes)
    {
        var root = nodes.FirstOrDefault(node => node.NodeId == rootNodeId);
        if (root is null) return null;

        var byParent = nodes
            .Where(node => node.ParentNodeId is not null)
            .GroupBy(node => node.ParentNodeId!)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(node => node.Order).ToArray());
        return BuildTree(root, byParent, []);
    }

    private static WorkflowNodeResponse BuildTree(
        WorkflowNodeReadModel node,
        IReadOnlyDictionary<string, WorkflowNodeReadModel[]> byParent,
        HashSet<string> path)
    {
        if (!path.Add(node.NodeId))
            throw new InvalidOperationException($"Cycle detected at workflow node '{node.NodeId}'.");
        var children = byParent.TryGetValue(node.NodeId, out var rows)
            ? rows.Select(child => BuildTree(child, byParent, path)).ToArray()
            : [];
        path.Remove(node.NodeId);
        return new WorkflowNodeResponse(
            node.NodeId,
            node.Goal,
            node.Depth,
            node.Order,
            node.Status,
            node.IsLeaf,
            node.Rationale,
            node.Answer,
            children);
    }
}
