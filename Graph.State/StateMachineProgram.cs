namespace MASS4.Graph.State;

/// <summary>
/// The executable state machine of one document. Each tick checks the transitions of the current state in priority
/// order, takes at most one, updates the current state and then clears raised triggers. One program serves every
/// instance; ticking different instances concurrently is safe.
/// </summary>
public sealed class StateMachineProgram
{
    readonly IdentityRegistry<SignalType> signals;
    readonly StateEntry[] states;
    readonly TransitionEntry[] transitions;
    readonly int[] dataPorts;
    readonly int initial;
    readonly Dictionary<StableId, int> triggerIndex;
    readonly InstanceSlot<int> current;
    readonly InstanceSlot<double> time;
    readonly InstanceArraySlot<byte> raised;
    readonly IDataPlane? data;

    internal StateMachineProgram(
        IdentityRegistry<SignalType> signals,
        StateEntry[] states,
        TransitionEntry[] transitions,
        int[] dataPorts,
        int initial,
        Dictionary<StableId, int> triggerIndex,
        InstanceLayoutBuilder layout,
        IDataPlane? data,
        ElementId entryNodeId)
    {
        EntryNodeId = entryNodeId;
        this.signals = signals;
        this.states = states;
        this.transitions = transitions;
        this.dataPorts = dataPorts;
        this.initial = initial;
        this.triggerIndex = triggerIndex;
        this.data = data;
        current = layout.Add<int>();
        time = layout.Add<double>();
        raised = layout.AddArray<byte>(triggerIndex.Count);
        RequiredFeatures =
        [
            .. states.SelectMany(static state => state.Behavior.RequiredFeatures)
                .Concat(transitions.SelectMany(static transition => transition.Behavior.RequiredFeatures))
                .Distinct(),
        ];
    }

    /// <summary>Number of states.</summary>
    public int StateCount => states.Length;

    /// <summary>Context features the document's states and transitions read.</summary>
    public IReadOnlyList<Type> RequiredFeatures { get; }

    /// <summary>Identity of the entry node; tools report the machine's current state under it.</summary>
    public ElementId EntryNodeId { get; }

    /// <summary>Resolves a trigger, following renames; false for triggers not registered at compile time.</summary>
    public bool TryResolveTrigger(StableId trigger, out TriggerHandle handle)
    {
        var found = triggerIndex.TryGetValue(signals.Canonicalize(trigger), out var index);
        handle = new(index);
        return found;
    }

    /// <summary>Resolves a trigger or throws.</summary>
    /// <exception cref="KeyNotFoundException">The trigger was not registered at compile time.</exception>
    public TriggerHandle ResolveTrigger(StableId trigger) =>
        TryResolveTrigger(trigger, out var handle)
            ? handle
            : throw new KeyNotFoundException($"Trigger {trigger} is not registered.");

    /// <summary>Enters the initial state; <see cref="Tick"/> does this on first use.</summary>
    public void Initialize(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance)
    {
        data?.BeginEvaluation(instances, instance);
        instances.Get(instance, current) = initial + 1;
        instances.Get(instance, time) = 0;
        states[initial].Behavior.OnEnter(new(this, context, instances, instance, initial));
    }

    /// <summary>Raises a trigger for the next tick of an instance.</summary>
    public void Raise(InstanceBuffer instances, InstanceHandle instance, TriggerHandle trigger) =>
        instances.GetSpan(instance, raised)[trigger.Index] = 1;

    /// <summary>Advances an instance by <paramref name="deltaSeconds"/>.</summary>
    /// <returns>Whether the machine changed state.</returns>
    public bool Tick(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance, double deltaSeconds)
    {
        if (instances.Get(instance, current) == 0)
        {
            Initialize(context, instances, instance);
        }
        else
        {
            data?.BeginEvaluation(instances, instance);
        }
        instances.Get(instance, time) += deltaSeconds;
        var state = instances.Get(instance, current) - 1;
        var entry = states[state];
        var changed = false;
        for (var t = entry.FirstTransition; t < entry.FirstTransition + entry.TransitionCount && !changed; t++)
        {
            if (transitions[t].Behavior.ShouldFire(new(this, context, instances, instance, t)))
            {
                Change(context, instances, instance, state, transitions[t].Target);
                changed = true;
            }
        }
        var now = instances.Get(instance, current) - 1;
        states[now].Behavior.OnUpdate(new(this, context, instances, instance, now));
        instances.GetSpan(instance, raised).Clear();
        return changed;
    }

    /// <summary>The current state node of an instance.</summary>
    /// <exception cref="InvalidOperationException">The instance was neither initialized nor ticked.</exception>
    public GraphNode CurrentState(InstanceBuffer instances, InstanceHandle instance) =>
        TryGetCurrentState(instances, instance, out var state)
            ? state
            : throw new InvalidOperationException("The instance has not started.");

    /// <summary>Finds the current state node of an instance; false before the instance started.</summary>
    public bool TryGetCurrentState(
        InstanceBuffer instances,
        InstanceHandle instance,
        [NotNullWhen(true)] out GraphNode? state)
    {
        var index = instances.Get(instance, current) - 1;
        state = index >= 0 ? states[index].Node : null;
        return state is not null;
    }

    /// <summary>Seconds since an instance entered its current state.</summary>
    public double TimeInState(InstanceBuffer instances, InstanceHandle instance) => instances.Get(instance, time);

    internal StateEntry StateAt(int state) => states[state];

    internal TransitionEntry TransitionAt(int transition) => transitions[transition];

    internal bool IsRaised(InstanceBuffer instances, InstanceHandle instance, int trigger) =>
        instances.GetSpan(instance, raised)[trigger] != 0;

    internal IDataPlane Data(int flatPort, out int index)
    {
        index = dataPorts[flatPort];
        if (data is null || index < 0)
        {
            throw new InvalidOperationException("The port carries no data, or the program has no data plane.");
        }
        return data;
    }

    internal static int PortIndex(GraphNode node, StableId key)
    {
        for (var i = 0; i < node.Ports.Count; i++)
        {
            if (node.Ports[i].Declaration.Key == key)
            {
                return i;
            }
        }
        throw new KeyNotFoundException($"Node {node.Id} has no port {key}.");
    }

    void Change(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance, int from, int to)
    {
        states[from].Behavior.OnExit(new(this, context, instances, instance, from));
        instances.Get(instance, current) = to + 1;
        instances.Get(instance, time) = 0;
        states[to].Behavior.OnEnter(new(this, context, instances, instance, to));
    }
}

/// <summary>One state of a compiled machine with its outgoing transitions in check order.</summary>
readonly record struct StateEntry(
    GraphNode Node,
    IStateNode Behavior,
    int FirstPort,
    int MemoryOffset,
    int MemorySize,
    int FirstTransition,
    int TransitionCount);

/// <summary>One transition of a compiled machine; a negative trigger means none.</summary>
readonly record struct TransitionEntry(
    GraphNode Node,
    ITransitionNode Behavior,
    int FirstPort,
    int Target,
    int Trigger);
