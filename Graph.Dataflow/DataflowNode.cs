namespace MASS4.Graph.Dataflow;

/// <summary>
/// Computes the outputs of one node type from its inputs. Implementations are stateless and shared by every node and
/// instance; per-instance values live in the instance buffer.
/// </summary>
public interface IDataflowNode
{
    /// <summary>
    /// Whether outputs may change between evaluations with unchanged inputs (time, randomness, host reads). Volatile
    /// nodes and everything downstream recompute once per evaluation; other nodes compute once per instance.
    /// </summary>
    bool IsVolatile => false;

    /// <summary>
    /// Evaluation-context features the node reads with <see cref="EvaluationContext.Get{T}"/>; backends refuse to
    /// evaluate when the host has not provided them.
    /// </summary>
    IReadOnlyList<Type> RequiredFeatures => [];

    /// <summary>Reads inputs and writes outputs through the context.</summary>
    void Evaluate(DataflowContext context);
}

/// <summary>Evaluates a node through a context; used by <see cref="DataflowNode"/>.</summary>
public delegate void DataflowEvaluator(DataflowContext context);

/// <summary>A node behavior built from a delegate.</summary>
/// <param name="evaluate">Reads inputs and writes outputs.</param>
/// <param name="isVolatile">Whether outputs may change with unchanged inputs.</param>
/// <param name="requires">Context features the node reads.</param>
public sealed class DataflowNode(DataflowEvaluator evaluate, bool isVolatile = false, params Type[] requires)
    : IDataflowNode
{
    /// <inheritdoc />
    public bool IsVolatile { get; } = isVolatile;

    /// <inheritdoc />
    public IReadOnlyList<Type> RequiredFeatures { get; } = requires;

    /// <inheritdoc />
    public void Evaluate(DataflowContext context) => evaluate(context);
}

/// <summary>
/// Storage and wiring behind <see cref="DataflowContext"/>. The interpreter's <see cref="DataflowProgram"/> is one
/// implementation; generated backends supply their own, so node behaviors run unchanged on every backend. Nodes and
/// ports are addressed by the backend's node index and each node's port declaration index.
/// </summary>
public abstract class DataflowFrame
{
    /// <summary>The node at an index.</summary>
    protected internal abstract GraphNode NodeAt(int node);

    /// <summary>Whether an input port receives a data edge.</summary>
    protected internal abstract bool IsConnected(int node, int port);

    /// <summary>Reads a port value, applying any coercion of an input.</summary>
    protected internal abstract T Read<T>(InstanceBuffer instances, InstanceHandle instance, int node, int port)
        where T : unmanaged;

    /// <summary>Reads a port's object value.</summary>
    protected internal abstract T? ReadReference<T>(InstanceBuffer instances, InstanceHandle instance, int node, int port)
        where T : class;

    /// <summary>Writes an output value.</summary>
    protected internal abstract void Write<T>(InstanceBuffer instances, InstanceHandle instance, int node, int port, T value)
        where T : unmanaged;

    /// <summary>Writes an output object value.</summary>
    protected internal abstract void WriteReference<T>(
        InstanceBuffer instances,
        InstanceHandle instance,
        int node,
        int port,
        T? value)
        where T : class;

    /// <summary>Copies a value between two ports of the same inferred type.</summary>
    protected internal abstract void Copy(InstanceBuffer instances, InstanceHandle instance, int node, int source, int target);
}

/// <summary>
/// What a node sees while it evaluates. Ports are addressed by their index in the node's declaration order; inputs
/// are already up to date.
/// </summary>
public readonly ref struct DataflowContext
{
    readonly DataflowFrame frame;
    readonly int node;

    /// <summary>Creates the context of one node evaluation; backends call this, behaviors receive it.</summary>
    /// <param name="frame">The backend's storage and wiring.</param>
    /// <param name="evaluation">Ambient services.</param>
    /// <param name="instances">The instance buffer being evaluated.</param>
    /// <param name="instance">The instance being evaluated.</param>
    /// <param name="node">The node's index in <paramref name="frame"/>.</param>
    public DataflowContext(
        DataflowFrame frame,
        EvaluationContext evaluation,
        InstanceBuffer instances,
        InstanceHandle instance,
        int node)
    {
        this.frame = frame;
        this.node = node;
        Evaluation = evaluation;
        Instances = instances;
        Instance = instance;
    }

    /// <summary>Ambient services such as cancellation, progress and typed features (for example time).</summary>
    public EvaluationContext Evaluation { get; }

    /// <summary>The instance buffer being evaluated.</summary>
    public InstanceBuffer Instances { get; }

    /// <summary>The instance being evaluated.</summary>
    public InstanceHandle Instance { get; }

    /// <summary>The node being evaluated.</summary>
    public GraphNode Node => frame.NodeAt(node);

    /// <summary>The node payload.</summary>
    public object? Payload => Node.Payload;

    /// <summary>Whether an input port receives an edge; unconnected inputs read the default value.</summary>
    public bool IsConnected(int port) => frame.IsConnected(node, port);

    /// <summary>Reads an input or output value.</summary>
    /// <exception cref="InvalidOperationException">The port carries no such data.</exception>
    public T Read<T>(int port) where T : unmanaged => frame.Read<T>(Instances, Instance, node, port);

    /// <summary>Reads an input or output object value.</summary>
    /// <exception cref="InvalidOperationException">The port carries no such data.</exception>
    public T? ReadReference<T>(int port) where T : class => frame.ReadReference<T>(Instances, Instance, node, port);

    /// <summary>Writes an output value.</summary>
    /// <exception cref="InvalidOperationException">The port carries no such data.</exception>
    public void Write<T>(int port, T value) where T : unmanaged => frame.Write(Instances, Instance, node, port, value);

    /// <summary>Writes an output object value.</summary>
    /// <exception cref="InvalidOperationException">The port carries no such data.</exception>
    public void WriteReference<T>(int port, T? value) where T : class =>
        frame.WriteReference(Instances, Instance, node, port, value);

    /// <summary>
    /// Copies a value between two ports of the same inferred type without naming it, for generic nodes such as
    /// relays and selectors.
    /// </summary>
    /// <exception cref="InvalidOperationException">The ports carry no data or different types.</exception>
    public void Copy(int source, int target) => frame.Copy(Instances, Instance, node, source, target);
}
