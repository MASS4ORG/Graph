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

    /// <summary>Finds a typed feature, for features that are genuinely optional.</summary>
    public bool TryGet<T>([NotNullWhen(true)] out T? feature) where T : class
    {
        var found = features.TryGetValue(typeof(T), out var value);
        feature = found ? (T)value! : null;
        return found;
    }

    /// <summary>Returns a feature a node or backend cannot work without.</summary>
    /// <exception cref="MissingFeatureException">The host did not provide it.</exception>
    public T Get<T>() where T : class =>
        features.TryGetValue(typeof(T), out var value) ? (T)value : throw new MissingFeatureException(typeof(T));

    /// <summary>Whether a feature of exactly this type was provided.</summary>
    public bool Contains(Type feature) => features.ContainsKey(feature);

    /// <summary>Checks that every required feature was provided, before anything evaluates.</summary>
    /// <exception cref="MissingFeatureException">A feature is missing.</exception>
    public void Require(IReadOnlyList<Type> required)
    {
        for (var i = 0; i < required.Count; i++)
        {
            if (!features.ContainsKey(required[i]))
            {
                throw new MissingFeatureException(required[i]);
            }
        }
    }
}

/// <summary>
/// An evaluation needed a context feature the host did not provide. Evaluation fails instead of falling back to a
/// default, which would produce plausible but wrong results.
/// </summary>
/// <param name="feature">The missing feature type.</param>
public sealed class MissingFeatureException(Type feature)
    : InvalidOperationException($"The evaluation context lacks the required feature {feature.Name}.")
{
    /// <summary>The missing feature type.</summary>
    public Type Feature { get; } = feature;
}

/// <summary>Input of a backend: a document and the identities it was authored against.</summary>
/// <param name="Document">The graph asset.</param>
public sealed record GraphSource(GraphDocument Document)
{
    /// <summary>The identities the document resolves against.</summary>
    public GraphTypeRegistry Registry => Document.Registry;
}

/// <summary>One observable value of an instance, keyed by the element it belongs to.</summary>
/// <param name="Element">An output variable, a data port, or the element state is reported under.</param>
/// <param name="Value">The value; unmanaged values are boxed.</param>
public readonly record struct GraphObservation(ElementId Element, object? Value);

/// <summary>
/// An executable form of one graph asset. It owns no instance state: callers allocate instances in an
/// <see cref="InstanceBuffer"/> built from <see cref="InstanceLayout"/>, so one artifact serves any number of
/// instances. Host inputs and observations are expressed in document identities, so different backends of the same
/// asset can be driven and compared identically.
/// </summary>
public interface ICompiledGraph
{
    /// <summary>Identity of the asset this artifact was built from.</summary>
    StableId AssetId { get; }

    /// <summary>Per-instance record layout this artifact needs.</summary>
    InstanceLayout InstanceLayout { get; }

    /// <summary>
    /// Context features the graph cannot evaluate without. <see cref="Initialize"/> and <see cref="EvaluateAsync"/>
    /// throw <see cref="MissingFeatureException"/> when one is absent instead of substituting a default.
    /// </summary>
    IReadOnlyList<Type> RequiredFeatures { get; }

    /// <summary>Output-scope blackboard variables the graph declares as its results.</summary>
    IReadOnlyList<ElementId> Outputs { get; }

    /// <summary>Prepares a freshly allocated instance, for example by entering the initial state.</summary>
    /// <exception cref="MissingFeatureException">A required feature is missing.</exception>
    void Initialize(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance);

    /// <summary>
    /// Evaluates one instance. The shape is asynchronous so long-running backends can await; synchronous backends
    /// complete immediately without allocating.
    /// </summary>
    /// <exception cref="MissingFeatureException">A required feature is missing.</exception>
    ValueTask EvaluateAsync(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance);

    /// <summary>Returns the current value of a declared output, evaluating whatever it depends on.</summary>
    /// <exception cref="KeyNotFoundException">The graph declares no such output.</exception>
    /// <exception cref="InvalidOperationException">The output is not of type <typeparamref name="T"/>.</exception>
    T ReadOutput<T>(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance, ElementId output)
        where T : unmanaged;

    /// <summary>Returns the current object value of a declared output, evaluating whatever it depends on.</summary>
    /// <exception cref="KeyNotFoundException">The graph declares no such output.</exception>
    /// <exception cref="InvalidOperationException">The output is not of type <typeparamref name="T"/>.</exception>
    T? ReadOutputReference<T>(
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle instance,
        ElementId output)
        where T : class;

    /// <summary>Raises a typed signal, such as a state trigger, for the next evaluation; false when unknown.</summary>
    bool Raise(InstanceBuffer instances, InstanceHandle instance, StableId signal);

    /// <summary>Sets a blackboard variable of an instance; false when unknown or of another type.</summary>
    bool SetVariable(InstanceBuffer instances, InstanceHandle instance, ElementId variable, object? value);

    /// <summary>Sets an unmanaged blackboard variable without boxing; false when unknown or of another type.</summary>
    bool SetVariable<T>(InstanceBuffer instances, InstanceHandle instance, ElementId variable, T value)
        where T : unmanaged;

    /// <summary>
    /// Adds every observable value of an instance (data outputs, current states) to <paramref name="observations"/>,
    /// for differential testing and inspection tools. Values are boxed; keep this off per-tick paths.
    /// </summary>
    void Observe(
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle instance,
        ICollection<GraphObservation> observations);
}

/// <summary>Typed host operations over <see cref="ICompiledGraph"/>.</summary>
public static class CompiledGraphExtensions
{
    /// <summary>
    /// Raises a registered signal by its declaration rather than a bare identity, so a reference to a renamed or
    /// removed signal fails to compile instead of missing at run time.
    /// </summary>
    public static bool Raise(this ICompiledGraph graph, InstanceBuffer instances, InstanceHandle instance, SignalType signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return graph.Raise(instances, instance, signal.Id);
    }
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
