using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MASS4.Graph.State;

/// <summary>
/// Evaluation-context feature carrying the time step of the current evaluation. Backends that tick state machines
/// require it, so hosts set it once per frame; a missing time step fails instead of freezing timers.
/// </summary>
public sealed class StateTime
{
    /// <summary>Seconds elapsed since the previous evaluation.</summary>
    public double DeltaSeconds { get; set; }
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

    /// <summary>Evaluation-context features the state reads; backends refuse to run without them.</summary>
    IReadOnlyList<Type> RequiredFeatures => [];

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

/// <summary>
/// Decides whether a transition fires; checked on every tick while its source state is current, in the order of the
/// transitions' <see cref="StateNodes.Priority"/> annotations. A transition with a <see cref="StateNodes.Trigger"/>
/// annotation is only checked on ticks its trigger was raised, whatever its behavior.
/// </summary>
public interface ITransitionNode
{
    /// <summary>Evaluation-context features the transition reads; backends refuse to run without them.</summary>
    IReadOnlyList<Type> RequiredFeatures => [];

    /// <summary>Whether the machine should take this transition now.</summary>
    bool ShouldFire(TransitionContext context);
}

/// <summary>Fires once its trigger annotation was raised, or at once without one.</summary>
public sealed class TriggerTransition : ITransitionNode
{
    /// <summary>Shared instance.</summary>
    public static TriggerTransition Instance { get; } = new();

    /// <inheritdoc />
    public bool ShouldFire(TransitionContext context) => true;
}

/// <summary>Fires when the boolean condition input is true, once its trigger annotation (if any) was raised.</summary>
public sealed class GuardedTransition : ITransitionNode
{
    /// <summary>Shared instance.</summary>
    public static GuardedTransition Instance { get; } = new();

    /// <inheritdoc />
    public bool ShouldFire(TransitionContext context) =>
        context.Read<bool>(context.PortIndex(StateNodes.ConditionKey));
}

sealed class EmptyState : IStateNode
{
    public static EmptyState Instance { get; } = new();
}

/// <summary>
/// Storage and wiring behind <see cref="StateContext"/> and <see cref="TransitionContext"/>. The interpreter's
/// <see cref="StateMachineProgram"/> is one implementation; generated backends supply their own, so state and
/// transition behaviors run unchanged on every backend. States and transitions are addressed by backend index and
/// ports by declaration index.
/// </summary>
public abstract class StateFrame
{
    /// <summary>The state node at an index.</summary>
    protected internal abstract GraphNode StateNode(int state);

    /// <summary>The transition node at an index.</summary>
    protected internal abstract GraphNode TransitionNode(int transition);

    /// <summary>Seconds since an instance entered its current state.</summary>
    protected internal abstract double ElapsedInState(InstanceBuffer instances, InstanceHandle instance);

    /// <summary>The per-instance memory a state declared, exactly as long as declared.</summary>
    protected internal abstract Span<byte> Memory(InstanceBuffer instances, InstanceHandle instance, int state);

    /// <summary>Reads a state's data port through the data plane.</summary>
    protected internal abstract T ReadState<T>(
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle instance,
        int state,
        int port)
        where T : unmanaged;

    /// <summary>Publishes a value on a state's data output through the data plane.</summary>
    protected internal abstract void WriteState<T>(
        InstanceBuffer instances,
        InstanceHandle instance,
        int state,
        int port,
        T value)
        where T : unmanaged;

    /// <summary>Reads a transition's data port through the data plane.</summary>
    protected internal abstract T ReadTransition<T>(
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle instance,
        int transition,
        int port)
        where T : unmanaged;

    /// <summary>Whether a transition has a trigger annotation.</summary>
    protected internal abstract bool HasTrigger(int transition);

