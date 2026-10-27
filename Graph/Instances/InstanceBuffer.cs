using System.Runtime.InteropServices;

namespace MASS4.Graph;

/// <summary>Typed location of one per-instance value inside an <see cref="InstanceLayout"/>.</summary>
/// <typeparam name="T">The unmanaged value type.</typeparam>
/// <param name="Offset">Byte offset within an instance record.</param>
public readonly record struct InstanceSlot<T>(int Offset) where T : unmanaged;

/// <summary>
/// Collects per-instance needs declared by semantics (dataflow cache slots, current state and timers, node memory)
/// into a fixed record layout.
/// </summary>
public sealed class InstanceLayoutBuilder
{
    int size;
    int alignment = 1;
    int slots;

    /// <summary>Reserves an aligned slot for one value.</summary>
    public InstanceSlot<T> Add<T>() where T : unmanaged
    {
        var valueSize = Unsafe.SizeOf<T>();
        var valueAlignment = Math.Min(8, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)valueSize));
        alignment = Math.Max(alignment, valueAlignment);
        var offset = Align(size, valueAlignment);
        size = offset + valueSize;
        slots++;
        return new(offset);
    }

    /// <summary>Freezes the layout.</summary>
    public InstanceLayout Build() => new(Align(Math.Max(size, 1), alignment), slots);

    static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}

/// <summary>Fixed record layout of one graph instance.</summary>
/// <param name="Stride">Bytes per instance record, including alignment padding.</param>
/// <param name="SlotCount">Number of reserved slots.</param>
public sealed record InstanceLayout(int Stride, int SlotCount);

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
        generations = new int[capacity];
        free = new int[capacity];
    }

    /// <summary>The per-instance record layout.</summary>
    public InstanceLayout Layout { get; }

    /// <summary>Number of live instances.</summary>
    public int Count => used - freeCount;

    /// <summary>Instances the buffer can hold before growing.</summary>
    public int Capacity => generations.Length;

    /// <summary>Creates an instance with zeroed state.</summary>
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

    /// <summary>Destroys an instance; its handle becomes stale.</summary>
    /// <exception cref="ArgumentException">The handle is stale.</exception>
    public void Release(InstanceHandle handle)
    {
        Validate(handle);
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
        Array.Resize(ref generations, capacity);
        Array.Resize(ref free, capacity);
    }
}
