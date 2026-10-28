namespace MASS4.Graph.Dataflow;

/// <summary>
/// The executable dataflow of one document. Values are pulled: reading a port evaluates the nodes it depends on,
/// once per evaluation for volatile dependencies and once per instance otherwise. Slots live in the shared instance
/// layout, so one program serves every instance; evaluating different instances concurrently is safe.
/// </summary>
public sealed class DataflowProgram : IDataPlane
{
    const int StaticEpoch = -1;

    readonly NodeEntry[] nodes;
    readonly PortEntry[] ports;
    readonly int[] sources;
    readonly Dictionary<ElementId, int> portIndex;
    readonly Dictionary<ElementId, int> nodeIndex;
    readonly InstanceSlot<int> epochCounter;
    readonly InstanceArraySlot<int> epochs;

    internal DataflowProgram(
        GraphDocument document,
        NodeEntry[] nodes,
        PortEntry[] ports,
        int[] sources,
        InstanceSlot<int> epochCounter,
        InstanceArraySlot<int> epochs)
    {
        Document = document;
        this.nodes = nodes;
        this.ports = ports;
        this.sources = sources;
        this.epochCounter = epochCounter;
        this.epochs = epochs;
        nodeIndex = new(nodes.Length);
        portIndex = new(ports.Length);
        for (var i = 0; i < nodes.Length; i++)
        {
            nodeIndex.Add(nodes[i].Node.Id, i);
            for (var p = 0; p < nodes[i].Node.Ports.Count; p++)
            {
                portIndex.Add(nodes[i].Node.Ports[p].Id, nodes[i].FirstPort + p);
            }
        }
    }

    /// <summary>The document this program was compiled from.</summary>
    public GraphDocument Document { get; }

    /// <inheritdoc />
    public bool TryResolve(ElementId portId, out int port) =>
        portIndex.TryGetValue(portId, out port) && ports[port].Location is not null;

    /// <summary>Whether a node is computed once per instance because nothing it depends on is volatile.</summary>
    /// <exception cref="KeyNotFoundException">The node is not part of the document.</exception>
    public bool IsCached(ElementId nodeId) => nodes[nodeIndex[nodeId]].IsStatic;

    /// <summary>The concrete data type of a port after wildcard inference, or null for ports without data.</summary>
    public StableId? ResolvedType(ElementId portId) =>
        portIndex.TryGetValue(portId, out var port) ? ports[port].Location?.Type.PortTypeId : null;

    /// <inheritdoc />
    public void BeginEvaluation(InstanceBuffer instances, InstanceHandle instance)
    {
        ref var counter = ref instances.Get(instance, epochCounter);
        if (counter >= int.MaxValue - 1)
        {
            instances.GetSpan(instance, epochs).Clear();
            counter = 0;
        }
        else
        {
            counter++;
        }
    }

    /// <summary>Forgets every cached value of an instance, for example after its parameters change.</summary>
    public void Invalidate(InstanceBuffer instances, InstanceHandle instance) =>
        instances.GetSpan(instance, epochs).Clear();

    /// <summary>Returns the current value of a port, evaluating whatever it depends on.</summary>
    /// <exception cref="KeyNotFoundException">The port carries no data.</exception>
    public T Read<T>(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance, ElementId portId)
        where T : unmanaged => Read<T>(context, instances, instance, Resolve(portId));

    /// <inheritdoc />
    public T Read<T>(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance, int port)
        where T : unmanaged
    {
        Prepare(context, instances, instance, port);
        return ReadPort<T>(instances, instance, port);
    }

    /// <inheritdoc />
    public T? ReadReference<T>(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance, int port)
        where T : class
    {
        Prepare(context, instances, instance, port);
        return LocationOf(port).ReadReference<T>(instances, instance);
    }

    /// <inheritdoc />
    public void Write<T>(InstanceBuffer instances, InstanceHandle instance, int port, T value) where T : unmanaged
    {
        LocationOf(port).Write(instances, instance, value);
        BeginEvaluation(instances, instance);
    }

    /// <inheritdoc />
    public void WriteReference<T>(InstanceBuffer instances, InstanceHandle instance, int port, T? value)
        where T : class
    {
        LocationOf(port).WriteReference(instances, instance, value);
        BeginEvaluation(instances, instance);
    }

    internal InstanceSlot<int> EpochCounter => epochCounter;

    internal GraphNode NodeAt(int node) => nodes[node].Node;

    internal bool IsConnected(int node, int port) => ports[nodes[node].FirstPort + port].SourceNode >= 0;

    internal ValueLocation Location(int node, int port) => LocationOf(nodes[node].FirstPort + port);

