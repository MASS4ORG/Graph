using System.Text.Json.Serialization;
using MASS4.Graph.Dataflow;
using MASS4.Graph.State;

[assembly: MASS4.Graph.GraphModule("MASS4.Graph.Samples.Fsm.Generated.GuardModule", typeof(StateNodes), typeof(DataflowNodes))]

namespace MASS4.Graph.Samples.Fsm.Generated;

/// <summary>Guard number declaration.</summary>
[GraphValue("9a4d0000-0000-4000-8000-000000000001", "number", typeof(double), JsonContext = typeof(GuardJson))]
public static partial class Number;

/// <summary>Guard boolean declaration.</summary>
[GraphValue("9a4d0000-0000-4000-8000-000000000002", "bool", typeof(bool))]
public static partial class Boolean;

/// <summary>Number constant declaration.</summary>
[GraphNode("9a4d0000-0000-4000-8000-000000000201", "Number")]
[GraphPort("Out", "9a4d0000-0000-4000-8000-000000000100", "value", PortDirection.Output, "9a4d0000-0000-4000-8000-000000000001", typeof(double))]
[GraphPayload(typeof(NumberPayload), Value = typeof(Number), DefaultFactory = nameof(Default))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Constant), Method = nameof(Evaluate))]
public static partial class Constant
{
    static NumberPayload Default() => new(0);
    internal static void Evaluate(DataflowContext c) => c.Write(Indices.Out, ((NumberPayload)c.Payload!).Value);
}

/// <summary>Distance comparison declaration.</summary>
[GraphNode("9a4d0000-0000-4000-8000-000000000202", "Less than")]
[GraphPort("A", "9a4d0000-0000-4000-8000-000000000101", "a", PortDirection.Input, "9a4d0000-0000-4000-8000-000000000001", typeof(double))]
[GraphPort("B", "9a4d0000-0000-4000-8000-000000000102", "b", PortDirection.Input, "9a4d0000-0000-4000-8000-000000000001", typeof(double))]
[GraphPort("Out", "9a4d0000-0000-4000-8000-000000000100", "result", PortDirection.Output, "9a4d0000-0000-4000-8000-000000000002", typeof(bool))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(LessThan), Method = nameof(Evaluate))]
public static partial class LessThan
{
    internal static void Evaluate(DataflowContext c) => c.Write(Indices.Out, c.Read<double>(Indices.A) < c.Read<double>(Indices.B));
}

/// <summary>Negation declaration.</summary>
[GraphNode("9a4d0000-0000-4000-8000-000000000203", "Not")]
[GraphPort("Value", "9a4d0000-0000-4000-8000-000000000101", "value", PortDirection.Input, "9a4d0000-0000-4000-8000-000000000002", typeof(bool))]
[GraphPort("Out", "9a4d0000-0000-4000-8000-000000000100", "result", PortDirection.Output, "9a4d0000-0000-4000-8000-000000000002", typeof(bool))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Not), Method = nameof(Evaluate))]
public static partial class Not
{
    internal static void Evaluate(DataflowContext c) => c.Write(Indices.Out, !c.Read<bool>(Indices.Value));
}

/// <summary>Attack state declaration with typed flow and data ports.</summary>
[GraphNode("9a4d0000-0000-4000-8000-000000000204", "Attack")]
[GraphPort("Enter", "4d415353-3447-5241-5048-000000000212", "enter", PortDirection.Input, "4d415353-3447-5241-5048-000000000201", typeof(StateFlow), Multiplicity = PortMultiplicity.Multiple)]
[GraphPort("Exit", "4d415353-3447-5241-5048-000000000213", "exit", PortDirection.Output, "4d415353-3447-5241-5048-000000000201", typeof(StateFlow))]
[GraphPort("Attacks", "9a4d0000-0000-4000-8000-000000000103", "attacks", PortDirection.Output, "9a4d0000-0000-4000-8000-000000000001", typeof(double))]
[GraphBehavior(GraphBehaviorKind.State, typeof(AttackBehavior))]
public static partial class Attack;

/// <summary>Guarded transition declaration; the generator registers its version 1 migration.</summary>
[GraphNode("9a4d0000-0000-4000-8000-000000000205", "When", GuardType = "9a4d0000-0000-4000-8000-000000000002")]
public static partial class Guarded;

/// <summary>Timed transition declaration with an independent object payload.</summary>
[GraphNode("9a4d0000-0000-4000-8000-000000000206", "After")]
[GraphPort("From", "4d415353-3447-5241-5048-000000000214", "from", PortDirection.Input, "4d415353-3447-5241-5048-000000000201", typeof(StateFlow))]
[GraphPort("To", "4d415353-3447-5241-5048-000000000215", "to", PortDirection.Output, "4d415353-3447-5241-5048-000000000201", typeof(StateFlow), Multiplicity = PortMultiplicity.Single)]
[GraphPayload(typeof(AfterPayload), JsonContext = typeof(GuardJson), DefaultFactory = nameof(Default))]
[GraphBehavior(GraphBehaviorKind.Transition, typeof(AfterBehavior))]
public static partial class After
{
    static AfterPayload Default() => new(1);
}

/// <summary>A simulated signal release, matching the hand library's renamedSeePlayer registration.</summary>
[GraphSignal("9a4d0000-0000-4000-8000-000000000304", "See player")]
[GraphRenamedFrom("9a4d0000-0000-4000-8000-000000000301")]
public static partial class SeePlayer;

/// <summary>Lost-player signal declaration.</summary>
[GraphSignal("9a4d0000-0000-4000-8000-000000000302", "Lose player")]
public static partial class LosePlayer;

/// <summary>Hit signal declaration.</summary>
[GraphSignal("9a4d0000-0000-4000-8000-000000000303", "Hit")]
public static partial class Hit;

sealed class AttackBehavior : IStateNode
{
    public int MemorySize => sizeof(int);
    public void OnEnter(StateContext context)
    {
        ref var attacks = ref context.Memory<int>();
        attacks++;
        context.Write(Attack.Indices.Attacks, (double)attacks);
    }
}

sealed class AfterBehavior : ITransitionNode
{
    public bool ShouldFire(TransitionContext context) =>
        context.TimeInState >= ((AfterPayload)context.Payload!).Seconds;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(AfterPayload))]
partial class GuardJson : JsonSerializerContext;
