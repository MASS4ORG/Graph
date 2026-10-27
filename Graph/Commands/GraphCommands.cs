namespace MASS4.Graph;

/// <summary>
/// A reversible document mutation. <see cref="Apply"/> either succeeds or reports diagnostics and leaves the document
/// unchanged; <see cref="Revert"/> undoes a successful apply. Domain commands compose the built-in primitives through
/// <see cref="CompositeCommand"/>.
/// </summary>
public interface IGraphCommand
{
    /// <summary>Label for undo history.</summary>
    string Description { get; }

    /// <summary>Applies the mutation; returns false, after reporting why, when it cannot.</summary>
    bool Apply(GraphDocument graph, DiagnosticBag diagnostics);

    /// <summary>Undoes a successful <see cref="Apply"/>.</summary>
    void Revert(GraphDocument graph);
}

/// <summary>Applies commands in order as one undoable step; a failing step reverts the earlier ones.</summary>
/// <param name="description">Label for undo history.</param>
/// <param name="commands">The steps, in application order.</param>
public sealed class CompositeCommand(string description, IReadOnlyList<IGraphCommand> commands) : IGraphCommand
{
    /// <inheritdoc />
    public string Description { get; } = description;

    /// <summary>The steps, in application order.</summary>
    public IReadOnlyList<IGraphCommand> Commands { get; } = commands;

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        for (var i = 0; i < Commands.Count; i++)
        {
            if (!Commands[i].Apply(graph, diagnostics))
            {
                for (var j = i - 1; j >= 0; j--)
                {
                    Commands[j].Revert(graph);
                }
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph)
    {
        for (var i = Commands.Count - 1; i >= 0; i--)
        {
            Commands[i].Revert(graph);
        }
    }
}

/// <summary>Adds a node of a registered type; port identities are fixed at construction so redo is stable.</summary>
public sealed class AddNodeCommand : IGraphCommand
{
    readonly ElementId[] portIds;

    /// <summary>Prepares the node.</summary>
    /// <param name="type">The node type; it must be registered in the target document.</param>
    /// <param name="payload">Initial application data; the type's default payload when null.</param>
    /// <param name="nodeId">Identity to use; a new one is generated when empty.</param>
    public AddNodeCommand(NodeType type, object? payload = null, ElementId nodeId = default)
    {
        Type = type;
        Payload = payload ?? type.DefaultPayload?.Invoke();
        NodeId = nodeId.IsEmpty ? ElementId.New() : nodeId;
        portIds = new ElementId[type.Ports.Count];
        for (var i = 0; i < portIds.Length; i++)
        {
            portIds[i] = ElementId.New();
        }
    }

    /// <inheritdoc />
    public string Description => $"Add {Type.DisplayName}";

    /// <summary>The node type.</summary>
    public NodeType Type { get; }

    /// <summary>Initial application data.</summary>
    public object? Payload { get; }

    /// <summary>
    /// Initial annotations keyed by registered annotation type; checked like <see cref="SetAnnotationCommand"/>.
    /// </summary>
    public IReadOnlyDictionary<StableId, object> Annotations { get; init; } = new Dictionary<StableId, object>();

    /// <summary>Identity of the node to add.</summary>
    public ElementId NodeId { get; }

    /// <summary>Identity the port declared with <paramref name="key"/> will have.</summary>
    /// <exception cref="KeyNotFoundException">The type declares no such port.</exception>
    public ElementId PortId(StableId key)
    {
        for (var i = 0; i < portIds.Length; i++)
        {
            if (Type.Ports[i].Key == key)
            {
                return portIds[i];
            }
        }
        throw new KeyNotFoundException($"'{Type.DisplayName}' declares no port {key}.");
    }

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        if (!graph.Registry.NodeTypes.TryResolve(Type.Id, out var registered) || registered != Type)
        {
            diagnostics.Error(GraphDiagnosticIds.NodeTypeNotRegistered, NodeId,
                $"Node type '{Type.DisplayName}' ({Type.Id}) is not registered in this document.");
            return false;
        }
        if (graph.ContainsElement(NodeId) || portIds.Any(graph.ContainsElement))
        {
            diagnostics.Error(GraphDiagnosticIds.DuplicateElement, NodeId,
                "The node or one of its ports already exists.");
            return false;
        }
        if (!TryResolveAnnotations(graph, diagnostics, out var annotations))
        {
            return false;
        }
        var ports = new GraphPort[portIds.Length];
        for (var i = 0; i < ports.Length; i++)
        {
            ports[i] = new(portIds[i], NodeId, Type.Ports[i]);
        }
        graph.AddNodeCore(new(NodeId, Type.Id, Type.Version, ports, Payload, annotations));
        return true;
    }

    bool TryResolveAnnotations(
        GraphDocument graph,
        DiagnosticBag diagnostics,
        out Dictionary<StableId, object> annotations)
    {
        annotations = [];
        foreach (var (type, value) in Annotations)
        {
            if (AnnotationRules.Check(graph.Registry, NodeId, type, value, out var canonical) is { } problem)
            {
                diagnostics.Add(problem);
                return false;
            }
            annotations[canonical] = value;
        }
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph)
    {
        if (graph.TryGetNode(NodeId, out var node))
        {
            graph.RemoveNodeCore(node);
        }
    }
}

