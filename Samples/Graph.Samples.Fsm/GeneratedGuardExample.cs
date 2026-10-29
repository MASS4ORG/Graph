using MASS4.Graph.Dataflow;
using MASS4.Graph.State;

namespace MASS4.Graph.Samples.Fsm.Generated;

/// <summary>
/// The <see cref="GuardExample"/> guard built only through generated declarations and typed builders. Ports are typed
/// handles and triggers are signal declarations, so no identity or name is spelled out at a call site: renaming a
/// declaration in source renames every reference, and removing one is a compile error.
/// </summary>
public static class GeneratedGuardExample
{
    /// <summary>Builds the guard with every semantics' rules of <paramref name="module"/> active.</summary>
    /// <exception cref="InvalidOperationException">An edit fails; the example is broken.</exception>
    public static GuardExample Build(GuardModule module, GraphTypeRegistry registry)
    {
        var data = module.CreateDataflow();
        var state = module.CreateState();
        var editor = new GraphEditor(new GraphDocument(registry), data.Conversions, [.. data.Rules, .. state.Rules]);
        var distance = new GraphVariable(ElementId.New(), "distance", Number.Type.Id, VariableScope.External)
        {
            DefaultValue = 100.0,
        };
        TypedGraphBuilder.Require(editor, new SetVariableCommand(distance));
        var entry = StateGraphBuilder.AddEntry(editor);
        var patrol = StateGraphBuilder.AddState(editor);
        var chase = StateGraphBuilder.AddState(editor);
        var attack = Attack.Add(editor);
        var search = StateGraphBuilder.AddState(editor);
        StateGraphBuilder.Start(editor, entry, patrol.Enter);
        StateGraphBuilder.AddTransition(editor, patrol.Exit, chase.Enter, SeePlayer.Type);
        StateGraphBuilder.AddTransition(editor, attack.Exit, attack.Enter, Hit.Type, priority: 1);
        StateGraphBuilder.AddTransition(editor, chase.Exit, search.Enter, LosePlayer.Type);
        StateGraphBuilder.AddTransition(editor, search.Exit, chase.Enter, SeePlayer.Type);
        After.Transition(editor, search.Exit, patrol.Enter, new AfterPayload(3));
        var close = LessThan.Add(editor);
        var far = Not.Add(editor);
        var reach = Constant.Add(editor, new NumberPayload(2));
        var read = new AddNodeCommand(DataflowNodes.GetVariable, new VariableReference(distance.Id));
        TypedGraphBuilder.Require(editor, read);
        TypedGraphBuilder.Require(editor, new ConnectCommand(read.PortId(DataflowNodes.ValueKey), close.A.Id));
        editor.Connect(reach.Out, close.B);
        editor.Connect(close.Out, far.Value);
        var engage = Guarded.Transition(editor, chase.Exit, attack.Enter);
        var retreat = Guarded.Transition(editor, attack.Exit, chase.Enter);
        editor.Connect(close.Out, engage.Condition);
        editor.Connect(far.Out, retreat.Condition);
        return new(editor.Document, distance, patrol.NodeId, chase.NodeId, attack.NodeId, search.NodeId,
            attack.Attacks.Id);
    }
}
