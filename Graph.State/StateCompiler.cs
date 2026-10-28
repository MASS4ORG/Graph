namespace MASS4.Graph.State;

/// <summary>Validates a document as one state machine and lays it out as a <see cref="StateMachineProgram"/>.</summary>
static class StateCompiler
{
    public static StateMachineProgram? Compile(
        StateSemantics semantics,
        GraphDocument document,
        InstanceLayoutBuilder layout,
        IDataPlane? data,
        DiagnosticBag diagnostics)
    {
        var local = new DiagnosticBag();
        foreach (var diagnostic in GraphValidator.Validate(document, semantics.Rules))
        {
            local.Add(diagnostic);
        }
        var nodes = document.Nodes.OrderBy(static node => node.Id).ToArray();
        var stateNodes = nodes.Where(node => semantics.RoleOf(node) == StateRole.State).ToArray();
        var stateIndex = stateNodes.Select(static (node, index) => (node.Id, index)).ToDictionary();
        var initial = InitialState(document, semantics, nodes, stateIndex, local);
        var links = Links(document, semantics, nodes, stateIndex, local);
        foreach (var diagnostic in local)
        {
            diagnostics.Add(diagnostic);
        }
        return local.HasErrors ? null : Build(semantics, stateNodes, links, initial, layout, data);
    }

    /// <summary>Resolves the source and target state of every transition, reporting unjoined ones.</summary>
    static List<Link> Links(
        GraphDocument document,
        StateSemantics semantics,
        GraphNode[] nodes,
        Dictionary<ElementId, int> stateIndex,
        DiagnosticBag diagnostics)
    {
        List<Link> links = [];
        foreach (var node in nodes.Where(node => semantics.RoleOf(node) == StateRole.Transition))
        {
            var link = new Link(Neighbor(document, node, PortDirection.Input, stateIndex), node,
                Neighbor(document, node, PortDirection.Output, stateIndex));
            if (link.From < 0 || link.To < 0)
            {
                diagnostics.Error(StateDiagnosticIds.TransitionUnconnected, node.Id,
                    "A transition needs a source state and a target state.");
            }
            links.Add(link);
        }
        return links;
    }

    static StateMachineProgram Build(
        StateSemantics semantics,
        GraphNode[] stateNodes,
        List<Link> links,
        int initial,
        InstanceLayoutBuilder layout,
        IDataPlane? data)
    {
        var triggers = semantics.Triggers.Entries.Select(static trigger => trigger.Id).Order()
            .Select(static (id, index) => (id, index)).ToDictionary();
        List<int> dataPorts = [];
        var states = new StateEntry[stateNodes.Length];
        List<TransitionEntry> transitions = [];
        for (var s = 0; s < stateNodes.Length; s++)
        {
            var node = stateNodes[s];
            var behavior = semantics.StateBehavior(node);
            var memory = behavior.MemorySize > 0 ? layout.AddArray<long>((behavior.MemorySize + 7) / 8).Offset : 0;
            var firstTransition = transitions.Count;
            var outgoing = links.Where(link => link.From == s)
                .OrderByDescending(link => semantics.TransitionBehavior(link.Node).Priority(link.Node))
                .ThenBy(static link => link.Node.Id);
            transitions.AddRange(outgoing.Select(link => Transition(semantics, link, triggers, data, dataPorts)));
            states[s] = new(node, behavior, AddPorts(node, data, dataPorts), memory, behavior.MemorySize,
                firstTransition, transitions.Count - firstTransition);
        }
        return new(semantics, states, [.. transitions], [.. dataPorts], initial, triggers, layout, data);
    }

    static TransitionEntry Transition(
        StateSemantics semantics,
        Link link,
        Dictionary<StableId, int> triggers,
        IDataPlane? data,
        List<int> dataPorts)
    {
        var trigger = link.Node.Payload is TransitionPayload { Trigger: { } id }
            ? triggers[semantics.Triggers.Canonicalize(id)]
            : -1;
        var ports = AddPorts(link.Node, data, dataPorts);
        return new(link.Node, semantics.TransitionBehavior(link.Node), ports, link.To, trigger);
    }

    readonly record struct Link(int From, GraphNode Node, int To);

    static int AddPorts(GraphNode node, IDataPlane? data, List<int> dataPorts)
    {
        var first = dataPorts.Count;
        foreach (var port in node.Ports)
        {
            dataPorts.Add(data is not null && data.TryResolve(port.Id, out var index) ? index : -1);
        }
        return first;
    }

    static int InitialState(
        GraphDocument document,
        StateSemantics semantics,
        GraphNode[] nodes,
        Dictionary<ElementId, int> stateIndex,
        DiagnosticBag diagnostics)
    {
        var entries = nodes.Where(node => semantics.RoleOf(node) == StateRole.Entry).ToArray();
        if (entries.Length != 1)
        {
            diagnostics.Error(StateDiagnosticIds.EntryCount, entries.Length > 1 ? entries[1].Id : null,
                $"A state machine needs exactly one entry node, not {entries.Length}.");
            return -1;
        }
        var initial = Neighbor(document, entries[0], PortDirection.Output, stateIndex);
        if (initial < 0)
        {
            diagnostics.Error(StateDiagnosticIds.EntryUnconnected, entries[0].Id,
                "The entry node must lead to a state.");
        }
        return initial;
    }

    /// <summary>The state across the node's first state-flow port in a direction, or -1.</summary>
    static int Neighbor(
        GraphDocument document,
        GraphNode node,
        PortDirection direction,
        Dictionary<ElementId, int> states)
    {
        var port = node.Ports.FirstOrDefault(port => StateNodes.IsFlow(port) && port.Direction == direction);
        var edge = port is null ? null : document.GetEdges(port.Id).FirstOrDefault();
        var other = edge is null ? default : direction == PortDirection.Output ? edge.Target : edge.Source;
        return edge is not null && document.TryGetPort(other, out var far) &&
               states.TryGetValue(far.NodeId, out var index)
            ? index
            : -1;
    }
}
