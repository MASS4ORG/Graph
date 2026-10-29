using System.Text.Json.Nodes;

namespace MASS4.Graph.State;

/// <summary>
/// The built-in state-flow port type, node types and transition annotations, plus helpers for domain node types.
/// A transition's trigger and priority are annotations: semantic metadata the state compiler consumes, never payload
/// content, so custom transition types keep their own payloads and still get both.
/// </summary>
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

    /// <summary>The registered signal a transition waits for; without it the transition needs no trigger.</summary>
    public static AnnotationType Trigger { get; } = new(Id(0x221), "trigger", AnnotationKind.Signal);

    /// <summary>
    /// Order among the transitions of one state; higher is checked first, ties resolve by node identity.
    /// </summary>
    public static AnnotationType Priority { get; } = new(Id(0x222), "priority", AnnotationKind.Integer);

    /// <summary>Marks the initial state; exactly one per state machine.</summary>
    public static NodeType Entry { get; } = new(Id(0x202), "Entry",
        [new(StartKey, "start", PortDirection.Output, FlowTypeId, PortMultiplicity.Single)]);

    /// <summary>A state without behavior of its own.</summary>
    public static NodeType State { get; } = new(Id(0x203), "State", [Enter(), Exit()]);

    /// <summary>
    /// A transition that fires when its trigger is raised, or at once without a trigger. Version 2 moved trigger and
    /// priority from the payload into annotations.
    /// </summary>
    public static NodeType Transition { get; } = new(Id(0x204), "Transition", [From(), To()]) { Version = 2 };

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
    /// port type; bind the result with <see cref="GuardedTransition"/> and register its migration with
    /// <see cref="AddTransitionMigration"/>.
    /// </summary>
    public static NodeType CreateGuardedTransition(StableId id, string displayName, StableId boolTypeId) =>
        new(id, displayName, [From(), To(), PortDeclaration.Input(ConditionKey, "condition", boolTypeId)])
        {
            Version = 2,
        };

    /// <summary>Annotations for a new transition, for <see cref="AddNodeCommand.Annotations"/>.</summary>
    public static IReadOnlyDictionary<StableId, object> Annotations(StableId? trigger = null, int priority = 0)
    {
        Dictionary<StableId, object> annotations = [];
        if (trigger is { } signal)
        {
            annotations[Trigger.Id] = signal;
        }
        if (priority != 0)
        {
            annotations[Priority.Id] = priority;
        }
        return annotations;
    }

    /// <summary>
    /// Annotations for a new transition triggered by a declared signal. Passing the declaration rather than its
    /// identity keeps trigger references compiler-checked across source renames.
    /// </summary>
    public static IReadOnlyDictionary<StableId, object> Annotations(SignalType? trigger, int priority = 0) =>
        Annotations(trigger?.Id, priority);

    /// <summary>
    /// Registers the state-flow port type, the built-in node types, the transition annotations and the built-in
    /// transition's version 1 → 2 migration.
    /// </summary>
    public static void Register(GraphTypeRegistry registry)
    {
        registry.PortTypes.Register(FlowType);
        registry.NodeTypes.Register(Entry);
        registry.NodeTypes.Register(State);
        registry.NodeTypes.Register(Transition);
        registry.Annotations.Register(Trigger);
        registry.Annotations.Register(Priority);
        AddTransitionMigration(registry, Transition.Id);
    }

    /// <summary>
    /// Registers the version 1 → 2 migration of a transition type: the version 1 payload
    /// <c>{"trigger": id, "priority": n}</c> becomes trigger and priority annotations, and the payload is dropped.
    /// </summary>
    public static void AddTransitionMigration(GraphTypeRegistry registry, StableId transitionTypeId) =>
        registry.AddMigration(new(transitionTypeId, 1, LiftPayload));

    internal static bool IsFlow(GraphPort port) => port.TypeId == FlowTypeId;

    static JsonObject LiftPayload(JsonObject node)
    {
        if (node["payload"] is not JsonObject payload)
        {
            return node;
        }
        var existing = node["annotations"] as JsonObject;
        var annotations = existing ?? [];
        if (payload["trigger"] is { } trigger)
        {
            annotations[Trigger.Id.ToString()] = trigger.DeepClone();
        }
        if (payload["priority"] is { } priority && priority.GetValue<int>() != 0)
        {
            annotations[Priority.Id.ToString()] = priority.DeepClone();
        }
        node.Remove("payload");
        if (existing is null && annotations.Count > 0)
        {
            node["annotations"] = annotations;
        }
        return node;
    }

    static StableId Id(int value) => new(new Guid($"4d415353-3447-5241-5048-{value:x12}"));
}
