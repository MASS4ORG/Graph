namespace MASS4.Graph.State;

/// <summary>Payload of transition nodes: an optional typed trigger and a priority among sibling transitions.</summary>
/// <param name="Trigger">Identity of a registered <see cref="TriggerType"/>; null fires without a trigger.</param>
/// <param name="Priority">Higher priorities are checked first; ties resolve by node identity.</param>
public sealed record TransitionPayload(StableId? Trigger = null, int Priority = 0);

/// <summary>The built-in state-flow port type and node types, plus helpers for domain node types.</summary>
public static class StateNodes
{
    /// <summary>Identity of the state-flow port type that joins states and transitions.</summary>
    public static StableId FlowTypeId { get; } = Id(0x201);

    /// <summary>The state-flow port type, owned by these semantics.</summary>
    public static PortType FlowType { get; } = new(FlowTypeId, "state flow");

    /// <summary>Key of the entry node's output.</summary>
    public static StableId StartKey { get; } = Id(0x211);

    /// <summary>Key of a state's flow input.</summary>
    public static StableId EnterKey { get; } = Id(0x212);

    /// <summary>Key of a state's flow output.</summary>
    public static StableId ExitKey { get; } = Id(0x213);

    /// <summary>Key of a transition's flow input.</summary>
    public static StableId FromKey { get; } = Id(0x214);

    /// <summary>Key of a transition's flow output.</summary>
    public static StableId ToKey { get; } = Id(0x215);

    /// <summary>Key of a guarded transition's boolean data input.</summary>
    public static StableId ConditionKey { get; } = Id(0x216);

    /// <summary>Marks the initial state; exactly one per state machine.</summary>
    public static NodeType Entry { get; } = new(Id(0x202), "Entry",
        [new(StartKey, "start", PortDirection.Output, FlowTypeId, PortMultiplicity.Single)]);

    /// <summary>A state without behavior of its own.</summary>
    public static NodeType State { get; } = new(Id(0x203), "State", [Enter(), Exit()]);

    /// <summary>A transition that fires when its trigger is raised, or at once without a trigger.</summary>
    public static NodeType Transition { get; } = new(Id(0x204), "Transition", [From(), To()])
    {
        PayloadSerializer = TransitionPayloadSerializer.Instance,
    };

    /// <summary>Declares a state's flow input, which any number of transitions may enter.</summary>
    public static PortDeclaration Enter() =>
        new(EnterKey, "enter", PortDirection.Input, FlowTypeId, PortMultiplicity.Multiple);

    /// <summary>Declares a state's flow output, which leads to any number of transitions.</summary>
    public static PortDeclaration Exit() =>
        new(ExitKey, "exit", PortDirection.Output, FlowTypeId, PortMultiplicity.Multiple);

    /// <summary>Declares a transition's flow input from exactly one state.</summary>
    public static PortDeclaration From() =>
        new(FromKey, "from", PortDirection.Input, FlowTypeId, PortMultiplicity.Single);

    /// <summary>Declares a transition's flow output to exactly one state.</summary>
    public static PortDeclaration To() =>
        new(ToKey, "to", PortDirection.Output, FlowTypeId, PortMultiplicity.Single);

    /// <summary>
    /// Creates a transition type that also requires a boolean data input to be true. The domain owns the boolean
    /// port type; bind the result with <see cref="GuardedTransition"/>.
    /// </summary>
    public static NodeType CreateGuardedTransition(StableId id, string displayName, StableId boolTypeId) =>
        new(id, displayName, [From(), To(), PortDeclaration.Input(ConditionKey, "condition", boolTypeId)])
        {
            PayloadSerializer = TransitionPayloadSerializer.Instance,
        };

    /// <summary>Registers the state-flow port type and the built-in node types.</summary>
    public static void Register(GraphTypeRegistry registry)
    {
        registry.PortTypes.Register(FlowType);
        registry.NodeTypes.Register(Entry);
        registry.NodeTypes.Register(State);
        registry.NodeTypes.Register(Transition);
    }

    internal static bool IsFlow(GraphPort port) => port.TypeId == FlowTypeId;

    static StableId Id(int value) => new(new Guid($"4d415353-3447-5241-5048-{value:x12}"));
}

/// <summary>Reads and writes <see cref="TransitionPayload"/> without reflection.</summary>
sealed class TransitionPayloadSerializer : IValueSerializer
{
    public static TransitionPayloadSerializer Instance { get; } = new();

    public void Write(Utf8JsonWriter writer, object? value)
    {
        var payload = (TransitionPayload)value!;
        writer.WriteStartObject();
        if (payload.Trigger is { } trigger)
        {
            writer.WriteString("trigger", trigger.ToString());
        }
        if (payload.Priority != 0)
        {
            writer.WriteNumber("priority", payload.Priority);
        }
        writer.WriteEndObject();
    }

    public object? Read(JsonElement element)
    {
        StableId? trigger = element.TryGetProperty("trigger", out var saved)
            ? StableId.Parse(saved.GetString()!)
            : null;
        var priority = element.TryGetProperty("priority", out var number) ? number.GetInt32() : 0;
        return new TransitionPayload(trigger, priority);
    }
}
