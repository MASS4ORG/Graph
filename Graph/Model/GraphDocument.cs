namespace MASS4.Graph;

/// <summary>
/// A graph asset: nodes, ports, edges, blackboard variables, groups and view layout. It holds no instance state, so any number
/// of runtime instances can share one document. All mutations go through <see cref="GraphEditor"/> commands.
/// </summary>
public sealed class GraphDocument
{
    static readonly GraphEdge[] NoEdges = [];

    readonly Dictionary<ElementId, GraphNode> nodes = [];
    readonly Dictionary<ElementId, GraphPort> ports = [];
    readonly Dictionary<ElementId, GraphEdge> edges = [];
    readonly Dictionary<ElementId, GraphVariable> variables = [];
    readonly Dictionary<ElementId, List<GraphEdge>> edgesByPort = [];
    readonly Dictionary<ElementId, GraphPoint> positions = [];
    readonly Dictionary<ElementId, GraphGroup> groups = [];
    readonly Dictionary<ElementId, ElementId> nodeGroups = [];
    readonly HashSet<ElementId> collapsed = [];
    List<GraphChange>? recording;

    /// <summary>Creates an empty document.</summary>
    /// <param name="registry">Identities the document resolves against.</param>
    /// <param name="assetId">Persistent identity of the asset; a new one is generated when empty.</param>
    public GraphDocument(GraphTypeRegistry registry, StableId assetId = default)
    {
        Registry = registry;
        AssetId = assetId.IsEmpty ? StableId.New() : assetId;
    }

    /// <summary>Raised after a command, undo or redo commits, with the element changes it made.</summary>
    public event Action<GraphDocument, IReadOnlyList<GraphChange>>? Changed;

    /// <summary>Persistent identity of the asset; subgraph references use it.</summary>
    public StableId AssetId { get; }

    /// <summary>Identities the document resolves against.</summary>
    public GraphTypeRegistry Registry { get; }

    /// <summary>All nodes, in no particular order.</summary>
    public IReadOnlyCollection<GraphNode> Nodes => nodes.Values;

    /// <summary>All edges, in no particular order.</summary>
    public IReadOnlyCollection<GraphEdge> Edges => edges.Values;

    /// <summary>All blackboard variables, in no particular order.</summary>
    public IReadOnlyCollection<GraphVariable> Variables => variables.Values;

    /// <summary>Node positions of the default view; non-semantic metadata.</summary>
    public IReadOnlyDictionary<ElementId, GraphPoint> Positions => positions;

    /// <summary>All groups, in no particular order; authored content that never affects validation or evaluation.</summary>
    public IReadOnlyCollection<GraphGroup> Groups => groups.Values;

    /// <summary>
    /// Groups drawn collapsed in the default view. Collapse is view state like <see cref="Positions"/>: it is saved
    /// with the layout, can be omitted with it, and never changes the groups themselves.
    /// </summary>
    public IReadOnlySet<ElementId> CollapsedGroups => collapsed;

