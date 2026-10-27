namespace MASS4.Graph.Interpreter;

/// <summary>
/// A document compiled by <see cref="GraphInterpreter"/>. One evaluation checks the required features, starts a new
/// data evaluation, ticks the state machine by the context's <see cref="StateTime"/>, then runs control tick entries;
/// it completes synchronously and allocates nothing.
/// </summary>
public sealed class InterpretedGraph : ICompiledGraph
{
    readonly (ElementId Port, int Index)[] ports;

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
        RequiredFeatures = Features(data, stateMachine, control);
        ports = [.. document.Nodes
            .SelectMany(static node => node.Ports)
            .Where(static port => port.Direction == PortDirection.Output)
            .OrderBy(static port => port.Id)
            .Select(port => (port.Id, data.TryResolve(port.Id, out var index) ? index : -1))
            .Where(static output => output.Item2 >= 0)];
    }

    static Type[] Features(DataflowProgram data, StateMachineProgram? stateMachine, ControlProgram? control)
    {
        IEnumerable<Type> features = data.RequiredFeatures;
        if (stateMachine is not null)
        {
            features = features.Concat(stateMachine.RequiredFeatures).Append(typeof(StateTime));
        }
        if (control is not null)
        {
            features = features.Concat(control.RequiredFeatures);
        }
        return [.. features.Distinct()];
    }

    /// <inheritdoc />
    public StableId AssetId { get; }

    /// <inheritdoc />
    public InstanceLayout InstanceLayout { get; }

    /// <inheritdoc />
    /// <remarks>Includes <see cref="StateTime"/> whenever the document contains a state machine.</remarks>
    public IReadOnlyList<Type> RequiredFeatures { get; }

    /// <inheritdoc />
    public IReadOnlyList<ElementId> Outputs => Data.Outputs;

    /// <summary>The dataflow program, which is also the data plane of the other programs.</summary>
    public DataflowProgram Data { get; }

    /// <summary>The state machine program, when the document contains one.</summary>
    public StateMachineProgram? StateMachine { get; }

    /// <summary>The control program, when the document contains control flow.</summary>
    public ControlProgram? Control { get; }

    /// <inheritdoc />
    public void Initialize(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance)
    {
        context.Require(RequiredFeatures);
        Data.Initialize(instances, instance);
        StateMachine?.Initialize(context, instances, instance);
    }

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException">The context's cancellation was requested.</exception>
    /// <exception cref="InvalidOperationException">Control flow exceeded its step or stack limit.</exception>
    public ValueTask EvaluateAsync(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        context.Require(RequiredFeatures);
        Data.BeginEvaluation(instances, instance);
        if (StateMachine is not null)
        {
            StateMachine.Tick(context, instances, instance, context.Get<StateTime>().DeltaSeconds);
        }
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
    public bool SetVariable<T>(InstanceBuffer instances, InstanceHandle instance, ElementId variable, T value)
        where T : unmanaged => Data.TrySetVariable(instances, instance, variable, value);

    /// <inheritdoc />
    public T ReadOutput<T>(
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle instance,
        ElementId output)
        where T : unmanaged => Data.ReadOutput<T>(context, instances, instance, output);

    /// <inheritdoc />
    public T? ReadOutputReference<T>(
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle instance,
        ElementId output)
        where T : class => Data.ReadOutputReference<T>(context, instances, instance, output);

    /// <inheritdoc />
    /// <remarks>Reports declared outputs under their variable identities, then every data output port.</remarks>
    public void Observe(
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle instance,
        ICollection<GraphObservation> observations)
    {
        foreach (var output in Data.Outputs)
        {
            observations.Add(new(output, Data.ReadOutputBoxed(context, instances, instance, output)));
        }
        foreach (var (port, index) in ports)
        {
            observations.Add(new(port, Data.ReadBoxed(context, instances, instance, index)));
        }
        if (StateMachine is not null && StateMachine.TryGetCurrentState(instances, instance, out var state))
        {
            observations.Add(new(StateMachine.EntryNodeId, state.Id));
        }
    }
}
