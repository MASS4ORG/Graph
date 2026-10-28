using System.Text.Json;
using MASS4.Graph.Dataflow;
using MASS4.Graph.State;

namespace MASS4.Graph.Samples.Fsm;

/// <summary>Payload of a number constant.</summary>
/// <param name="Value">The constant.</param>
public sealed record NumberPayload(double Value);

/// <summary>Payload of a timed transition.</summary>
/// <param name="Seconds">Time in the source state after which the transition fires.</param>
public sealed record AfterPayload(double Seconds);

/// <summary>
/// The guard domain, registered by hand: number and boolean types, comparison nodes, an attack state that counts its
/// entries, a timed transition and three typed triggers.
/// </summary>
public static class GuardLibrary
{
    /// <summary>Numbers (distances, counts, times).</summary>
    public static PortType Number { get; } = new(Id(0x001), "number") { ValueSerializer = NumberSerializer.Instance };

    /// <summary>Booleans (guard conditions).</summary>
    public static PortType Bool { get; } = new(Id(0x002), "bool");

    /// <summary>Output of every single-output node.</summary>
    public static StableId OutKey { get; } = Id(0x100);

    /// <summary>First input.</summary>
    public static StableId AKey { get; } = Id(0x101);

    /// <summary>Second input.</summary>
    public static StableId BKey { get; } = Id(0x102);

    /// <summary>The attack count published by the attack state.</summary>
    public static StableId AttacksKey { get; } = Id(0x103);

    /// <summary>A number constant; payload <see cref="NumberPayload"/>.</summary>
    public static NodeType Constant { get; } = new(Id(0x201), "Number",
        [PortDeclaration.Output(OutKey, "value", Number.Id)])
    {
        PayloadSerializer = NumberPayloadSerializer.Instance,
        DefaultPayload = static () => new NumberPayload(0),
    };

    /// <summary>Whether a is less than b.</summary>
    public static NodeType LessThan { get; } = new(Id(0x202), "Less than",
    [
        PortDeclaration.Input(AKey, "a", Number.Id), PortDeclaration.Input(BKey, "b", Number.Id),
        PortDeclaration.Output(OutKey, "result", Bool.Id),
    ]);

    /// <summary>Boolean negation.</summary>
    public static NodeType Not { get; } = new(Id(0x203), "Not",
        [PortDeclaration.Input(AKey, "value", Bool.Id), PortDeclaration.Output(OutKey, "result", Bool.Id)]);

    /// <summary>The attack state; publishes how many times it was entered.</summary>
    public static NodeType Attack { get; } = new(Id(0x204), "Attack",
        [StateNodes.Enter(), StateNodes.Exit(), PortDeclaration.Output(AttacksKey, "attacks", Number.Id)]);

    /// <summary>A transition guarded by a boolean condition.</summary>
    public static NodeType Guarded { get; } = StateNodes.CreateGuardedTransition(Id(0x205), "When", Bool.Id);

    /// <summary>A transition that fires after a time in the source state; payload <see cref="AfterPayload"/>.</summary>
    public static NodeType After { get; } = new(Id(0x206), "After", [StateNodes.From(), StateNodes.To()])
    {
        PayloadSerializer = AfterPayloadSerializer.Instance,
        DefaultPayload = static () => new AfterPayload(1),
    };

    /// <summary>The guard sees the player.</summary>
    public static SignalType SeePlayer { get; } = new(Id(0x301), "See player");

    /// <summary>The guard lost sight of the player.</summary>
    public static SignalType LosePlayer { get; } = new(Id(0x302), "Lose player");

    /// <summary>The guard landed a hit and attacks again.</summary>
    public static SignalType Hit { get; } = new(Id(0x303), "Hit");

    /// <summary>
    /// Registers the port and node types, the trigger signals, and the built-in state and dataflow types they rely
    /// on. <paramref name="renamedSeePlayer"/> simulates a later release that gave the trigger a new identity,
    /// keeping the old one as a rename entry next to every other rename.
    /// </summary>
    public static void Register(GraphTypeRegistry registry, StableId? renamedSeePlayer = null)
    {
        registry.PortTypes.Register(Number);
        registry.PortTypes.Register(Bool);
        foreach (var node in new[] { Constant, LessThan, Not, Attack, Guarded, After })
        {
            registry.NodeTypes.Register(node);
        }
        if (renamedSeePlayer is { } renamed)
        {
            registry.Signals.Register(new(renamed, SeePlayer.DisplayName));
            registry.Signals.AddRename(SeePlayer.Id, renamed);
        }
        else
        {
            registry.Signals.Register(SeePlayer);
        }
        registry.Signals.Register(LosePlayer);
        registry.Signals.Register(Hit);
        StateNodes.Register(registry);
        DataflowNodes.Register(registry);
    }

    /// <summary>Creates dataflow semantics with the guard value types and behaviors.</summary>
    public static DataflowSemantics CreateDataflow() => new DataflowSemantics()
        .AddValueType<double>(Number.Id)
        .AddValueType<bool>(Bool.Id)
        .AddNode(Constant.Id, new DataflowNode(static c => c.Write(0, ((NumberPayload)c.Payload!).Value)))
        .AddNode(LessThan.Id, new DataflowNode(static c => c.Write(2, c.Read<double>(0) < c.Read<double>(1))))
        .AddNode(Not.Id, new DataflowNode(static c => c.Write(1, !c.Read<bool>(0))));

    /// <summary>Creates state semantics with the guard roles.</summary>
    public static StateSemantics CreateState() => new StateSemantics()
        .AddState(Attack.Id, new AttackState())
        .AddTransition(Guarded.Id, GuardedTransition.Instance)
        .AddTransition(After.Id, new AfterTransition());

    static StableId Id(int value) => new(new Guid($"9a4d0000-0000-4000-8000-{value:x12}"));

    /// <summary>Counts entries in instance memory, so self-transitions are visible, and publishes the count.</summary>
    sealed class AttackState : IStateNode
    {
        public int MemorySize => sizeof(int);

        public void OnEnter(StateContext context)
        {
            ref var attacks = ref context.Memory<int>();
            attacks++;
            context.Write(2, (double)attacks);
        }
    }

    /// <summary>Fires once the machine has been in the source state for the payload's duration.</summary>
    sealed class AfterTransition : ITransitionNode
    {
        public bool ShouldFire(TransitionContext context) =>
            context.TimeInState >= ((AfterPayload)context.Payload!).Seconds;
    }

    sealed class NumberSerializer : IValueSerializer
    {
        public static NumberSerializer Instance { get; } = new();

        public void Write(Utf8JsonWriter writer, object? value) => writer.WriteNumberValue((double)value!);

        public object? Read(JsonElement element) => element.GetDouble();
    }

    sealed class NumberPayloadSerializer : IValueSerializer
    {
        public static NumberPayloadSerializer Instance { get; } = new();

        public void Write(Utf8JsonWriter writer, object? value) =>
            writer.WriteNumberValue(((NumberPayload)value!).Value);

        public object? Read(JsonElement element) => new NumberPayload(element.GetDouble());
    }

    sealed class AfterPayloadSerializer : IValueSerializer
    {
        public static AfterPayloadSerializer Instance { get; } = new();

        public void Write(Utf8JsonWriter writer, object? value)
        {
            writer.WriteStartObject();
            writer.WriteNumber("seconds", ((AfterPayload)value!).Seconds);
            writer.WriteEndObject();
        }

        public object? Read(JsonElement element) => new AfterPayload(element.GetProperty("seconds").GetDouble());
    }
}
