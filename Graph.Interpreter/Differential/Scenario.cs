namespace MASS4.Graph.Interpreter;

/// <summary>One host action of a differential scenario, applied identically to every backend compared.</summary>
public abstract record ScenarioStep;

/// <summary>Evaluates every instance once, after setting the <see cref="StateTime"/> step.</summary>
/// <param name="DeltaSeconds">Seconds since the previous evaluation.</param>
public sealed record EvaluateStep(double DeltaSeconds = 0) : ScenarioStep;

/// <summary>Raises a signal, such as a state trigger, on one instance or on all of them.</summary>
/// <param name="Signal">The signal identity.</param>
/// <param name="Instance">Index of the instance, or null for all.</param>
public sealed record RaiseStep(StableId Signal, int? Instance = null) : ScenarioStep;

/// <summary>Sets a blackboard variable on one instance or on all of them.</summary>
/// <param name="Variable">The variable identity.</param>
/// <param name="Value">The new value.</param>
/// <param name="Instance">Index of the instance, or null for all.</param>
public sealed record SetVariableStep(ElementId Variable, object? Value, int? Instance = null) : ScenarioStep;

/// <summary>Changes host features of the evaluation context, such as input read by volatile nodes.</summary>
/// <param name="Configure">Applied to each backend's own context.</param>
public sealed record ConfigureStep(Action<EvaluationContext> Configure) : ScenarioStep;

/// <summary>A reproducible sequence of host actions run against several backends of the same document.</summary>
/// <param name="Name">Name for reports.</param>
/// <param name="InstanceCount">Instances every backend runs.</param>
/// <param name="Steps">Actions in order; observations are compared after initialization and after each step.</param>
public sealed record Scenario(string Name, int InstanceCount, IReadOnlyList<ScenarioStep> Steps)
{
    /// <summary>Creates each backend's evaluation context; a fresh empty context when null.</summary>
    public Func<EvaluationContext>? CreateContext { get; init; }
}

/// <summary>A value that differs between the reference and the candidate backend.</summary>
/// <param name="Step">0 after initialization, then the 1-based index of the step just applied.</param>
/// <param name="Instance">Index of the instance.</param>
/// <param name="Element">The element the value belongs to.</param>
/// <param name="Expected">The reference value.</param>
/// <param name="Actual">The candidate value.</param>
/// <param name="Reason">Why the values differ.</param>
public sealed record Divergence(
    int Step,
    int Instance,
    ElementId Element,
    object? Expected,
    object? Actual,
    string Reason)
{
    /// <summary>Formats the divergence for test output.</summary>
    public override string ToString() =>
        $"step {Step}, instance {Instance}, element {Element}: {Reason} (expected {Expected}, actual {Actual})";
}

/// <summary>Result of comparing a candidate backend with a reference backend.</summary>
/// <param name="Scenario">The scenario name.</param>
/// <param name="Reference">Name of the reference backend.</param>
/// <param name="Candidate">Name of the candidate backend.</param>
/// <param name="Diagnostics">Compile diagnostics of both backends plus harness findings.</param>
/// <param name="CompileFailed">Whether either backend failed to compile the document.</param>
/// <param name="Divergences">Every differing value, in step order.</param>
public sealed record DifferentialReport(
    string Scenario,
    string Reference,
    string Candidate,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool CompileFailed,
    IReadOnlyList<Divergence> Divergences)
{
    /// <summary>Whether both backends compiled, agree on their contracts and every observation matched.</summary>
    public bool Equivalent =>
        !CompileFailed && Divergences.Count == 0 &&
        Diagnostics.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}

/// <summary>How the harness compares values.</summary>
public sealed record DifferentialOptions
{
    /// <summary>Largest absolute difference tolerated between floating-point values; exact by default.</summary>
    public double Tolerance { get; init; }

    /// <summary>
    /// Comparers for object values keyed by their runtime type, such as frames or textures, so backends that produce
    /// distinct but equal objects compare equal without the domain type overriding <see cref="object.Equals(object)"/>.
    /// </summary>
    public IReadOnlyDictionary<Type, System.Collections.IEqualityComparer> Comparers { get; init; } =
        new Dictionary<Type, System.Collections.IEqualityComparer>();
}

/// <summary>Diagnostic codes reported by the differential harness.</summary>
public static class DifferentialDiagnosticIds
{
    /// <summary>The backends declare different required context features.</summary>
    public const string FeaturesDiffer = "MG4001";
}
