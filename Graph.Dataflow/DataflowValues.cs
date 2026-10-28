namespace MASS4.Graph.Dataflow;

/// <summary>How the values of a dataflow type are stored per instance.</summary>
public enum ValueStorage
{
    /// <summary>Inline in the instance record; reading and writing never allocates.</summary>
    Unmanaged,

    /// <summary>As an object reference, for values such as textures or video frames.</summary>
    Reference,
}

/// <summary>A port type the dataflow semantics can store per instance and pass between nodes.</summary>
public abstract class DataflowValueType
{
    private protected DataflowValueType(StableId portTypeId, Type clrType, ValueStorage storage)
    {
        PortTypeId = portTypeId;
        ClrType = clrType;
        Storage = storage;
    }

    /// <summary>Identity of the port type.</summary>
    public StableId PortTypeId { get; }

    /// <summary>CLR type node implementations read and write for this port type.</summary>
    public Type ClrType { get; }

    /// <summary>How values are stored.</summary>
    public ValueStorage Storage { get; }

    internal abstract ValueLocation Allocate(InstanceLayoutBuilder layout);

    internal abstract void Copy(InstanceBuffer instances, InstanceHandle instance, int source, int target);

    /// <summary>Reads a value as an object; boxes unmanaged values, so keep it off per-tick paths.</summary>
    internal abstract object? ReadBoxed(InstanceBuffer instances, InstanceHandle instance, int offset);

    /// <summary>Writes an object value; anything not of the stored type writes the default.</summary>
    internal abstract void WriteBoxed(InstanceBuffer instances, InstanceHandle instance, int offset, object? value);

    /// <summary>Whether an object value can be stored as this type.</summary>
    internal abstract bool Accepts(object? value);
}

sealed class UnmanagedValueType<T>(StableId portTypeId)
    : DataflowValueType(portTypeId, typeof(T), ValueStorage.Unmanaged) where T : unmanaged
{
    internal override ValueLocation Allocate(InstanceLayoutBuilder layout) => new(this, layout.Add<T>().Offset);

    internal override void Copy(InstanceBuffer instances, InstanceHandle instance, int source, int target) =>
        instances.Get(instance, new InstanceSlot<T>(target)) = instances.Get(instance, new InstanceSlot<T>(source));

    internal override object? ReadBoxed(InstanceBuffer instances, InstanceHandle instance, int offset) =>
        instances.Get(instance, new InstanceSlot<T>(offset));

    internal override void WriteBoxed(InstanceBuffer instances, InstanceHandle instance, int offset, object? value) =>
        instances.Get(instance, new InstanceSlot<T>(offset)) = value is T typed ? typed : default;

    internal override bool Accepts(object? value) => value is T;
}

sealed class ReferenceValueType<T>(StableId portTypeId)
    : DataflowValueType(portTypeId, typeof(T), ValueStorage.Reference) where T : class
{
    internal override ValueLocation Allocate(InstanceLayoutBuilder layout) =>
        new(this, layout.AddReference<T>().Index);

    internal override void Copy(InstanceBuffer instances, InstanceHandle instance, int source, int target) =>
        instances.SetReference(instance, new InstanceReferenceSlot<T>(target),
            instances.GetReference(instance, new InstanceReferenceSlot<T>(source)));

    internal override object? ReadBoxed(InstanceBuffer instances, InstanceHandle instance, int offset) =>
        instances.GetReference(instance, new InstanceReferenceSlot<T>(offset));

    internal override void WriteBoxed(InstanceBuffer instances, InstanceHandle instance, int offset, object? value) =>
        instances.SetReference(instance, new InstanceReferenceSlot<T>(offset), value as T);

    internal override bool Accepts(object? value) => value is null or T;
}

/// <summary>Where one port value lives in an instance: a byte offset or a reference index.</summary>
/// <param name="Type">The stored type.</param>
/// <param name="Offset">Byte offset for unmanaged values, reference index otherwise.</param>
readonly record struct ValueLocation(DataflowValueType Type, int Offset)
{
    public T Read<T>(InstanceBuffer instances, InstanceHandle instance) where T : unmanaged
    {
        Check<T>(ValueStorage.Unmanaged);
        return instances.Get(instance, new InstanceSlot<T>(Offset));
    }

    public void Write<T>(InstanceBuffer instances, InstanceHandle instance, T value) where T : unmanaged
    {
        Check<T>(ValueStorage.Unmanaged);
        instances.Get(instance, new InstanceSlot<T>(Offset)) = value;
    }

    public T? ReadReference<T>(InstanceBuffer instances, InstanceHandle instance) where T : class
    {
        Check<T>(ValueStorage.Reference);
        return instances.GetReference(instance, new InstanceReferenceSlot<T>(Offset));
    }

    public void WriteReference<T>(InstanceBuffer instances, InstanceHandle instance, T? value) where T : class
    {
        Check<T>(ValueStorage.Reference);
        instances.SetReference(instance, new InstanceReferenceSlot<T>(Offset), value);
    }

    void Check<T>(ValueStorage storage)
    {
        if (Type.Storage != storage || Type.ClrType != typeof(T))
        {
            throw new InvalidOperationException(
                $"Port type {Type.PortTypeId} stores {Type.ClrType.Name}, not {typeof(T).Name}.");
        }
    }
}

/// <summary>Converts a stored value between two unmanaged port types at evaluation time.</summary>
abstract class Coercion
{
    public abstract void Apply(InstanceBuffer instances, InstanceHandle instance, int source, int target);
}

sealed class Coercion<TFrom, TTo>(Func<TFrom, TTo> convert) : Coercion where TFrom : unmanaged where TTo : unmanaged
{
    public override void Apply(InstanceBuffer instances, InstanceHandle instance, int source, int target) =>
        instances.Get(instance, new InstanceSlot<TTo>(target)) =
            convert(instances.Get(instance, new InstanceSlot<TFrom>(source)));
}
