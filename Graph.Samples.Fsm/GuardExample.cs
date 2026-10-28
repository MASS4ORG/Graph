using MASS4.Graph.Dataflow;
using MASS4.Graph.State;

namespace MASS4.Graph.Samples.Fsm;

/// <summary>
/// The guard: Patrol → Chase on SeePlayer; Chase ↔ Attack on the distance to the player (closer than 2 attacks,
/// otherwise chases); Attack → Attack on Hit, a self-transition that checks first; Chase → Search on LosePlayer;
/// Search → Chase on SeePlayer, or back to Patrol after 3 seconds. The distance is a per-instance external variable.
/// </summary>
/// <param name="Document">The state machine asset.</param>
/// <param name="Distance">Per-instance distance to the player.</param>
/// <param name="Patrol">The patrol state.</param>
/// <param name="Chase">The chase state.</param>
/// <param name="Attack">The attack state.</param>
/// <param name="Search">The search state.</param>
/// <param name="Attacks">The attack count published by the attack state.</param>
public sealed record GuardExample(
    GraphDocument Document,
    GraphVariable Distance,
    ElementId Patrol,
    ElementId Chase,
    ElementId Attack,
    ElementId Search,
    ElementId Attacks)
{
    /// <summary>Builds the guard through editor commands with every semantics' rules active.</summary>
    /// <exception cref="InvalidOperationException">A command fails; the example is broken.</exception>
    public static GuardExample Build(DataflowSemantics data, StateSemantics state, GraphTypeRegistry registry)
    {
        var editor = new GraphEditor(new GraphDocument(registry), data.Conversions, [.. data.Rules, .. state.Rules]);
        var distance = new GraphVariable(ElementId.New(), "distance", GuardLibrary.Number.Id, VariableScope.External)
        {
            DefaultValue = 100.0,
        };
        Require(editor.Execute(new SetVariableCommand(distance)));
        var entry = Add(editor, StateNodes.Entry);
        var patrol = Add(editor, StateNodes.State);
        var chase = Add(editor, StateNodes.State);
        var attack = Add(editor, GuardLibrary.Attack);
        var search = Add(editor, StateNodes.State);
        Connect(editor, entry, StateNodes.StartKey, patrol, StateNodes.EnterKey);
        Join(editor, patrol, chase, StateNodes.Transition, null, StateNodes.Annotations(GuardLibrary.SeePlayer.Id));
        Join(editor, attack, attack, StateNodes.Transition, null, StateNodes.Annotations(GuardLibrary.Hit.Id, 1));
        Join(editor, chase, search, StateNodes.Transition, null, StateNodes.Annotations(GuardLibrary.LosePlayer.Id));
        Join(editor, search, chase, StateNodes.Transition, null, StateNodes.Annotations(GuardLibrary.SeePlayer.Id));
        Join(editor, search, patrol, GuardLibrary.After, new AfterPayload(3), StateNodes.Annotations());
        var close = Add(editor, GuardLibrary.LessThan);
        var far = Add(editor, GuardLibrary.Not);
        var reach = Add(editor, GuardLibrary.Constant, new NumberPayload(2));
        var read = Add(editor, DataflowNodes.GetVariable, new VariableReference(distance.Id));
        Connect(editor, read, DataflowNodes.ValueKey, close, GuardLibrary.AKey);
        Connect(editor, reach, GuardLibrary.OutKey, close, GuardLibrary.BKey);
        Connect(editor, close, GuardLibrary.OutKey, far, GuardLibrary.AKey);
        var engage = Join(editor, chase, attack, GuardLibrary.Guarded, null, StateNodes.Annotations());
        var retreat = Join(editor, attack, chase, GuardLibrary.Guarded, null, StateNodes.Annotations());
        Connect(editor, close, GuardLibrary.OutKey, engage, StateNodes.ConditionKey);
        Connect(editor, far, GuardLibrary.OutKey, retreat, StateNodes.ConditionKey);
        return new(editor.Document, distance, patrol.NodeId, chase.NodeId, attack.NodeId, search.NodeId,
            attack.PortId(GuardLibrary.AttacksKey));
    }

    static AddNodeCommand Join(
        GraphEditor editor,
        AddNodeCommand from,
        AddNodeCommand to,
        NodeType type,
        object? payload,
        IReadOnlyDictionary<StableId, object> annotations)
    {
        var transition = new AddNodeCommand(type, payload) { Annotations = annotations };
        Require(editor.Execute(transition));
        Connect(editor, from, StateNodes.ExitKey, transition, StateNodes.FromKey);
        Connect(editor, transition, StateNodes.ToKey, to, StateNodes.EnterKey);
        return transition;
    }

    static AddNodeCommand Add(GraphEditor editor, NodeType type, object? payload = null)
    {
        var command = new AddNodeCommand(type, payload);
        Require(editor.Execute(command));
        return command;
    }

    static void Connect(GraphEditor editor, AddNodeCommand from, StableId output, AddNodeCommand to, StableId input) =>
        Require(editor.Connect(from.PortId(output), to.PortId(input)));

    static void Require(CommandResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
        }
    }
}
