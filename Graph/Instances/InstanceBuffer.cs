using System.Runtime.InteropServices;

namespace MASS4.Graph;

/// <summary>Typed location of one per-instance value inside an <see cref="InstanceLayout"/>.</summary>
/// <typeparam name="T">The unmanaged value type.</typeparam>
/// <param name="Offset">Byte offset within an instance record.</param>
public readonly record struct InstanceSlot<T>(int Offset) where T : unmanaged;

/// <summary>Typed location of a fixed-length run of per-instance values, such as flags or timers.</summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
/// <param name="Offset">Byte offset of the first element within an instance record.</param>
/// <param name="Length">Number of elements.</param>
public readonly record struct InstanceArraySlot<T>(int Offset, int Length) where T : unmanaged;

/// <summary>Location of one per-instance object reference, such as a texture or a video frame.</summary>
/// <typeparam name="T">The referenced type.</typeparam>
/// <param name="Index">Index among the instance's reference slots.</param>
public readonly record struct InstanceReferenceSlot<T>(int Index) where T : class;

/// <summary>
/// Collects per-instance needs declared by semantics (dataflow cache slots, current state and timers, node memory)
/// into a fixed record layout.
/// </summary>
public sealed class InstanceLayoutBuilder
{
    int size;
    int alignment = 1;
    int slots;
    int references;

    /// <summary>Reserves an aligned slot for one value.</summary>
    public InstanceSlot<T> Add<T>() where T : unmanaged
    {
        slots++;
        return new(Reserve(Unsafe.SizeOf<T>(), 1));
    }

    /// <summary>Reserves an aligned run of <paramref name="length"/> values.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The length is negative.</exception>
    public InstanceArraySlot<T> AddArray<T>(int length) where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        slots++;
        return new(Reserve(Unsafe.SizeOf<T>(), length), length);
    }

    /// <summary>Reserves a slot for one object reference.</summary>
    public InstanceReferenceSlot<T> AddReference<T>() where T : class
    {
        slots++;
        return new(references++);
    }

    /// <summary>Freezes the layout.</summary>
    public InstanceLayout Build() => new(Align(Math.Max(size, 1), alignment), slots) { ReferenceCount = references };

    int Reserve(int elementSize, int length)
    {
        var elementAlignment = Math.Min(8, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)elementSize));
        alignment = Math.Max(alignment, elementAlignment);
        var offset = Align(size, elementAlignment);
        size = offset + elementSize * length;
        return offset;
    }

    static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}

/// <summary>Fixed record layout of one graph instance.</summary>
/// <param name="Stride">Bytes per instance record, including alignment padding.</param>
/// <param name="SlotCount">Number of reserved slots.</param>
public sealed record InstanceLayout(int Stride, int SlotCount)
{
    /// <summary>Number of object reference slots per instance.</summary>
    public int ReferenceCount { get; init; }
}

/// <summary>Identifies a live instance; stale handles to released instances are rejected.</summary>
/// <param name="Index">Record index in the buffer.</param>
/// <param name="Generation">Reuse counter of the record.</param>
public readonly record struct InstanceHandle(int Index, int Generation);

/// <summary>
/// Per-instance state of many instances of one graph asset, stored contiguously. Accessing slots allocates nothing;
/// only growing the capacity does.
/// </summary>
public sealed class InstanceBuffer
{
    byte[] storage;
    object?[] references;
    int[] generations;
    int[] free;
    int freeCount;
    int used;

    /// <summary>Creates a buffer for a layout.</summary>
    /// <param name="layout">The per-instance record layout.</param>
    /// <param name="capacity">Instances to reserve storage for up front.</param>
    public InstanceBuffer(InstanceLayout layout, int capacity = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Layout = layout;
        storage = new byte[layout.Stride * capacity];
        references = new object?[layout.ReferenceCount * capacity];
        generations = new int[capacity];
        free = new int[capacity];
    }

