namespace MASS4.Graph;

/// <summary>Progress of a long-running evaluation or compilation.</summary>
/// <param name="Fraction">Completed share between 0 and 1.</param>
/// <param name="Stage">Optional description of the current stage.</param>
public readonly record struct EvaluationProgress(double Fraction, string? Stage = null);

/// <summary>
/// Ambient services for an evaluation: cancellation, progress, host functions, and typed features that semantics
/// packages add (for example a time or streaming context for media graphs). Reuse one context across ticks so that
/// evaluation allocates nothing.
/// </summary>
public sealed class EvaluationContext
{
    readonly Dictionary<Type, object> features = [];

    /// <summary>Signals that evaluation should stop.</summary>
    public CancellationToken Cancellation { get; set; }

    /// <summary>Receives progress reports, when anyone listens.</summary>
    public IProgress<EvaluationProgress>? Progress { get; set; }

    /// <summary>Host functions available to the graph.</summary>
    public IdentityRegistry<HostFunction>? HostFunctions { get; set; }

    /// <summary>Adds or replaces a typed feature.</summary>
    public EvaluationContext Set<T>(T feature) where T : class
    {
        features[typeof(T)] = feature;
        return this;
    }

    /// <summary>Finds a typed feature.</summary>
    public bool TryGet<T>([NotNullWhen(true)] out T? feature) where T : class
    {
        var found = features.TryGetValue(typeof(T), out var value);
        feature = found ? (T)value! : null;
        return found;
    }
}

/// <summary>Input of a backend: a document and the identities it was authored against.</summary>
/// <param name="Document">The graph asset.</param>
public sealed record GraphSource(GraphDocument Document)
{
    /// <summary>The identities the document resolves against.</summary>
    public GraphTypeRegistry Registry => Document.Registry;
}

/// <summary>
/// An executable form of one graph asset. It owns no instance state: callers allocate instances in an
/// <see cref="InstanceBuffer"/> built from <see cref="InstanceLayout"/>, so one artifact serves any number of
/// instances.
/// </summary>
public interface ICompiledGraph
{
    /// <summary>Identity of the asset this artifact was built from.</summary>
    StableId AssetId { get; }

    /// <summary>Per-instance record layout this artifact needs.</summary>
    InstanceLayout InstanceLayout { get; }

    /// <summary>Prepares a freshly allocated instance, for example by entering the initial state.</summary>
    void Initialize(InstanceBuffer instances, InstanceHandle instance);

    /// <summary>Evaluates one instance; synchronous backends complete without allocating.</summary>
    ValueTask EvaluateAsync(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance);
}

/// <summary>Outcome of compiling a graph.</summary>
/// <param name="Graph">The artifact, or null when compilation failed.</param>
/// <param name="Diagnostics">Findings naming the affected elements.</param>
public sealed record CompileResult(ICompiledGraph? Graph, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Turns graph sources into executable artifacts. The interpreter is the always-available reference backend; generated
/// code backends must produce identical results.
/// </summary>
public interface IGraphBackend
{
    /// <summary>Backend name for diagnostics and differential test reports.</summary>
    string Name { get; }

    /// <summary>Compiles a source, reporting progress and honoring cancellation.</summary>
    ValueTask<CompileResult> CompileAsync(
        GraphSource source,
        IProgress<EvaluationProgress>? progress = null,
        CancellationToken cancellation = default);
}
