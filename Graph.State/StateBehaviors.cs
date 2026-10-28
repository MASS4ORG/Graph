using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MASS4.Graph.State;

/// <summary>A typed event that fires transitions; registered identities replace magic event strings.</summary>
/// <param name="id">Persistent identity.</param>
/// <param name="displayName">Human-readable name.</param>
public sealed class TriggerType(StableId id, string displayName) : ITypeIdentity
{
    /// <inheritdoc />
    public StableId Id { get; } = id;

    /// <inheritdoc />
    public string DisplayName { get; } = displayName;
}

/// <summary>Index of a trigger in a compiled state machine; resolve once and raise without lookups.</summary>
/// <param name="Index">Position among the machine's triggers.</param>
public readonly record struct TriggerHandle(int Index);

/// <summary>
/// Behavior of one state type. Implementations are stateless and shared; per-instance memory is requested through
/// <see cref="MemorySize"/>.
/// </summary>
public interface IStateNode
{
    /// <summary>Bytes of per-instance memory each state of this type keeps.</summary>
    int MemorySize => 0;

    /// <summary>Runs when the machine enters the state.</summary>
    void OnEnter(StateContext context)
    {
    }

    /// <summary>Runs on every tick the machine is in the state, after transitions were checked.</summary>
    void OnUpdate(StateContext context)
    {
    }

    /// <summary>Runs when the machine leaves the state.</summary>
    void OnExit(StateContext context)
    {
    }
}

/// <summary>Decides whether a transition fires; checked on every tick while its source state is current.</summary>
public interface ITransitionNode
{
    /// <summary>Order among the transitions of one state; higher runs first. Defaults to the payload.</summary>
    int Priority(GraphNode node) => node.Payload is TransitionPayload payload ? payload.Priority : 0;

    /// <summary>Whether the machine should take this transition now.</summary>
    bool ShouldFire(TransitionContext context);
}

/// <summary>Fires when the payload trigger was raised this tick, or at once when the payload has no trigger.</summary>
public sealed class TriggerTransition : ITransitionNode
{
    /// <summary>Shared instance.</summary>
    public static TriggerTransition Instance { get; } = new();

    /// <inheritdoc />
    public bool ShouldFire(TransitionContext context) => context.TriggerSatisfied;
}

/// <summary>Fires when the payload trigger (if any) was raised and the boolean condition input is true.</summary>
public sealed class GuardedTransition : ITransitionNode
{
    /// <summary>Shared instance.</summary>
    public static GuardedTransition Instance { get; } = new();

    /// <inheritdoc />
    public bool ShouldFire(TransitionContext context) =>
        context.TriggerSatisfied && context.Read<bool>(context.PortIndex(StateNodes.ConditionKey));
}

sealed class EmptyState : IStateNode
{
    public static EmptyState Instance { get; } = new();
}