/// <summary>Removes a node together with its edges and layout position.</summary>
/// <param name="nodeId">The node to remove.</param>
public sealed class RemoveNodeCommand(ElementId nodeId) : IGraphCommand
{
    GraphNode? removed;
    GraphEdge[] removedEdges = [];
    GraphPoint? removedPosition;

    /// <inheritdoc />
    public string Description => "Remove node";

    /// <summary>The node to remove.</summary>
    public ElementId NodeId { get; } = nodeId;

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        if (!graph.TryGetNode(NodeId, out var node))
        {
            diagnostics.Error(GraphDiagnosticIds.NodeNotFound, NodeId, "The node does not exist.");
            return false;
        }
        removed = node;
        removedEdges = [.. node.Ports.SelectMany(port => graph.GetEdges(port.Id)).Distinct()];
        removedPosition = graph.Positions.TryGetValue(NodeId, out var position) ? position : null;
        foreach (var edge in removedEdges)
        {
            graph.RemoveEdgeCore(edge);
        }
        graph.RemoveNodeCore(node);
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph)
    {
        if (removed is null)
        {
            return;
        }
        graph.AddNodeCore(removed);
        foreach (var edge in removedEdges)
        {
            graph.AddEdgeCore(edge);
        }
        if (removedPosition is { } position)
        {
            graph.SetPositionCore(NodeId, position);
        }
    }
}

/// <summary>Connects an output port to an input port, enforcing direction, uniqueness and multiplicity.</summary>
/// <param name="source">The output port.</param>
/// <param name="target">The input port.</param>
/// <param name="edgeId">Identity to use; a new one is generated when empty.</param>
public sealed class ConnectCommand(ElementId source, ElementId target, ElementId edgeId = default) : IGraphCommand
{
    /// <inheritdoc />
    public string Description => "Connect";

    /// <summary>The edge this command adds.</summary>
    public GraphEdge Edge { get; } = new(edgeId.IsEmpty ? ElementId.New() : edgeId, source, target);

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        var error = CheckPorts(graph, out var from, out var to) ?? CheckEdges(graph, from!, to!);
        if (error is not null)
        {
            diagnostics.Add(error);
            return false;
        }
        graph.AddEdgeCore(Edge);
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph)
    {
        if (graph.TryGetEdge(Edge.Id, out var edge))
        {
            graph.RemoveEdgeCore(edge);
        }
    }

    Diagnostic? CheckPorts(GraphDocument graph, out GraphPort? from, out GraphPort? to)
    {
        to = null;
        if (!graph.TryGetPort(Edge.Source, out from))
        {
            return Error(GraphDiagnosticIds.PortNotFound, Edge.Source, "The source port does not exist.");
        }
        if (!graph.TryGetPort(Edge.Target, out to))
        {
            return Error(GraphDiagnosticIds.PortNotFound, Edge.Target, "The target port does not exist.");
        }
        return from.Direction == PortDirection.Output && to.Direction == PortDirection.Input
            ? null
            : Error(GraphDiagnosticIds.DirectionMismatch, Edge.Id,
                $"Edges run from an output to an input; "
                + $"'{from.Declaration.Name}' → '{to.Declaration.Name}' does not.");
    }

    Diagnostic? CheckEdges(GraphDocument graph, GraphPort from, GraphPort to)
    {
        if (graph.ContainsElement(Edge.Id))
        {
            return Error(GraphDiagnosticIds.DuplicateElement, Edge.Id, "The edge identity is already used.");
        }
        var existing = graph.GetEdges(Edge.Target);
        if (existing.Any(edge => edge.Source == Edge.Source))
        {
            return Error(GraphDiagnosticIds.DuplicateEdge, Edge.Target,
                $"'{from.Declaration.Name}' is already connected to '{to.Declaration.Name}'.");
        }
        var crowded = Exceeds(to, existing) ? to : Exceeds(from, graph.GetEdges(Edge.Source)) ? from : null;
        return crowded is null
            ? null
            : Error(GraphDiagnosticIds.MultiplicityExceeded, crowded.Id,
                $"'{crowded.Declaration.Name}' accepts a single connection and is already connected.");
    }

    static Diagnostic Error(string id, ElementId element, string message) =>
        new(id, DiagnosticSeverity.Error, element, message);

    static bool Exceeds(GraphPort port, IReadOnlyList<GraphEdge> edges) =>
        port.Declaration.Multiplicity == PortMultiplicity.Single && edges.Count > 0;
}

/// <summary>Removes an edge.</summary>
/// <param name="edgeId">The edge to remove.</param>
public sealed class DisconnectCommand(ElementId edgeId) : IGraphCommand
{
    GraphEdge? removed;

    /// <inheritdoc />
    public string Description => "Disconnect";

    /// <summary>The edge to remove.</summary>
    public ElementId EdgeId { get; } = edgeId;

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        if (!graph.TryGetEdge(EdgeId, out removed))
        {
            diagnostics.Error(GraphDiagnosticIds.EdgeNotFound, EdgeId, "The edge does not exist.");
            return false;
        }
        graph.RemoveEdgeCore(removed);
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph)
    {
        if (removed is not null)
        {
            graph.AddEdgeCore(removed);
        }
    }
}
