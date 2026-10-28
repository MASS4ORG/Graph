namespace MASS4.Graph.Dataflow;

/// <summary>Rejects data edges that close a cycle; edges of other semantics are ignored.</summary>
/// <param name="semantics">Decides which ports carry data.</param>
sealed class DataflowAcyclicRule(DataflowSemantics semantics) : IGraphRule
{
    public void Validate(GraphValidationContext context)
    {
        var graph = context.Graph;
        if (context.Changes is null)
        {
            foreach (var edge in FindBackEdges(graph))
            {
                context.Report(Cycle(edge));
            }
            return;
        }
        foreach (var edge in context.AffectedEdges)
        {
            if (semantics.IsDataEdge(graph, edge, out var source, out var target) &&
                Reaches(graph, target.NodeId, source.NodeId))
            {
                context.Report(Cycle(edge));
            }
        }
    }

    static Diagnostic Cycle(GraphEdge edge) =>
        new(DataflowDiagnosticIds.DataCycle, DiagnosticSeverity.Error, edge.Id,
            "This data edge closes a cycle; dataflow values cannot depend on themselves.");

    bool Reaches(GraphDocument graph, ElementId from, ElementId to)
    {
        HashSet<ElementId> seen = [from];
        Stack<ElementId> pending = new([from]);
        while (pending.TryPop(out var current))
        {
            if (current == to)
            {
                return true;
            }
            foreach (var next in OutgoingData(graph, current).Select(edge => graph.GetPort(edge.Target).NodeId))
            {
                if (seen.Add(next))
                {
                    pending.Push(next);
                }
            }
        }
        return false;
    }

    /// <summary>Depth-first search over data edges; every edge into a node still on the path closes a cycle.</summary>
    List<GraphEdge> FindBackEdges(GraphDocument graph)
    {
        List<GraphEdge> back = [];
        Dictionary<ElementId, bool> onPath = [];
        foreach (var root in graph.Nodes.Select(static node => node.Id).Order())
        {
            if (!onPath.ContainsKey(root))
            {
                Visit(graph, root, onPath, back);
            }
        }
        return back;
    }

    void Visit(GraphDocument graph, ElementId root, Dictionary<ElementId, bool> onPath, List<GraphEdge> back)
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
                back.Add(edge);
            }
        }
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
