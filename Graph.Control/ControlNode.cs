using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MASS4.Graph.Control;

/// <summary>
/// Runs one node type when execution reaches it. Implementations are stateless and shared; per-instance memory is
/// requested through <see cref="MemorySize"/> and reached through <see cref="ControlContext.Memory{T}"/>.
/// </summary>
public interface IControlNode
{
    /// <summary>Whether <see cref="ControlProgram.Tick"/> starts execution at this node.</summary>
    bool IsTickEntry => false;

    /// <summary>Bytes of per-instance memory each node of this type keeps between runs.</summary>
    int MemorySize => 0;

    /// <summary>Evaluation-context features the node reads; backends refuse to run without them.</summary>
    IReadOnlyList<Type> RequiredFeatures => [];

    /// <summary>Does the node's work and chooses which exec outputs continue; none ends the branch.</summary>
    void Execute(ref ControlContext context);
}

/// <summary>Executes a node through a context; used by <see cref="ControlNode"/>.</summary>
public delegate void ControlExecutor(ref ControlContext context);

/// <summary>A node behavior built from a delegate.</summary>
/// <param name="execute">Does the work and chooses continuations.</param>
/// <param name="isTickEntry">Whether ticks start at this node.</param>
/// <param name="memorySize">Bytes of per-instance memory.</param>
/// <param name="requires">Context features the node reads.</param>
public sealed class ControlNode(
    ControlExecutor execute,
    bool isTickEntry = false,
    int memorySize = 0,
    params Type[] requires) : IControlNode
{
    /// <inheritdoc />
    public bool IsTickEntry { get; } = isTickEntry;

    /// <inheritdoc />
    public int MemorySize { get; } = memorySize;

    /// <inheritdoc />
    public IReadOnlyList<Type> RequiredFeatures { get; } = requires;

    /// <inheritdoc />
    public void Execute(ref ControlContext context) => execute(ref context);
}

/// <summary>What a node sees while it executes; ports are addressed by their index in declaration order.</summary>
public ref struct ControlContext
{
    readonly ControlProgram program;
    readonly int node;
    readonly Span<int> continuations;
    int continued;

    internal ControlContext(
        ControlProgram program,
        EvaluationContext evaluation,
        InstanceBuffer instances,
        InstanceHandle instance,
        int node,
        Span<int> continuations)
    {
        this.program = program;
        this.node = node;
        this.continuations = continuations;
        continued = 0;
        Evaluation = evaluation;
        Instances = instances;
        Instance = instance;
    }

    /// <summary>Ambient services such as cancellation and typed features.</summary>
    public EvaluationContext Evaluation { get; }

    /// <summary>The instance buffer being run.</summary>
    public InstanceBuffer Instances { get; }

    /// <summary>The instance being run.</summary>
    public InstanceHandle Instance { get; }

    /// <summary>The node being executed.</summary>
    public readonly GraphNode Node => program.NodeAt(node);

    /// <summary>The node payload.</summary>
    public readonly object? Payload => Node.Payload;

    internal readonly int Continued => continued;

    internal readonly int ContinuationAt(int index) => continuations[index];

    /// <summary>Reads a data input through the data plane.</summary>
    /// <exception cref="InvalidOperationException">The port carries no data or there is no data plane.</exception>
    public readonly T Read<T>(int port) where T : unmanaged =>
        program.Data(node, port, out var index).Read<T>(Evaluation, Instances, Instance, index);

    /// <summary>Writes a data output through the data plane; values depending on it recompute.</summary>
    /// <exception cref="InvalidOperationException">The port carries no data or there is no data plane.</exception>
    public readonly void Write<T>(int port, T value) where T : unmanaged =>
        program.Data(node, port, out var index).Write(Instances, Instance, index, value);

    /// <summary>Returns this node's per-instance memory viewed as <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException">The node type declared less memory than needed.</exception>
    public readonly ref T Memory<T>() where T : unmanaged
    {
        var (offset, size) = program.MemoryOf(node);
        if (Unsafe.SizeOf<T>() > size)
        {
            throw new InvalidOperationException(
                $"'{Node.TypeId}' declares {size} bytes of memory, not enough for {typeof(T).Name}.");
        }
        return ref MemoryMarshal.AsRef<T>(Instances.GetRecord(Instance).Slice(offset, Unsafe.SizeOf<T>()));
    }

    /// <summary>Continues execution through an exec output once this node returns, in call order.</summary>
    /// <exception cref="InvalidOperationException">Not an exec output, or too many continuations.</exception>
    public void Continue(int port)
    {
        program.CheckExecOutput(node, port);
        if (continued == continuations.Length)
        {
            throw new InvalidOperationException($"A node may continue at most {continuations.Length} exec outputs.");
        }
        continuations[continued++] = port;
    }
}