    internal T ReadLocal<T>(InstanceBuffer instances, InstanceHandle instance, int node, int port)
        where T : unmanaged => ReadPort<T>(instances, instance, nodes[node].FirstPort + port);

    internal T? ReadLocalReference<T>(InstanceBuffer instances, InstanceHandle instance, int node, int port)
        where T : class => Location(node, port).ReadReference<T>(instances, instance);

    internal void CopyLocal(InstanceBuffer instances, InstanceHandle instance, int node, int from, int to)
    {
        var entry = ports[nodes[node].FirstPort + from];
        var source = LocationOf(nodes[node].FirstPort + from);
        var target = LocationOf(nodes[node].FirstPort + to);
        if (source.Type.Storage != target.Type.Storage || source.Type.ClrType != target.Type.ClrType)
        {
            throw new InvalidOperationException(
                $"Cannot copy {source.Type.ClrType.Name} values into a {target.Type.ClrType.Name} port.");
        }
        if (entry.Coercion is { } coercion)
        {
            coercion.Apply(instances, instance, entry.CoercionSource, source.Offset);
        }
        source.Type.Copy(instances, instance, source.Offset, target.Offset);
    }

    int Resolve(ElementId portId) =>
        TryResolve(portId, out var port) ? port : throw new KeyNotFoundException($"Port {portId} carries no data.");

    ValueLocation LocationOf(int port) =>
        ports[port].Location ?? throw new InvalidOperationException("The port carries no data.");

    T ReadPort<T>(InstanceBuffer instances, InstanceHandle instance, int port) where T : unmanaged
    {
        var entry = ports[port];
        var location = entry.Location ?? throw new InvalidOperationException("The port carries no data.");
        if (entry.Coercion is { } coercion)
        {
            coercion.Apply(instances, instance, entry.CoercionSource, location.Offset);
        }
        return location.Read<T>(instances, instance);
    }

    void Prepare(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance, int port)
    {
        var entry = ports[port];
        var owner = entry.IsOutput ? entry.Node : entry.SourceNode;
        if (owner >= 0)
        {
            EnsureFresh(context, instances, instance, owner);
        }
    }

    bool IsFresh(InstanceBuffer instances, InstanceHandle instance, int node, int current)
    {
        if (nodes[node].Behavior is null)
        {
            return true;
        }
        var epoch = instances.GetSpan(instance, epochs)[node];
        return epoch == StaticEpoch || epoch == current;
    }

    /// <summary>Evaluates a node after its stale sources, depth first, with an explicit stack.</summary>
    void EnsureFresh(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance, int node)
    {
        var current = instances.Get(instance, epochCounter) + 1;
        if (IsFresh(instances, instance, node, current))
        {
            return;
        }
        var stack = ArrayPool<int>.Shared.Rent(sources.Length + 1);
        try
        {
            var count = 0;
            stack[count++] = node << 1;
            while (count > 0)
            {
                var top = stack[count - 1];
                var index = top >> 1;
                if (IsFresh(instances, instance, index, current))
                {
                    count--;
                }
                else if ((top & 1) == 0)
                {
                    stack[count - 1] = top | 1;
                    count = PushStaleSources(instances, instance, index, current, stack, count);
                }
                else
                {
                    count--;
                    Evaluate(context, instances, instance, index, current);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(stack);
        }
    }

    int PushStaleSources(
        InstanceBuffer instances,
        InstanceHandle instance,
        int node,
        int current,
        int[] stack,
        int count)
    {
        var entry = nodes[node];
        for (var s = 0; s < entry.SourceCount; s++)
        {
            var source = sources[entry.FirstSource + s];
            if (!IsFresh(instances, instance, source, current))
            {
                stack[count++] = source << 1;
            }
        }
        return count;
    }

    void Evaluate(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance, int node, int current)
    {
        var entry = nodes[node];
        entry.Behavior!.Evaluate(new(this, context, instances, instance, node));
        instances.GetSpan(instance, epochs)[node] = entry.IsStatic ? StaticEpoch : current;
    }
}

/// <summary>One node of a compiled program; a null behavior marks values written by other semantics.</summary>
readonly record struct NodeEntry(
    GraphNode Node,
    IDataflowNode? Behavior,
    int FirstPort,
    int FirstSource,
    int SourceCount,
    bool IsStatic);

/// <summary>One port of a compiled program; a null location marks ports without data.</summary>
readonly record struct PortEntry(
    int Node,
    bool IsOutput,
    ValueLocation? Location,
    int SourceNode,
    Coercion? Coercion,
    int CoercionSource);
