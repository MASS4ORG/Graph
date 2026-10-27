namespace MASS4.Graph.Interpreter;

/// <summary>
/// Runs one scenario against a reference backend (normally the interpreter) and a candidate backend, and reports every
/// observed value that differs, by step, instance and element identity.
/// </summary>
public static class DifferentialHarness
{
    /// <summary>Compiles the source with both backends and compares them over the scenario.</summary>
    /// <exception cref="MissingFeatureException">The scenario context lacks a feature a backend requires.</exception>
    public static async ValueTask<DifferentialReport> CompareAsync(
        GraphSource source,
        IGraphBackend reference,
        IGraphBackend candidate,
        Scenario scenario,
        DifferentialOptions? options = null,
        CancellationToken cancellation = default)
    {
        options ??= new();
        var expected = await reference.CompileAsync(source, null, cancellation);
        var actual = await candidate.CompileAsync(source, null, cancellation);
        List<Diagnostic> diagnostics = [.. expected.Diagnostics, .. actual.Diagnostics];
        if (expected.Graph is null || actual.Graph is null)
        {
            return new(scenario.Name, reference.Name, candidate.Name, diagnostics, true, []);
        }
        if (!expected.Graph.RequiredFeatures.ToHashSet().SetEquals(actual.Graph.RequiredFeatures))
        {
            // Backends that read different host features cannot be compared meaningfully, so nothing runs.
            diagnostics.Add(new(DifferentialDiagnosticIds.FeaturesDiffer, DiagnosticSeverity.Error, null,
                $"{reference.Name} requires [{Names(expected.Graph)}] but {candidate.Name} requires " +
                $"[{Names(actual.Graph)}]."));
            return new(scenario.Name, reference.Name, candidate.Name, diagnostics, false, []);
        }
        var left = Run.Start(expected.Graph, scenario, cancellation);
        var right = Run.Start(actual.Graph, scenario, cancellation);
        List<Divergence> divergences = [];
        Compare(left, right, 0, options, divergences);
        for (var step = 0; step < scenario.Steps.Count; step++)
        {
            await left.ApplyAsync(scenario.Steps[step]);
            await right.ApplyAsync(scenario.Steps[step]);
            Compare(left, right, step + 1, options, divergences);
        }
        return new(scenario.Name, reference.Name, candidate.Name, diagnostics, false, divergences);
    }

    static void Compare(Run left, Run right, int step, DifferentialOptions options, List<Divergence> divergences)
    {
        for (var instance = 0; instance < left.Handles.Length; instance++)
        {
            var expected = left.Observe(instance);
            var actual = right.Observe(instance);
            foreach (var (element, value) in expected)
            {
                if (!actual.Remove(element, out var other))
                {
                    divergences.Add(new(step, instance, element, value, null, "missing from the candidate"));
                }
                else if (!Same(value, other, options.Tolerance, options.Comparers))
                {
                    divergences.Add(new(step, instance, element, value, other, "values differ"));
                }
            }
            foreach (var (element, value) in actual)
            {
                divergences.Add(new(step, instance, element, null, value, "missing from the reference"));
            }
        }
    }

    /// <summary>
    /// Compares two observed values: a comparer registered for the expected value's type wins, floating-point values
    /// honor the tolerance, everything else uses <see cref="object.Equals(object, object)"/>.
    /// </summary>
    internal static bool Same(
        object? expected,
        object? actual,
        double tolerance,
        IReadOnlyDictionary<Type, System.Collections.IEqualityComparer>? comparers = null)
    {
        if (ComparerFor(expected, comparers) is { } comparer)
        {
            return comparer.Equals(expected, actual);
        }
        return (expected, actual) switch
        {
            (double a, double b) => Math.Abs(a - b) <= tolerance || a.Equals(b),
            (float a, float b) => Math.Abs(a - b) <= tolerance || a.Equals(b),
            _ => Equals(expected, actual),
        };
    }

    static System.Collections.IEqualityComparer? ComparerFor(
        object? value,
        IReadOnlyDictionary<Type, System.Collections.IEqualityComparer>? comparers) =>
        value is not null && comparers is not null && comparers.TryGetValue(value.GetType(), out var comparer)
            ? comparer
            : null;

    static string Names(ICompiledGraph graph) =>
        string.Join(", ", graph.RequiredFeatures.Select(static feature => feature.Name).Order());

    /// <summary>One backend's artifact, context and instances while a scenario runs.</summary>
    sealed class Run(
        ICompiledGraph graph,
        EvaluationContext context,
        StateTime time,
        InstanceBuffer instances,
        InstanceHandle[] handles)
    {
        public InstanceHandle[] Handles => handles;

        /// <exception cref="MissingFeatureException">The scenario context lacks a required feature.</exception>
        public static Run Start(ICompiledGraph graph, Scenario scenario, CancellationToken cancellation)
        {
            var time = new StateTime();
            var context = (scenario.CreateContext?.Invoke() ?? new EvaluationContext()).Set(time);
            context.Cancellation = cancellation;
            context.Require(graph.RequiredFeatures);
            var instances = new InstanceBuffer(graph.InstanceLayout, scenario.InstanceCount);
            var handles = new InstanceHandle[scenario.InstanceCount];
            for (var i = 0; i < handles.Length; i++)
            {
                handles[i] = instances.Allocate();
                graph.Initialize(context, instances, handles[i]);
            }
            return new(graph, context, time, instances, handles);
        }

        public async ValueTask ApplyAsync(ScenarioStep step)
        {
            switch (step)
            {
                case EvaluateStep evaluate:
                    time.DeltaSeconds = evaluate.DeltaSeconds;
                    foreach (var handle in handles)
                    {
                        await graph.EvaluateAsync(context, instances, handle);
                    }
                    break;
                case RaiseStep raise:
                    ForTargets(raise.Instance, handle => graph.Raise(instances, handle, raise.Signal));
                    break;
                case SetVariableStep set:
                    ForTargets(set.Instance, handle => graph.SetVariable(instances, handle, set.Variable, set.Value));
                    break;
                case ConfigureStep configure:
                    configure.Configure(context);
                    break;
                default:
                    throw new ArgumentException($"Unsupported scenario step {step.GetType().Name}.", nameof(step));
            }
        }

        public Dictionary<ElementId, object?> Observe(int instance)
        {
            List<GraphObservation> observations = [];
            graph.Observe(context, instances, handles[instance], observations);
            return observations.ToDictionary(static o => o.Element, static o => o.Value);
        }

        void ForTargets(int? instance, Action<InstanceHandle> action)
        {
            if (instance is { } index)
            {
                action(handles[index]);
                return;
            }
            foreach (var handle in handles)
            {
                action(handle);
            }
        }
    }
}
