namespace MASS4.Graph.State;

/// <summary>The entry node of a state machine and its typed flow output.</summary>
/// <param name="NodeId">Document node identity.</param>
/// <param name="Start">Flow output to the initial state.</param>
public readonly record struct EntryHandle(ElementId NodeId, GraphOutput<StateFlow> Start);

/// <summary>A state node and its typed flow ports.</summary>
/// <param name="NodeId">Document node identity.</param>
/// <param name="Enter">Flow input from transitions.</param>
/// <param name="Exit">Flow output to transitions.</param>
public readonly record struct StateHandle(ElementId NodeId, GraphInput<StateFlow> Enter, GraphOutput<StateFlow> Exit);

/// <summary>
/// Typed editing helpers for the built-in state-machine nodes, matching the helpers generated for domain
/// declarations. Triggers are signal declarations, not identities, so a renamed or removed signal is a compile error at
/// every call site. Each helper is one reversible edit and throws with the editor's diagnostics when rejected.
/// </summary>
public static class StateGraphBuilder
{
    /// <summary>Adds the entry node.</summary>
    public static EntryHandle AddEntry(GraphEditor editor)
    {
        var node = new AddNodeCommand(StateNodes.Entry);
        TypedGraphBuilder.Require(editor, node);
        return new(node.NodeId, new(node.PortId(StateNodes.StartKey)));
    }

    /// <summary>Adds a state of <paramref name="type"/>, by default the built-in state without behavior.</summary>
    public static StateHandle AddState(GraphEditor editor, NodeType? type = null)
    {
        var node = new AddNodeCommand(type ?? StateNodes.State);
        TypedGraphBuilder.Require(editor, node);
        return new(node.NodeId, new(node.PortId(StateNodes.EnterKey)), new(node.PortId(StateNodes.ExitKey)));
    }

    /// <summary>Connects the entry to the initial state.</summary>
    public static void Start(GraphEditor editor, EntryHandle entry, GraphInput<StateFlow> initial) =>
        editor.Connect(entry.Start, initial);

    /// <summary>
    /// Adds a built-in transition from one state to another and wires it as one atomic edit. Without a trigger it fires
    /// on the next tick; higher priorities are checked first.
    /// </summary>
    public static ElementId AddTransition(
        GraphEditor editor,
        GraphOutput<StateFlow> from,
        GraphInput<StateFlow> to,
        SignalType? trigger = null,
        int priority = 0)
    {
        var node = new AddNodeCommand(StateNodes.Transition) { Annotations = StateNodes.Annotations(trigger, priority) };
        TypedGraphBuilder.Require(editor, new CompositeCommand("Add transition",
        [
            node, new ConnectCommand(from.Id, node.PortId(StateNodes.FromKey)),
            new ConnectCommand(node.PortId(StateNodes.ToKey), to.Id),
        ]));
        return node.NodeId;
    }
}
