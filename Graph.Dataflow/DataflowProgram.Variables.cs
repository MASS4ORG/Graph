namespace MASS4.Graph.Dataflow;

public sealed partial class DataflowProgram
{
    /// <summary>Identities of the blackboard variables this program stores per instance.</summary>
    public IEnumerable<ElementId> Variables => variables.Keys;

    /// <summary>
    /// Resets an instance's variables to their document defaults and forgets cached values. Instances initialize
    /// themselves before their first read, write or evaluation, so calling this is only needed to reset.
    /// </summary>
    public void Initialize(InstanceBuffer instances, InstanceHandle instance)
    {
        foreach (var (location, value) in variables.Values)
        {
            location.Type.WriteBoxed(instances, instance, location.Offset, value);
        }
        instances.Get(instance, initialized) = 1;
        Invalidate(instances, instance);
    }

    /// <summary>Returns an instance's value of a variable.</summary>
    /// <exception cref="KeyNotFoundException">The program stores no such variable.</exception>
    /// <exception cref="InvalidOperationException">The variable is not of type <typeparamref name="T"/>.</exception>
    public T GetVariable<T>(InstanceBuffer instances, InstanceHandle instance, ElementId variableId)
        where T : unmanaged
    {
        EnsureInitialized(instances, instance);
        return VariableAt(variableId).Read<T>(instances, instance);
    }

    /// <summary>Returns an instance's object value of a variable.</summary>
    /// <exception cref="KeyNotFoundException">The program stores no such variable.</exception>
    /// <exception cref="InvalidOperationException">The variable is not of type <typeparamref name="T"/>.</exception>
    public T? GetVariableReference<T>(InstanceBuffer instances, InstanceHandle instance, ElementId variableId)
        where T : class
    {
        EnsureInitialized(instances, instance);
        return VariableAt(variableId).ReadReference<T>(instances, instance);
    }

    /// <summary>
    /// Sets an instance's value of a variable. Every cached value of the instance is forgotten, because any node,
    /// including ones otherwise computed once per instance, may depend on it.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The program stores no such variable.</exception>
    /// <exception cref="InvalidOperationException">The variable is not of type <typeparamref name="T"/>.</exception>
    public void SetVariable<T>(InstanceBuffer instances, InstanceHandle instance, ElementId variableId, T value)
        where T : unmanaged
    {
        EnsureInitialized(instances, instance);
        VariableAt(variableId).Write(instances, instance, value);
        Invalidate(instances, instance);
    }

    /// <summary>Sets an instance's object value of a variable and forgets every cached value of the instance.</summary>
    /// <exception cref="KeyNotFoundException">The program stores no such variable.</exception>
    /// <exception cref="InvalidOperationException">The variable is not of type <typeparamref name="T"/>.</exception>
    public void SetVariableReference<T>(
        InstanceBuffer instances,
        InstanceHandle instance,
        ElementId variableId,
        T? value)
        where T : class
    {
        EnsureInitialized(instances, instance);
        VariableAt(variableId).WriteReference(instances, instance, value);
        Invalidate(instances, instance);
    }

    /// <summary>Sets a variable from an untyped value, for hosts and tools; false when it cannot be stored.</summary>
    public bool TrySetVariable(InstanceBuffer instances, InstanceHandle instance, ElementId variableId, object? value)
    {
        if (!variables.TryGetValue(variableId, out var slot) || !slot.Location.Type.Accepts(value))
        {
            return false;
        }
        EnsureInitialized(instances, instance);
        slot.Location.Type.WriteBoxed(instances, instance, slot.Location.Offset, value);
        Invalidate(instances, instance);
        return true;
    }

    /// <summary>Returns the current value of a data port as an object, for tools and differential testing.</summary>
    /// <remarks>Unmanaged values are boxed; keep this off per-tick paths.</remarks>
    public object? ReadBoxed(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance, int port)
    {
        Prepare(context, instances, instance, port);
        var entry = ports[port];
        var location = LocationOf(port);
        if (entry.Coercion is { } coercion)
        {
            coercion.Apply(instances, instance, entry.CoercionSource, location.Offset);
        }
        return location.Type.ReadBoxed(instances, instance, location.Offset);
    }

    void EnsureInitialized(InstanceBuffer instances, InstanceHandle instance)
    {
        if (instances.Get(instance, initialized) == 0)
        {
            Initialize(instances, instance);
        }
    }

    ValueLocation VariableAt(ElementId variableId) =>
        variables.TryGetValue(variableId, out var slot)
            ? slot.Location
            : throw new KeyNotFoundException($"Variable {variableId} is not stored by this program.");
}
