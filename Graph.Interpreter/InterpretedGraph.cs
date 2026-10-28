namespace MASS4.Graph.Interpreter;

/// <summary>
/// A document compiled by <see cref="GraphInterpreter"/>. One evaluation starts a new data evaluation, ticks the state
/// machine by the context's <see cref="StateTime"/>, then runs control tick entries; it completes synchronously and
/// allocates nothing.
/// </summary>
public sealed class InterpretedGraph : ICompiledGraph
{
    readonly (ElementId Port, int Index)[] outputs;

    internal InterpretedGraph(
        GraphDocument document,
        InstanceLayout layout,
        DataflowProgram data,
        StateMachineProgram? stateMachine,
        ControlProgram? control)
    {
        AssetId = document.AssetId;
        InstanceLayout = layout;
        Data = data;
        StateMachine = stateMachine;
        Control = control;
        outputs = [.. document.Nodes
            .SelectMany(static node => node.Ports)
            .Where(static port => port.Direction == PortDirection.Output)
            .OrderBy(static port => port.Id)
            .Select(port => (port.Id, data.TryResolve(port.Id, out var index) ? index : -1))
            .Where(static output => output.Item2 >= 0)];
    }

    /// <inheritdoc />
    public StableId AssetId { get; }

    /// <inheritdoc />
    public InstanceLayout InstanceLayout { get; }

    /// <summary>The dataflow program, which is also the data plane of the other programs.</summary>
    public DataflowProgram Data { get; }

    /// <summary>The state machine program, when the document contains one.</summary>
    public StateMachineProgram? StateMachine { get; }

    /// <summary>The control program, when the document contains control flow.</summary>
    public ControlProgram? Control { get; }

    /// <inheritdoc />
    public void Initialize(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance)
    {
        Data.Initialize(instances, instance);
        StateMachine?.Initialize(context, instances, instance);
    }

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException">The context's cancellation was requested.</exception>
    /// <exception cref="InvalidOperationException">Control flow exceeded its step or stack limit.</exception>
    public ValueTask EvaluateAsync(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        Data.BeginEvaluation(instances, instance);
        var delta = context.TryGet<StateTime>(out var time) ? time.DeltaSeconds : 0;
        StateMachine?.Tick(context, instances, instance, delta);
        if (Control?.Tick(context, instances, instance) is { } result and not ControlRunResult.Completed)
        {
            throw new InvalidOperationException($"Control flow stopped: {result}.");
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public bool Raise(InstanceBuffer instances, InstanceHandle instance, StableId signal)
    {
        if (StateMachine is null || !StateMachine.TryResolveTrigger(signal, out var trigger))
        {
            return false;
        }
        StateMachine.Raise(instances, instance, trigger);
        return true;
    }

    /// <inheritdoc />
    public bool SetVariable(InstanceBuffer instances, InstanceHandle instance, ElementId variable, object? value) =>
        Data.TrySetVariable(instances, instance, variable, value);

    /// <inheritdoc />
    public void Observe(
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle instance,
        ICollection<GraphObservation> observations)
    {
        foreach (var (port, index) in outputs)
        {
            observations.Add(new(port, Data.ReadBoxed(context, instances, instance, index)));
        }
        if (StateMachine is not null && StateMachine.TryGetCurrentState(instances, instance, out var state))
        {
            observations.Add(new(StateMachine.EntryNodeId, state.Id));
        }
    }
}
