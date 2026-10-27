namespace MASS4.Graph;

/// <summary>A port instance on a node.</summary>
/// <param name="Id">Identity of the port within the document.</param>
/// <param name="NodeId">Identity of the owning node.</param>
/// <param name="Declaration">Key, name, direction, type and multiplicity.</param>
public sealed record GraphPort(ElementId Id, ElementId NodeId, PortDeclaration Declaration)
{
    /// <summary>Shortcut for the declared direction.</summary>
    public PortDirection Direction => Declaration.Direction;

    /// <summary>Shortcut for the declared port type identity.</summary>
    public StableId TypeId => Declaration.TypeId;
}

/// <summary>A directed connection from an output port to an input port.</summary>
/// <param name="Id">Identity of the edge within the document.</param>
/// <param name="Source">The output port.</param>
/// <param name="Target">The input port.</param>
public sealed record GraphEdge(ElementId Id, ElementId Source, ElementId Target);

/// <summary>Role of a blackboard variable.</summary>
public enum VariableScope
{
    /// <summary>Private to the graph.</summary>
    Local,

    /// <summary>Exposed parameter; on subgraphs it becomes an input port of the referencing node.</summary>
    Input,

    /// <summary>Exposed result; on subgraphs it becomes an output port of the referencing node.</summary>
    Output,

    /// <summary>Bound by the host to an external object or service at instantiation.</summary>
    External,
}

/// <summary>A typed entry of the graph blackboard; it reuses the port type system.</summary>
/// <param name="Id">Identity of the variable within the document.</param>
/// <param name="Name">Display name.</param>
/// <param name="TypeId">Identity of the value's <see cref="PortType"/>.</param>
/// <param name="Scope">Visibility and binding role.</param>
public sealed record GraphVariable(ElementId Id, string Name, StableId TypeId, VariableScope Scope)
{
    /// <summary>Initial value, written through the port type's serializer or kept opaque without one.</summary>
    public object? DefaultValue { get; init; }
}

/// <summary>Position of a node in a view; layout is persisted but never semantic.</summary>
/// <param name="X">Horizontal coordinate.</param>
/// <param name="Y">Vertical coordinate.</param>
public readonly record struct GraphPoint(double X, double Y);

/// <summary>
/// A saved value whose type the loader could not read (unregistered node type, missing serializer or unsupported
/// version). It is written back verbatim so that unknown content survives load and save.
/// </summary>
/// <param name="json">The saved JSON value; it is cloned so it outlives the source document.</param>
public sealed class UnknownPayload(JsonElement json)
{
    /// <summary>The saved JSON value.</summary>
    public JsonElement Json { get; } = json.Clone();
}

/// <summary>A node: a typed instance with ports and an application payload.</summary>
public sealed class GraphNode
{
    readonly GraphPort[] ports;

    internal GraphNode(ElementId id, StableId typeId, int typeVersion, GraphPort[] ports, object? payload)
    {
        Id = id;
        TypeId = typeId;
        TypeVersion = typeVersion;
        this.ports = ports;
        Payload = payload;
    }

    /// <summary>Identity of the node within the document.</summary>
    public ElementId Id { get; }

    /// <summary>Canonical identity of the node type; it may be unregistered for preserved unknown nodes.</summary>
    public StableId TypeId { get; }

    /// <summary>Payload schema version the payload conforms to.</summary>
    public int TypeVersion { get; }

    /// <summary>Ports in declaration order.</summary>
    public IReadOnlyList<GraphPort> Ports => ports;

    /// <summary>Application data; treat it as an immutable value and replace it with a command.</summary>
    public object? Payload { get; internal set; }

    /// <summary>Saved properties this version does not understand, written back verbatim.</summary>
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; internal init; } = EmptyExtensions;

    internal static readonly IReadOnlyDictionary<string, JsonElement> EmptyExtensions =
        new Dictionary<string, JsonElement>();

    /// <summary>Finds a port by its declaration key.</summary>
    public bool TryGetPort(StableId key, [NotNullWhen(true)] out GraphPort? port)
    {
        foreach (var candidate in ports)
        {
            if (candidate.Declaration.Key == key)
            {
                port = candidate;
                return true;
            }
        }
        port = null;
        return false;
    }

    /// <summary>Finds a port by its declaration key or throws.</summary>
    /// <exception cref="KeyNotFoundException">The node has no port with that key.</exception>
    public GraphPort GetPort(StableId key) =>
        TryGetPort(key, out var port) ? port : throw new KeyNotFoundException($"Node {Id} has no port {key}.");
}

/// <summary>Kind of a recorded document change.</summary>
public enum GraphChangeKind
{
    /// <summary>A node and its ports were added.</summary>
    NodeAdded,

    /// <summary>A node and its ports were removed.</summary>
    NodeRemoved,

    /// <summary>A node payload was replaced.</summary>
    PayloadChanged,

    /// <summary>An edge was added.</summary>
    EdgeAdded,

    /// <summary>An edge was removed.</summary>
    EdgeRemoved,

    /// <summary>A variable was added.</summary>
    VariableAdded,

    /// <summary>A variable was removed.</summary>
    VariableRemoved,

    /// <summary>A variable was replaced.</summary>
    VariableChanged,

    /// <summary>Non-semantic view metadata changed.</summary>
    LayoutChanged,
}

/// <summary>One element-level change, reported to listeners and to scoped validation rules.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="ElementId">The affected element.</param>
public readonly record struct GraphChange(GraphChangeKind Kind, ElementId ElementId);
