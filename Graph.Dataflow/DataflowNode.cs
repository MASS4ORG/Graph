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

    /// <summary>Reads inputs and writes outputs through the context.</summary>
    void Evaluate(DataflowContext context);
}

/// <summary>Evaluates a node through a context; used by <see cref="DataflowNode"/>.</summary>
public delegate void DataflowEvaluator(DataflowContext context);

/// <summary>A node behavior built from a delegate.</summary>
/// <param name="evaluate">Reads inputs and writes outputs.</param>
/// <param name="isVolatile">Whether outputs may change with unchanged inputs.</param>
public sealed class DataflowNode(DataflowEvaluator evaluate, bool isVolatile = false) : IDataflowNode
{
    /// <inheritdoc />
    public bool IsVolatile { get; } = isVolatile;

    /// <inheritdoc />
    public void Evaluate(DataflowContext context) => evaluate(context);
}

/// <summary>
/// What a node sees while it evaluates. Ports are addressed by their index in the node's declaration order; inputs
/// are already up to date.
/// </summary>
public readonly ref struct DataflowContext
{
    readonly DataflowProgram program;
    readonly int node;

    internal DataflowContext(
        DataflowProgram program,
        EvaluationContext evaluation,
        InstanceBuffer instances,
        InstanceHandle instance,
        int node)
    {
        this.program = program;
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
    public GraphNode Node => program.NodeAt(node);

    /// <summary>The node payload.</summary>
    public object? Payload => Node.Payload;

    /// <summary>Whether an input port receives an edge; unconnected inputs read the default value.</summary>
    public bool IsConnected(int port) => program.IsConnected(node, port);

    /// <summary>Reads an input or output value.</summary>
    /// <exception cref="InvalidOperationException">The port carries no such data.</exception>
    public T Read<T>(int port) where T : unmanaged => program.ReadLocal<T>(Instances, Instance, node, port);

    /// <summary>Reads an input or output object value.</summary>
    /// <exception cref="InvalidOperationException">The port carries no such data.</exception>
    public T? ReadReference<T>(int port) where T : class =>
        program.ReadLocalReference<T>(Instances, Instance, node, port);

    /// <summary>Writes an output value.</summary>
    /// <exception cref="InvalidOperationException">The port carries no such data.</exception>
    public void Write<T>(int port, T value) where T : unmanaged =>
        program.Location(node, port).Write(Instances, Instance, value);

    /// <summary>Writes an output object value.</summary>
    /// <exception cref="InvalidOperationException">The port carries no such data.</exception>
    public void WriteReference<T>(int port, T? value) where T : class =>
        program.Location(node, port).WriteReference(Instances, Instance, value);

    /// <summary>
    /// Copies a value between two ports of the same inferred type without naming it, for generic nodes such as
    /// relays and selectors.
    /// </summary>
    /// <exception cref="InvalidOperationException">The ports carry no data or different types.</exception>
    public void Copy(int source, int target) => program.CopyLocal(Instances, Instance, node, source, target);
}