    /// <summary>Saved top-level properties this version does not understand, written back verbatim.</summary>
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; internal set; } = GraphNode.EmptyExtensions;

    /// <summary>Finds a node.</summary>
    public bool TryGetNode(ElementId id, [NotNullWhen(true)] out GraphNode? node) => nodes.TryGetValue(id, out node);

    /// <summary>Finds a node or throws.</summary>
    /// <exception cref="KeyNotFoundException">The document has no such node.</exception>
    public GraphNode GetNode(ElementId id) =>
        nodes.TryGetValue(id, out var node) ? node : throw new KeyNotFoundException($"Node {id} does not exist.");

    /// <summary>Finds a port on any node or throws.</summary>
    /// <exception cref="KeyNotFoundException">The document has no such port.</exception>
    public GraphPort GetPort(ElementId id) =>
        ports.TryGetValue(id, out var port) ? port : throw new KeyNotFoundException($"Port {id} does not exist.");

    /// <summary>Finds a port on any node.</summary>
    public bool TryGetPort(ElementId id, [NotNullWhen(true)] out GraphPort? port) => ports.TryGetValue(id, out port);

    /// <summary>Finds an edge.</summary>
    public bool TryGetEdge(ElementId id, [NotNullWhen(true)] out GraphEdge? edge) => edges.TryGetValue(id, out edge);

    /// <summary>Finds a group.</summary>
    public bool TryGetGroup(ElementId id, [NotNullWhen(true)] out GraphGroup? group) => groups.TryGetValue(id, out group);

    /// <summary>The group a node is directly in, or null when it is in none.</summary>
    public ElementId? GroupOf(ElementId nodeId) => nodeGroups.TryGetValue(nodeId, out var group) ? group : null;

    /// <summary>Groups whose parent is <paramref name="parent"/>; null lists the top-level groups.</summary>
    public IEnumerable<GraphGroup> ChildGroups(ElementId? parent) => groups.Values.Where(group => group.Parent == parent);

    /// <summary>
    /// The outermost collapsed group enclosing a node or group, which a view draws in its place; null when it is
    /// visible. A collapsed group is itself visible unless one of its ancestors is collapsed.
    /// </summary>
    public ElementId? CollapsedContainer(ElementId element)
    {
        ElementId? outermost = null;
        var current = groups.TryGetValue(element, out var group) ? group.Parent : GroupOf(element);
        while (current is { } id && groups.TryGetValue(id, out var ancestor))
        {
            if (collapsed.Contains(id))
            {
                outermost = id;
            }
            current = ancestor.Parent;
        }
        return outermost;
    }

    /// <summary>Whether <paramref name="ancestor"/> encloses <paramref name="group"/> or is the group itself.</summary>
    public bool IsWithin(ElementId group, ElementId ancestor)
    {
        for (ElementId? current = group; current is { } id; current = groups.TryGetValue(id, out var g) ? g.Parent : null)
        {
            if (id == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Finds a blackboard variable.</summary>
    public bool TryGetVariable(ElementId id, [NotNullWhen(true)] out GraphVariable? variable) =>
        variables.TryGetValue(id, out variable);

    /// <summary>Edges attached to a port, in attachment order.</summary>
    public IReadOnlyList<GraphEdge> GetEdges(ElementId portId) =>
        edgesByPort.TryGetValue(portId, out var list) ? list : NoEdges;

    /// <summary>Whether any element of any kind uses this identity.</summary>
    public bool ContainsElement(ElementId id) =>
        nodes.ContainsKey(id) || ports.ContainsKey(id) || edges.ContainsKey(id) || variables.ContainsKey(id) ||
        groups.ContainsKey(id);

    /// <summary>Resolves the node type of a node, following renames; false for preserved unknown types.</summary>
    public bool TryGetNodeType(GraphNode node, [NotNullWhen(true)] out NodeType? type) =>
        Registry.NodeTypes.TryResolve(node.TypeId, out type);

    internal void BeginRecording() => recording = [];

    internal List<GraphChange> EndRecording()
    {
        var changes = recording ?? [];
        recording = null;
        return changes;
    }

    internal void RaiseChanged(IReadOnlyList<GraphChange> changes)
    {
        if (changes.Count > 0)
        {
            Changed?.Invoke(this, changes);
        }
    }

    internal void AddNodeCore(GraphNode node)
    {
        nodes.Add(node.Id, node);
        foreach (var port in node.Ports)
        {
            ports.Add(port.Id, port);
        }
        Record(GraphChangeKind.NodeAdded, node.Id);
    }

    internal void RemoveNodeCore(GraphNode node)
    {
        foreach (var port in node.Ports)
        {
            ports.Remove(port.Id);
            edgesByPort.Remove(port.Id);
        }
        nodes.Remove(node.Id);
        SetNodeGroupCore(node.Id, null);
        if (positions.Remove(node.Id))
        {
            Record(GraphChangeKind.LayoutChanged, node.Id);
        }
        Record(GraphChangeKind.NodeRemoved, node.Id);
    }

    internal void AddEdgeCore(GraphEdge edge)
    {
        edges.Add(edge.Id, edge);
        Attach(edge.Source, edge);
        Attach(edge.Target, edge);
        Record(GraphChangeKind.EdgeAdded, edge.Id);
    }

    internal void RemoveEdgeCore(GraphEdge edge)
    {
        edges.Remove(edge.Id);
        Detach(edge.Source, edge);
        Detach(edge.Target, edge);
        Record(GraphChangeKind.EdgeRemoved, edge.Id);
    }

    internal void SetPayloadCore(GraphNode node, object? payload)
    {
        node.Payload = payload;
        Record(GraphChangeKind.PayloadChanged, node.Id);
    }

    internal void SetAnnotationCore(GraphNode node, StableId type, object? value)
    {
        node.SetAnnotation(type, value);
        Record(GraphChangeKind.AnnotationChanged, node.Id);
    }

    internal void SetVariableCore(GraphVariable variable)
    {
        var existed = variables.ContainsKey(variable.Id);
        variables[variable.Id] = variable;
        Record(existed ? GraphChangeKind.VariableChanged : GraphChangeKind.VariableAdded, variable.Id);
    }

    internal void RemoveVariableCore(ElementId id)
    {
        variables.Remove(id);
        Record(GraphChangeKind.VariableRemoved, id);
    }

    internal void SetPositionCore(ElementId nodeId, GraphPoint? position)
    {
        if (position is { } value)
        {
            positions[nodeId] = value;
        }
        else
        {
            positions.Remove(nodeId);
        }
        Record(GraphChangeKind.LayoutChanged, nodeId);
    }

    internal void AddGroupCore(GraphGroup group)
    {
        groups.Add(group.Id, group);
        Record(GraphChangeKind.GroupAdded, group.Id);
    }

    /// <summary>Removes an empty group; callers first move its nodes and child groups elsewhere.</summary>
    internal void RemoveGroupCore(GraphGroup group)
    {
        groups.Remove(group.Id);
        SetCollapsedCore(group.Id, false);
        Record(GraphChangeKind.GroupRemoved, group.Id);
    }

    internal void SetGroupTitleCore(GraphGroup group, string title)
    {
        group.Title = title;
        Record(GraphChangeKind.GroupChanged, group.Id);
    }

    internal void SetGroupParentCore(GraphGroup group, ElementId? parent)
    {
        group.Parent = parent;
        Record(GraphChangeKind.GroupChanged, group.Id);
    }

    internal void SetNodeGroupCore(ElementId nodeId, ElementId? groupId)
    {
        if (nodeGroups.Remove(nodeId, out var previous))
        {
            groups[previous].RemoveNode(nodeId);
            Record(GraphChangeKind.GroupChanged, previous);
        }
        if (groupId is { } id)
        {
            groups[id].AddNode(nodeId);
            nodeGroups[nodeId] = id;
            Record(GraphChangeKind.GroupChanged, id);
        }
    }

    internal void SetCollapsedCore(ElementId groupId, bool value)
    {
        if (value ? collapsed.Add(groupId) : collapsed.Remove(groupId))
        {
            Record(GraphChangeKind.LayoutChanged, groupId);
        }
    }

    void Attach(ElementId portId, GraphEdge edge)
    {
        if (!edgesByPort.TryGetValue(portId, out var list))
        {
            list = [];
            edgesByPort.Add(portId, list);
        }
        list.Add(edge);
    }

    void Detach(ElementId portId, GraphEdge edge)
    {
        if (edgesByPort.TryGetValue(portId, out var list))
        {
            list.Remove(edge);
            if (list.Count == 0)
            {
                edgesByPort.Remove(portId);
            }
        }
    }

    void Record(GraphChangeKind kind, ElementId id) => recording?.Add(new(kind, id));
}