    /// <summary>Whether a trigger was raised this tick; unknown triggers are never raised.</summary>
    protected internal abstract bool IsRaised(InstanceBuffer instances, InstanceHandle instance, StableId trigger);
}

/// <summary>What a state sees while it runs; ports are addressed by their index in declaration order.</summary>
public readonly ref struct StateContext
{
    readonly StateFrame frame;
    readonly int state;

    /// <summary>Creates the context of one state callback; backends call this, behaviors receive it.</summary>
    /// <param name="frame">The backend's storage and wiring.</param>
    /// <param name="evaluation">Ambient services.</param>
    /// <param name="instances">The instance buffer being ticked.</param>
    /// <param name="instance">The instance being ticked.</param>
    /// <param name="state">The state's index in <paramref name="frame"/>.</param>
    public StateContext(
        StateFrame frame,
        EvaluationContext evaluation,
        InstanceBuffer instances,
        InstanceHandle instance,
        int state)
    {
        this.frame = frame;
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
    public GraphNode Node => frame.StateNode(state);

    /// <summary>The state payload.</summary>
    public object? Payload => Node.Payload;

    /// <summary>Seconds since the machine entered this state.</summary>
    public double TimeInState => frame.ElapsedInState(Instances, Instance);

    /// <summary>Index of the port declared with <paramref name="key"/>.</summary>
    /// <exception cref="KeyNotFoundException">The node has no such port.</exception>
    public int PortIndex(StableId key) => StateMachineProgram.PortIndex(Node, key);

    /// <summary>Reads a data input through the data plane.</summary>
    /// <exception cref="InvalidOperationException">The port carries no data or there is no data plane.</exception>
    public T Read<T>(int port) where T : unmanaged => frame.ReadState<T>(Evaluation, Instances, Instance, state, port);

    /// <summary>Writes a data output through the data plane; values depending on it recompute.</summary>
    /// <exception cref="InvalidOperationException">The port carries no data or there is no data plane.</exception>
    public void Write<T>(int port, T value) where T : unmanaged => frame.WriteState(Instances, Instance, state, port, value);

    /// <summary>Returns this state's per-instance memory viewed as <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">The state type declared less memory than needed.</exception>
    public ref T Memory<T>() where T : unmanaged
    {
        var bytes = frame.Memory(Instances, Instance, state);
        if (Unsafe.SizeOf<T>() > bytes.Length)
        {
            throw new InvalidOperationException(
                $"'{Node.TypeId}' declares {bytes.Length} bytes of memory, not enough for {typeof(T).Name}.");
        }
        return ref MemoryMarshal.AsRef<T>(bytes[..Unsafe.SizeOf<T>()]);
    }
}

/// <summary>What a transition sees while it decides; ports are addressed by their index in declaration order.</summary>
public readonly ref struct TransitionContext
{
    readonly StateFrame frame;
    readonly int transition;

    /// <summary>Creates the context of one transition check; backends call this, behaviors receive it.</summary>
    /// <param name="frame">The backend's storage and wiring.</param>
    /// <param name="evaluation">Ambient services.</param>
    /// <param name="instances">The instance buffer being ticked.</param>
    /// <param name="instance">The instance being ticked.</param>
    /// <param name="transition">The transition's index in <paramref name="frame"/>.</param>
    public TransitionContext(
        StateFrame frame,
        EvaluationContext evaluation,
        InstanceBuffer instances,
        InstanceHandle instance,
        int transition)
    {
        this.frame = frame;
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
    public GraphNode Node => frame.TransitionNode(transition);

    /// <summary>The transition payload.</summary>
    public object? Payload => Node.Payload;

    /// <summary>Seconds since the machine entered the source state.</summary>
    public double TimeInState => frame.ElapsedInState(Instances, Instance);

    /// <summary>Whether the transition has a trigger annotation.</summary>
    public bool HasTrigger => frame.HasTrigger(transition);

    /// <summary>Whether a trigger was raised this tick; unknown triggers are never raised.</summary>
    public bool IsRaised(StableId trigger) => frame.IsRaised(Instances, Instance, trigger);

    /// <summary>Index of the port declared with <paramref name="key"/>.</summary>
    /// <exception cref="KeyNotFoundException">The node has no such port.</exception>
    public int PortIndex(StableId key) => StateMachineProgram.PortIndex(Node, key);

    /// <summary>Reads a data input through the data plane.</summary>
    /// <exception cref="InvalidOperationException">The port carries no data or there is no data plane.</exception>
    public T Read<T>(int port) where T : unmanaged =>
        frame.ReadTransition<T>(Evaluation, Instances, Instance, transition, port);
}
