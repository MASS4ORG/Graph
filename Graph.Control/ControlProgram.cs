using System.Buffers;

namespace MASS4.Graph.Control;

/// <summary>
/// The executable control flow of one document. A run starts at an entry node and proceeds depth first: the
/// continuations of a node run in the order it requested them, and each continuation's branch finishes before the
/// next begins. One program serves every instance; runs of different instances may proceed concurrently.
/// </summary>
public sealed class ControlProgram
{
    readonly ControlSemantics semantics;
    readonly NodeEntry[] nodes;
    readonly PortEntry[] ports;
    readonly int[] targets;
    readonly int[] tickEntries;
    readonly Dictionary<ElementId, int> nodeIndex;
    readonly IDataPlane? data;

    ControlProgram(
        ControlSemantics semantics,
        NodeEntry[] nodes,
        PortEntry[] ports,
        int[] targets,
        IDataPlane? data)
    {
        this.semantics = semantics;
        this.nodes = nodes;
        this.ports = ports;
        this.targets = targets;
        this.data = data;
        nodeIndex = new(nodes.Length);
        List<int> entries = [];
        for (var i = 0; i < nodes.Length; i++)
        {
            nodeIndex.Add(nodes[i].Node.Id, i);
            if (nodes[i].Behavior is { IsTickEntry: true })
            {
                entries.Add(i);
            }
        }
        tickEntries = [.. entries];
        RequiredFeatures = [.. nodes.SelectMany(static node => node.Behavior?.RequiredFeatures ?? []).Distinct()];
    }

    /// <summary>Context features the document's control nodes read.</summary>
    public IReadOnlyList<Type> RequiredFeatures { get; }

    /// <summary>Runs every tick entry, in node identity order, after starting a new data evaluation.</summary>
    public ControlRunResult Tick(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance)
    {
        data?.BeginEvaluation(instances, instance);
        foreach (var entry in tickEntries)
        {
            var result = Execute(context, instances, instance, entry);
            if (result != ControlRunResult.Completed)
            {
                return result;
            }
        }
        return ControlRunResult.Completed;
    }

    /// <summary>Runs from one entry node, for example when the host raises the event it represents.</summary>
    /// <exception cref="KeyNotFoundException">The node is not part of the document.</exception>
    public ControlRunResult Run(
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle instance,
        ElementId entryNodeId)
    {
        var entry = nodeIndex[entryNodeId];
        data?.BeginEvaluation(instances, instance);
        return Execute(context, instances, instance, entry);
    }

    internal static ControlProgram Create(
        ControlSemantics semantics,
        GraphDocument document,
        InstanceLayoutBuilder layout,
        IDataPlane? data)
    {
        var graphNodes = document.Nodes.OrderBy(static node => node.Id).ToArray();
        var indexOf = graphNodes.Select(static (node, index) => (node.Id, index)).ToDictionary();
        var nodes = new NodeEntry[graphNodes.Length];
        List<PortEntry> ports = [];
        List<int> targets = [];
        for (var i = 0; i < graphNodes.Length; i++)
        {
            nodes[i] = CreateNode(semantics, graphNodes[i], layout, ports.Count);
            foreach (var port in graphNodes[i].Ports)
            {
                ports.Add(CreatePort(document, port, indexOf, targets, data));
            }
        }
        return new(semantics, nodes, [.. ports], [.. targets], data);
    }

    static NodeEntry CreateNode(ControlSemantics semantics, GraphNode node, InstanceLayoutBuilder layout, int firstPort)
    {
        semantics.TryGetBehavior(node.TypeId, out var behavior);
        var size = behavior?.MemorySize ?? 0;
        var memory = size > 0 ? layout.AddArray<long>((size + 7) / 8).Offset : 0;
        return new(node, behavior, firstPort, memory, size);
    }

    /// <summary>Lists exec targets in node identity order and resolves the data-plane index of a port.</summary>
    static PortEntry CreatePort(
        GraphDocument document,
        GraphPort port,
        Dictionary<ElementId, int> indexOf,
        List<int> targets,
        IDataPlane? data)
    {
        var first = targets.Count;
        var isExecOutput = ControlSemantics.IsExec(port) && port.Direction == PortDirection.Output;
        if (isExecOutput)
        {
            targets.AddRange(document.GetEdges(port.Id)
                .Select(edge => document.GetPort(edge.Target).NodeId)
                .Order()
                .Select(target => indexOf[target]));
        }
        var dataPort = data is not null && data.TryResolve(port.Id, out var resolved) ? resolved : -1;
        return new(isExecOutput, first, targets.Count - first, dataPort);
    }

    internal GraphNode NodeAt(int node) => nodes[node].Node;

    internal (int Offset, int Size) MemoryOf(int node) => (nodes[node].MemoryOffset, nodes[node].MemorySize);

    internal IDataPlane Data(int node, int port, out int index)
    {
        index = ports[nodes[node].FirstPort + port].DataPort;
        if (data is null || index < 0)
        {
            throw new InvalidOperationException("The port carries no data, or the program has no data plane.");
        }
        return data;
    }

    internal void CheckExecOutput(int node, int port)
    {
        if ((uint)port >= (uint)nodes[node].Node.Ports.Count || !ports[nodes[node].FirstPort + port].IsExecOutput)
        {
            throw new InvalidOperationException($"Port {port} of the node is not an exec output.");
        }
    }

    ControlRunResult Execute(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance, int start)
    {
        var stack = ArrayPool<int>.Shared.Rent(semantics.StackCapacity);
        var continuations = ArrayPool<int>.Shared.Rent(semantics.MaxContinuations);
        try
        {
            var count = 1;
            var steps = 0;
            stack[0] = start;
            while (count > 0)
            {
                if (++steps > semantics.MaxStepsPerRun)
                {
                    return ControlRunResult.StepLimitExceeded;
                }
                var node = stack[--count];
                if (nodes[node].Behavior is not { } behavior)
                {
                    continue;
                }
                var step = new ControlContext(this, context, instances, instance, node,
                    continuations.AsSpan(0, semantics.MaxContinuations));
                behavior.Execute(ref step);
                if (!TryPush(node, ref step, stack, ref count))
                {
                    return ControlRunResult.StackOverflow;
                }
            }
            return ControlRunResult.Completed;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(continuations);
            ArrayPool<int>.Shared.Return(stack);
        }
    }

    /// <summary>Pushes continuation targets in reverse so the first requested continuation runs first.</summary>
    bool TryPush(int node, ref ControlContext step, int[] stack, ref int count)
    {
        for (var c = step.Continued - 1; c >= 0; c--)
        {
            var port = ports[nodes[node].FirstPort + step.ContinuationAt(c)];
            for (var t = port.TargetCount - 1; t >= 0; t--)
            {
                if (count == semantics.StackCapacity)
                {
                    return false;
                }
                stack[count++] = targets[port.FirstTarget + t];
            }
        }
        return true;
    }

    readonly record struct NodeEntry(
        GraphNode Node,
        IControlNode? Behavior,
        int FirstPort,
        int MemoryOffset,
        int MemorySize);

    readonly record struct PortEntry(bool IsExecOutput, int FirstTarget, int TargetCount, int DataPort);
}
