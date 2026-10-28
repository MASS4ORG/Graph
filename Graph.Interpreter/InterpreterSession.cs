namespace MASS4.Graph.Interpreter;

/// <summary>
/// Live edit for editors and hot preview: it watches a document and recompiles on the first evaluation after a
/// change, without any explicit rebuild. A successful recompile replaces the running graph and restarts its
/// instances from their initial state; a failed one keeps the last good graph running and publishes the diagnostics.
/// </summary>
public sealed class InterpreterSession : IDisposable
{
    readonly GraphInterpreter interpreter;
    readonly int instanceCount;
    InstanceHandle[] handles = [];
    bool dirty = true;

    /// <summary>Starts watching a document.</summary>
    /// <param name="interpreter">Compiles the document.</param>
    /// <param name="document">The document being edited.</param>
    /// <param name="instanceCount">Instances the preview runs.</param>
    /// <exception cref="ArgumentOutOfRangeException">The instance count is not positive.</exception>
    public InterpreterSession(GraphInterpreter interpreter, GraphDocument document, int instanceCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(instanceCount);
        this.interpreter = interpreter;
        this.instanceCount = instanceCount;
        Document = document;
        document.Changed += OnChanged;
    }

    /// <summary>The document being watched.</summary>
    public GraphDocument Document { get; }

    /// <summary>The graph currently running, or null when no compile has succeeded yet.</summary>
    public InterpretedGraph? Graph { get; private set; }

    /// <summary>Instance state of the running graph.</summary>
    public InstanceBuffer? Instances { get; private set; }

    /// <summary>The running instances.</summary>
    public IReadOnlyList<InstanceHandle> Handles => handles;

    /// <summary>Diagnostics of the latest compile, successful or not.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];

    /// <summary>Number of successful compiles so far.</summary>
    public int Revision { get; private set; }

    /// <summary>Recompiles when the document changed since the last compile.</summary>
    /// <returns>Whether a newly compiled graph is now running.</returns>
    public bool Refresh(EvaluationContext context)
    {
        if (!dirty)
        {
            return false;
        }
        dirty = false;
        var result = interpreter.Compile(Document);
        Diagnostics = result.Diagnostics;
        if (result.Graph is not InterpretedGraph graph)
        {
            return false;
        }
        Graph = graph;
        Instances = new(graph.InstanceLayout, instanceCount);
        handles = new InstanceHandle[instanceCount];
        for (var i = 0; i < instanceCount; i++)
        {
            handles[i] = Instances.Allocate();
            graph.Initialize(context, Instances, handles[i]);
        }
        Revision++;
        return true;
    }

    /// <summary>Refreshes, then evaluates every instance of the running graph.</summary>
    public async ValueTask EvaluateAsync(EvaluationContext context)
    {
        Refresh(context);
        if (Graph is null || Instances is null)
        {
            return;
        }
        foreach (var handle in handles)
        {
            await Graph.EvaluateAsync(context, Instances, handle);
        }
    }

    /// <summary>Stops watching the document.</summary>
    public void Dispose() => Document.Changed -= OnChanged;

    void OnChanged(GraphDocument document, IReadOnlyList<GraphChange> changes) => dirty = true;
}
