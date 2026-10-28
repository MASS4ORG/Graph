namespace MASS4.Graph;

/// <summary>
/// Typed access to data-port values of one graph instance. Dataflow semantics implement it; control and state
/// semantics read guard inputs and write data outputs through it, so the semantics compose in one document without
/// referencing each other.
/// </summary>
public interface IDataPlane
{
    /// <summary>Finds the data-plane index of a port; false for ports that carry no data.</summary>
    bool TryResolve(ElementId portId, out int port);

    /// <summary>Starts a new evaluation of an instance, so values that may change recompute.</summary>
    void BeginEvaluation(InstanceBuffer instances, InstanceHandle instance);

    /// <summary>Returns the current value of a port, evaluating whatever it depends on.</summary>
    T Read<T>(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance, int port)
        where T : unmanaged;

    /// <summary>Returns the current object value of a port, evaluating whatever it depends on.</summary>
    T? ReadReference<T>(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance, int port)
        where T : class;

    /// <summary>Sets an output value produced outside the data plane; values depending on it recompute.</summary>
    void Write<T>(InstanceBuffer instances, InstanceHandle instance, int port, T value) where T : unmanaged;

    /// <summary>Sets an object output value produced outside the data plane; dependents recompute.</summary>
    void WriteReference<T>(InstanceBuffer instances, InstanceHandle instance, int port, T? value) where T : class;
}