/// <summary>What a state sees while it runs; ports are addressed by their index in declaration order.</summary>
public readonly ref struct StateContext
{
    readonly StateMachineProgram program;
    readonly int state;

    internal StateContext(
        StateMachineProgram program,
        EvaluationContext evaluation,
        InstanceBuffer instances,
        InstanceHandle instance,
        int state)
    {
        this.program = program;
        this.state = state;
        Evaluation = evaluation;
        Instances = instances;
        Instance = instance;
    }

    /// <summary>Ambient services such as cancellation and typed features.</summary>
    public EvaluationContext Evaluation { get; }

    /// <summary>The instance buffer being ticked.</summary>
    public InstanceBuffer Instances { get; }

    /// <summary>The instance being ticked.</summary>
    public InstanceHandle Instance { get; }

    /// <summary>The state node.</summary>
    public GraphNode Node => program.StateAt(state).Node;

    /// <summary>The state payload.</summary>
    public object? Payload => Node.Payload;

    /// <summary>Seconds since the machine entered this state.</summary>
    public double TimeInState => program.TimeInState(Instances, Instance);

    /// <summary>Index of the port declared with <paramref name="key"/>.</summary>
    /// <exception cref="KeyNotFoundException">The node has no such port.</exception>
    public int PortIndex(StableId key) => StateMachineProgram.PortIndex(Node, key);

    /// <summary>Reads a data input through the data plane.</summary>
    /// <exception cref="InvalidOperationException">The port carries no data or there is no data plane.</exception>
    public T Read<T>(int port) where T : unmanaged =>
        program.Data(program.StateAt(state).FirstPort + port, out var index)
            .Read<T>(Evaluation, Instances, Instance, index);

    /// <summary>Writes a data output through the data plane; values depending on it recompute.</summary>
    /// <exception cref="InvalidOperationException">The port carries no data or there is no data plane.</exception>
    public void Write<T>(int port, T value) where T : unmanaged =>
        program.Data(program.StateAt(state).FirstPort + port, out var index)
            .Write(Instances, Instance, index, value);

    /// <summary>Returns this state's per-instance memory viewed as <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">The state type declared less memory than needed.</exception>
    public ref T Memory<T>() where T : unmanaged
    {
        var entry = program.StateAt(state);
        if (Unsafe.SizeOf<T>() > entry.MemorySize)
        {
            throw new InvalidOperationException(
                $"'{Node.TypeId}' declares {entry.MemorySize} bytes of memory, not enough for {typeof(T).Name}.");
        }
        var bytes = Instances.GetRecord(Instance).Slice(entry.MemoryOffset, Unsafe.SizeOf<T>());
        return ref MemoryMarshal.AsRef<T>(bytes);
    }
}

/// <summary>What a transition sees while it decides; ports are addressed by their index in declaration order.</summary>
public readonly ref struct TransitionContext
{
    readonly StateMachineProgram program;
    readonly int transition;

    internal TransitionContext(
        StateMachineProgram program,
        EvaluationContext evaluation,
        InstanceBuffer instances,
        InstanceHandle instance,
        int transition)
    {
        this.program = program;
        this.transition = transition;
        Evaluation = evaluation;
        Instances = instances;
        Instance = instance;
    }

    /// <summary>Ambient services such as cancellation and typed features.</summary>
    public EvaluationContext Evaluation { get; }

    /// <summary>The instance buffer being ticked.</summary>
    public InstanceBuffer Instances { get; }

    /// <summary>The instance being ticked.</summary>
    public InstanceHandle Instance { get; }

    /// <summary>The transition node.</summary>
    public GraphNode Node => program.TransitionAt(transition).Node;

    /// <summary>The transition payload.</summary>
    public object? Payload => Node.Payload;

    /// <summary>Seconds since the machine entered the source state.</summary>
    public double TimeInState => program.TimeInState(Instances, Instance);

    /// <summary>Whether the payload names a trigger.</summary>
    public bool HasTrigger => program.TransitionAt(transition).Trigger >= 0;

    /// <summary>True without a payload trigger, otherwise whether it was raised this tick.</summary>
    public bool TriggerSatisfied =>
        program.TransitionAt(transition).Trigger is var trigger &&
        (trigger < 0 || program.IsRaised(Instances, Instance, trigger));

    /// <summary>Whether a trigger was raised this tick; unknown triggers are never raised.</summary>
    public bool IsRaised(StableId trigger) =>
        program.TryResolveTrigger(trigger, out var handle) && program.IsRaised(Instances, Instance, handle.Index);

    /// <summary>Index of the port declared with <paramref name="key"/>.</summary>
    /// <exception cref="KeyNotFoundException">The node has no such port.</exception>
    public int PortIndex(StableId key) => StateMachineProgram.PortIndex(Node, key);

    /// <summary>Reads a data input through the data plane.</summary>
    /// <exception cref="InvalidOperationException">The port carries no data or there is no data plane.</exception>
    public T Read<T>(int port) where T : unmanaged =>
        program.Data(program.TransitionAt(transition).FirstPort + port, out var index)
            .Read<T>(Evaluation, Instances, Instance, index);
}
