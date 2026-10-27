namespace MASS4.Graph;

/// <summary>What a rule may inspect and where it reports.</summary>
public sealed class GraphValidationContext
{
    readonly DiagnosticBag diagnostics;

    internal GraphValidationContext(GraphDocument graph, IReadOnlyList<GraphChange>? changes, DiagnosticBag diagnostics)
    {
        Graph = graph;
        Changes = changes;
        this.diagnostics = diagnostics;
    }

    /// <summary>The document being validated.</summary>
    public GraphDocument Graph { get; }

    /// <summary>Changes being validated, or null when the whole document is validated.</summary>
    public IReadOnlyList<GraphChange>? Changes { get; }

    /// <summary>Nodes added or changed (every node for full validation); removed nodes are skipped.</summary>
    public IEnumerable<GraphNode> AffectedNodes =>
        Changes is null
            ? Graph.Nodes
            : Affected<GraphNode>(GraphChangeKind.NodeAdded, GraphChangeKind.PayloadChanged, Graph.TryGetNode);

    /// <summary>Edges added (every edge for full validation).</summary>
    public IEnumerable<GraphEdge> AffectedEdges =>
        Changes is null
            ? Graph.Edges
            : Affected<GraphEdge>(GraphChangeKind.EdgeAdded, GraphChangeKind.EdgeAdded, Graph.TryGetEdge);

    /// <summary>Variables added or changed (every variable for full validation).</summary>
    public IEnumerable<GraphVariable> AffectedVariables =>
        Changes is null
            ? Graph.Variables
            : Affected<GraphVariable>(
                GraphChangeKind.VariableAdded, GraphChangeKind.VariableChanged, Graph.TryGetVariable);

    /// <summary>Reports a finding; any error rolls the validated mutation back.</summary>
    public void Report(Diagnostic diagnostic) => diagnostics.Add(diagnostic);

    delegate bool Lookup<T>(ElementId id, [NotNullWhen(true)] out T? value);

    IEnumerable<T> Affected<T>(GraphChangeKind first, GraphChangeKind second, Lookup<T> lookup)
    {
        HashSet<ElementId> seen = [];
        foreach (var change in Changes!)
        {
            if ((change.Kind == first || change.Kind == second) && seen.Add(change.ElementId) &&
                lookup(change.ElementId, out var value))
            {
                yield return value;
            }
        }
    }
}

/// <summary>A validation rule; semantics packages contribute their own (for example acyclicity for dataflow).</summary>
public interface IGraphRule
{
    /// <summary>Inspects the affected elements and reports diagnostics.</summary>
    void Validate(GraphValidationContext context);
}

/// <summary>Runs rules against a whole document, for tools and CI.</summary>
public static class GraphValidator
{
    /// <summary>Validates every element of the document.</summary>
    public static IReadOnlyList<Diagnostic> Validate(GraphDocument graph, IEnumerable<IGraphRule> rules)
    {
        var diagnostics = new DiagnosticBag();
        Run(graph, null, rules, diagnostics);
        return [.. diagnostics];
    }

    internal static void Run(
        GraphDocument graph,
        IReadOnlyList<GraphChange>? changes,
        IEnumerable<IGraphRule> rules,
        DiagnosticBag diagnostics)
    {
        var context = new GraphValidationContext(graph, changes, diagnostics);
        foreach (var rule in rules)
        {
            rule.Validate(context);
        }
    }
}

/// <summary>Warns about nodes and variables whose types are not registered; such elements are preserved.</summary>
public sealed class KnownTypesRule : IGraphRule
{
    /// <summary>Shared instance.</summary>
    public static KnownTypesRule Instance { get; } = new();

    /// <inheritdoc />
    public void Validate(GraphValidationContext context)
    {
        var registry = context.Graph.Registry;
        foreach (var node in context.AffectedNodes)
        {
            if (!registry.NodeTypes.TryResolve(node.TypeId, out _))
            {
                context.Report(new(GraphDiagnosticIds.UnknownNodeType, DiagnosticSeverity.Warning, node.Id,
                    $"Node type {node.TypeId} is not registered; the node is preserved but cannot be evaluated."));
            }
        }
        foreach (var variable in context.AffectedVariables)
        {
            if (!registry.PortTypes.TryResolve(variable.TypeId, out _))
            {
                context.Report(new(GraphDiagnosticIds.UnknownPortType, DiagnosticSeverity.Warning, variable.Id,
                    $"Variable '{variable.Name}' has unregistered type {variable.TypeId}."));
            }
        }
    }
}

/// <summary>Rejects edges whose port types the configured strategy does not allow to connect directly.</summary>
/// <param name="compatibility">The strategy selected by the graph's semantics.</param>
public sealed class PortCompatibilityRule(IPortCompatibility compatibility) : IGraphRule
{
    /// <inheritdoc />
    public void Validate(GraphValidationContext context)
    {
        var graph = context.Graph;
        foreach (var edge in context.AffectedEdges)
        {
            if (graph.TryGetPort(edge.Source, out var source) && graph.TryGetPort(edge.Target, out var target) &&
                Check(graph.Registry.PortTypes, edge, source, target) is { } diagnostic)
            {
                context.Report(diagnostic);
            }
        }
    }

    Diagnostic? Check(IdentityRegistry<PortType> types, GraphEdge edge, GraphPort source, GraphPort target)
    {
        if (!types.TryResolve(source.TypeId, out var from) || !types.TryResolve(target.TypeId, out var to))
        {
            return new(GraphDiagnosticIds.UnknownPortType, DiagnosticSeverity.Warning, edge.Id,
                $"Cannot check '{source.Declaration.Name}' → '{target.Declaration.Name}': a type is not registered.");
        }
        var result = compatibility.Check(from, to);
        return result.Kind switch
        {
            PortCompatibilityKind.RequiresConversion => new(GraphDiagnosticIds.ConversionRequired,
                DiagnosticSeverity.Error, edge.Id,
                $"'{from.DisplayName}' → '{to.DisplayName}' needs a '{result.Conversion?.DisplayName}' conversion."),
            PortCompatibilityKind.Incompatible => new(GraphDiagnosticIds.IncompatiblePortTypes,
                DiagnosticSeverity.Error, edge.Id, $"'{from.DisplayName}' cannot connect to '{to.DisplayName}'."),
            _ => null,
        };
    }
}
