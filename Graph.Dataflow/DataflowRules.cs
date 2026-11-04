namespace MASS4.Graph.Dataflow;

/// <summary>Rejects data edges that close a cycle, naming every edge of it; edges of other semantics are ignored.</summary>
/// <param name="semantics">Decides which ports carry data.</param>
sealed class DataflowAcyclicRule(DataflowSemantics semantics) : IGraphRule
{
    public void Validate(GraphValidationContext context)
    {
        var graph = context.Graph;
        if (context.Changes is null)
        {
            foreach (var cycle in FindCycles(graph))
            {
                context.Report(Cycle(cycle));
            }
            return;
        }
        foreach (var edge in context.AffectedEdges)
        {
            if (semantics.IsDataEdge(graph, edge, out var source, out var target) &&
                PathEdges(graph, target.NodeId, source.NodeId) is { } path)
            {
                context.Report(Cycle([edge, .. path]));
            }
        }
    }

    /// <summary>Reports the closing edge as the element and the whole cycle, in flow order, as related elements.</summary>
    static Diagnostic Cycle(List<GraphEdge> cycle) =>
        new(DataflowDiagnosticIds.DataCycle, DiagnosticSeverity.Error, cycle[0].Id,
            $"This data edge closes a cycle of {cycle.Count} edge(s); dataflow values cannot depend on themselves.")
        {
            Related = [.. cycle.Select(static edge => edge.Id)],
        };

    /// <summary>Shortest data path between two nodes, or null when <paramref name="to"/> is unreachable.</summary>
    List<GraphEdge>? PathEdges(GraphDocument graph, ElementId from, ElementId to)
    {
        Dictionary<ElementId, GraphEdge?> via = new() { [from] = null };
        Queue<ElementId> pending = new([from]);
        while (pending.TryDequeue(out var current))
        {
            if (current == to)
            {
                List<GraphEdge> path = [];
                while (via[current] is { } step)
                {
                    path.Add(step);
                    current = graph.GetPort(step.Source).NodeId;
                }
                path.Reverse();
                return path;
            }
            foreach (var edge in OutgoingData(graph, current))
            {
                var next = graph.GetPort(edge.Target).NodeId;
                if (via.TryAdd(next, edge))
                {
                    pending.Enqueue(next);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Depth-first search over data edges; every edge into a node still on the path closes a cycle, which is that edge
    /// followed by the path edges from the revisited node.
    /// </summary>
    List<List<GraphEdge>> FindCycles(GraphDocument graph)
    {
        List<List<GraphEdge>> cycles = [];
        Dictionary<ElementId, bool> onPath = [];
        foreach (var root in graph.Nodes.Select(static node => node.Id).Order())
        {
            if (!onPath.ContainsKey(root))
            {
                Visit(graph, root, onPath, cycles);
            }
        }
        return cycles;
    }

    void Visit(GraphDocument graph, ElementId root, Dictionary<ElementId, bool> onPath, List<List<GraphEdge>> cycles)
    {
        Stack<(ElementId Node, IEnumerator<GraphEdge> Edges)> path = new();
        onPath[root] = true;
        path.Push((root, OutgoingData(graph, root).GetEnumerator()));
        while (path.TryPeek(out var frame))
        {
            if (!frame.Edges.MoveNext())
            {
                onPath[frame.Node] = false;
                path.Pop();
                continue;
            }
            var edge = frame.Edges.Current;
            var next = graph.GetPort(edge.Target).NodeId;
            if (!onPath.TryGetValue(next, out var active))
            {
                onPath[next] = true;
                path.Push((next, OutgoingData(graph, next).GetEnumerator()));
            }
            else if (active)
            {
                cycles.Add(CycleOnPath(path, edge, next));
            }
        }
    }

    /// <summary>The closing edge, then each path edge from <paramref name="start"/> back to the closing edge's source.</summary>
    static List<GraphEdge> CycleOnPath(Stack<(ElementId Node, IEnumerator<GraphEdge> Edges)> path, GraphEdge closing,
        ElementId start)
    {
        List<GraphEdge> steps = [];
        if (path.Peek().Node != start)
        {
            // Below the top, each frame's current edge leads into the frame above it.
            foreach (var (node, edges) in path.Skip(1))
            {
                steps.Add(edges.Current);
                if (node == start)
                {
                    break;
                }
            }
            steps.Reverse();
        }
        return [closing, .. steps];
    }

    IEnumerable<GraphEdge> OutgoingData(GraphDocument graph, ElementId node) =>
        graph.GetNode(node).Ports
            .Where(static port => port.Direction == PortDirection.Output)
            .SelectMany(port => graph.GetEdges(port.Id).OrderBy(static edge => edge.Id))
            .Where(edge => semantics.IsDataEdge(graph, edge, out _, out _));
}

/// <summary>Rejects data inputs with more than one edge; dataflow inputs read exactly one source.</summary>
/// <param name="semantics">Decides which ports carry data.</param>
sealed class DataflowInputRule(DataflowSemantics semantics) : IGraphRule
{
    public void Validate(GraphValidationContext context)
    {
        var graph = context.Graph;
        HashSet<ElementId> reported = [];
        foreach (var edge in context.AffectedEdges)
        {
            if (semantics.IsDataEdge(graph, edge, out _, out var target) && graph.GetEdges(target.Id).Count > 1 &&
                reported.Add(target.Id))
            {
                context.Report(new(DataflowDiagnosticIds.MultipleDataEdges, DiagnosticSeverity.Error, target.Id,
                    $"Data input '{target.Declaration.Name}' accepts a single edge."));
            }
        }
    }
}

/// <summary>
/// Reports wildcard type conflicts on every change, and wildcard nodes without a concrete type on full validation.
/// </summary>
/// <param name="semantics">Decides which ports carry data.</param>
sealed class DataflowInferenceRule(DataflowSemantics semantics) : IGraphRule
{
    public void Validate(GraphValidationContext context)
    {
        foreach (var diagnostic in TypeInference.Run(semantics, context.Graph).Diagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error || context.Changes is null)
            {
                context.Report(diagnostic);
            }
        }
    }
}
