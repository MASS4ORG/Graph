namespace MASS4.Graph.Dataflow;

/// <summary>
/// Resolves wildcard data ports to concrete port types. All wildcard ports of one node share one type variable, which
/// data edges bind to the concrete types they connect to, directly or through other wildcard nodes.
/// </summary>
sealed class TypeInference
{
    readonly Dictionary<ElementId, int> variables = [];
    readonly List<int> parents = [];
    readonly List<StableId?> bindings = [];

    TypeInference()
    {
    }

    /// <summary>Concrete types of the wildcard ports that could be inferred.</summary>
    public Dictionary<ElementId, StableId> Resolved { get; } = [];

    /// <summary>Conflicts (errors) and connected wildcard nodes without a concrete type (warnings).</summary>
    public List<Diagnostic> Diagnostics { get; } = [];

    public static TypeInference Run(DataflowSemantics semantics, GraphDocument document)
    {
        var inference = new TypeInference();
        foreach (var edge in document.Edges.OrderBy(static edge => edge.Id))
        {
            if (semantics.IsDataEdge(document, edge, out var source, out var target))
            {
                inference.Unify(inference.TermOf(document, source), inference.TermOf(document, target), edge.Id);
            }
        }
        inference.Resolve(document);
        return inference;
    }

    Term TermOf(GraphDocument document, GraphPort port) =>
        IsWildcard(document, port) ? new(VariableOf(port.NodeId), default) : new(-1, port.TypeId);

    int VariableOf(ElementId node)
    {
        if (!variables.TryGetValue(node, out var variable))
        {
            variable = parents.Count;
            variables.Add(node, variable);
            parents.Add(variable);
            bindings.Add(null);
        }
        return variable;
    }

    void Unify(Term a, Term b, ElementId edge)
    {
        if (a.Variable < 0 && b.Variable < 0)
        {
            return;
        }
        if (a.Variable >= 0 && b.Variable >= 0)
        {
            Merge(Find(a.Variable), Find(b.Variable), edge);
            return;
        }
        var (variable, concrete) = a.Variable >= 0 ? (a.Variable, b.Concrete) : (b.Variable, a.Concrete);
        Bind(Find(variable), concrete, edge);
    }

    void Merge(int a, int b, ElementId edge)
    {
        if (a == b)
        {
            return;
        }
        parents[b] = a;
        if (bindings[b] is { } bound)
        {
            Bind(a, bound, edge);
        }
    }

    void Bind(int root, StableId concrete, ElementId edge)
    {
        if (bindings[root] is not { } bound)
        {
            bindings[root] = concrete;
        }
        else if (bound != concrete)
        {
            Diagnostics.Add(new(DataflowDiagnosticIds.TypeConflict, DiagnosticSeverity.Error, edge,
                $"Wildcard ports would carry both {bound} and {concrete}."));
        }
    }

    int Find(int variable)
    {
        while (parents[variable] != variable)
        {
            parents[variable] = parents[parents[variable]];
            variable = parents[variable];
        }
        return variable;
    }

    void Resolve(GraphDocument document)
    {
        foreach (var (node, variable) in variables.OrderBy(static pair => pair.Key))
        {
            if (bindings[Find(variable)] is not { } concrete)
            {
                Diagnostics.Add(new(DataflowDiagnosticIds.TypeUnresolved, DiagnosticSeverity.Warning, node,
                    "No concrete type reaches this node's wildcard ports."));
                continue;
            }
            if (!document.TryGetNode(node, out var graphNode))
            {
                continue;
            }
            foreach (var port in graphNode.Ports.Where(port => IsWildcard(document, port)))
            {
                Resolved[port.Id] = concrete;
            }
        }
    }

    static bool IsWildcard(GraphDocument document, GraphPort port) =>
        document.Registry.PortTypes.TryResolve(port.TypeId, out var type) && type.IsWildcard;

    readonly record struct Term(int Variable, StableId Concrete);
}