    /// <summary>The per-instance record layout.</summary>
    public InstanceLayout Layout { get; }

    /// <summary>Number of live instances.</summary>
    public int Count => used - freeCount;

    /// <summary>Instances the buffer can hold before growing.</summary>
    public int Capacity => generations.Length;

    /// <summary>Creates an instance with zeroed state and empty references.</summary>
    public InstanceHandle Allocate()
    {
        int index;
        if (freeCount > 0)
        {
            index = free[--freeCount];
        }
        else
        {
            if (used == Capacity)
            {
                Grow();
            }
            index = used++;
        }
        Record(index).Clear();
        return new(index, generations[index]);
    }

    /// <summary>Destroys an instance; its handle becomes stale and its references are released.</summary>
    /// <exception cref="ArgumentException">The handle is stale.</exception>
    public void Release(InstanceHandle handle)
    {
        Validate(handle);
        Array.Clear(references, handle.Index * Layout.ReferenceCount, Layout.ReferenceCount);
        generations[handle.Index]++;
        free[freeCount++] = handle.Index;
    }

    /// <summary>Whether the handle refers to a live instance.</summary>
    public bool IsAlive(InstanceHandle handle) =>
        (uint)handle.Index < (uint)used && generations[handle.Index] == handle.Generation;

    /// <summary>Returns a reference to one slot of an instance.</summary>
    /// <exception cref="ArgumentException">The handle is stale.</exception>
    public ref T Get<T>(InstanceHandle handle, InstanceSlot<T> slot) where T : unmanaged
    {
        Validate(handle);
        return ref MemoryMarshal.AsRef<T>(Record(handle.Index).Slice(slot.Offset, Unsafe.SizeOf<T>()));
    }

    /// <summary>Returns the elements of an array slot of an instance.</summary>
    /// <exception cref="ArgumentException">The handle is stale.</exception>
    public Span<T> GetSpan<T>(InstanceHandle handle, InstanceArraySlot<T> slot) where T : unmanaged
    {
        Validate(handle);
        var bytes = Record(handle.Index).Slice(slot.Offset, slot.Length * Unsafe.SizeOf<T>());
        return MemoryMarshal.Cast<byte, T>(bytes);
    }

    /// <summary>Reads a reference slot of an instance.</summary>
    /// <exception cref="ArgumentException">The handle is stale.</exception>
    public T? GetReference<T>(InstanceHandle handle, InstanceReferenceSlot<T> slot) where T : class
    {
        Validate(handle);
        return (T?)references[handle.Index * Layout.ReferenceCount + slot.Index];
    }

    /// <summary>Writes a reference slot of an instance.</summary>
    /// <exception cref="ArgumentException">The handle is stale.</exception>
    public void SetReference<T>(InstanceHandle handle, InstanceReferenceSlot<T> slot, T? value) where T : class
    {
        Validate(handle);
        references[handle.Index * Layout.ReferenceCount + slot.Index] = value;
    }

    /// <summary>Returns the raw record of an instance.</summary>
    /// <exception cref="ArgumentException">The handle is stale.</exception>
    public Span<byte> GetRecord(InstanceHandle handle)
    {
        Validate(handle);
        return Record(handle.Index);
    }

    Span<byte> Record(int index) => storage.AsSpan(index * Layout.Stride, Layout.Stride);

    void Validate(InstanceHandle handle)
    {
        if ((uint)handle.Index >= (uint)used || generations[handle.Index] != handle.Generation)
        {
            throw new ArgumentException("The instance handle is stale.", nameof(handle));
        }
    }

    void Grow()
    {
        var capacity = Math.Max(4, Capacity * 2);
        Array.Resize(ref storage, capacity * Layout.Stride);
        Array.Resize(ref references, capacity * Layout.ReferenceCount);
        Array.Resize(ref generations, capacity);
        Array.Resize(ref free, capacity);
    }
}
