namespace MASS4.Graph.Interpreter.Tests;

public class DifferentialTests
{
    [Test]
    public async Task TheInterpreterMatchesItselfOnTheMixedDocument()
    {
        var mixed = BuildMixed();
        var interpreter = new GraphInterpreter(mixed.Data, mixed.Control, mixed.State);

        var report = await DifferentialHarness.CompareAsync(new(mixed.Editor.Document), interpreter,
            new GraphInterpreter(mixed.Data, mixed.Control, mixed.State), MixedScenario());

        await Assert.That(report.Equivalent).IsTrue();
        await Assert.That(report.Reference).IsEqualTo("interpreter");
        await Assert.That(report.Scenario).IsEqualTo("mixed");
    }

    [Test]
    public async Task TheInterpreterMatchesItselfOnSingleSemanticsDocuments()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var gain = new GraphVariable(ElementId.New(), "gain", Number.Id, VariableScope.Input) { DefaultValue = 1.0 };
        editor.Execute(new SetVariableCommand(gain));
        var read = editor.AddNode(DataflowNodes.GetVariable, new VariableReference(gain.Id));
        var doubled = editor.AddNode(Scale);
        editor.Wire(read, 0, doubled, 0);
        var tick = editor.AddNode(OnTick);
        var counter = editor.AddNode(Counter);
        editor.Wire(tick, 0, counter, 0);
        var interpreter = new GraphInterpreter(data, CreateControl());
        var scenario = new Scenario("dataflow and control", 2,
        [
            new EvaluateStep(), new SetVariableStep(gain.Id, 4.0, Instance: 1), new EvaluateStep(),
            new SetVariableStep(gain.Id, 2.0), new EvaluateStep(),
        ]);

        var report = await DifferentialHarness.CompareAsync(new(editor.Document), interpreter, interpreter, scenario);

        await Assert.That(report.Equivalent).IsTrue();
        await Assert.That(report.Divergences.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DivergencesNameTheStepInstanceAndElement()
    {
        var mixed = BuildMixed();
        var interpreter = new GraphInterpreter(mixed.Data, mixed.Control, mixed.State);
        var stride = mixed.Stride.Port(1);
        var faulty = new Perturbing(interpreter, stride, static value => value is double d ? d + 1 : value);

        var report = await DifferentialHarness.CompareAsync(new(mixed.Editor.Document), interpreter, faulty,
            MixedScenario());

        await Assert.That(report.Equivalent).IsFalse();
        await Assert.That(report.Divergences.All(d => d.Element == stride && d.Reason == "values differ")).IsTrue();
        await Assert.That(report.Divergences.Count).IsEqualTo(3 * (MixedScenario().Steps.Count + 1));
        await Assert.That(report.Divergences[0].ToString()).Contains(stride.ToString());
        await Assert.That(report.Candidate).IsEqualTo("perturbing");
    }

    [Test]
    public async Task MissingAndExtraObservationsAreReported()
    {
        var mixed = BuildMixed();
        var interpreter = new GraphInterpreter(mixed.Data, mixed.Control, mixed.State);
        var faulty = new Perturbing(interpreter, mixed.Total.Port(2), null);
        var scenario = new Scenario("one step", 1, [new EvaluateStep(0.1)]) { CreateContext = Context };

        var report = await DifferentialHarness.CompareAsync(new(mixed.Editor.Document), interpreter, faulty, scenario);

        var reasons = report.Divergences.Select(d => d.Reason).Distinct().Order().ToArray();
        await Assert.That(reasons).IsEquivalentTo(["missing from the candidate", "missing from the reference"]);
    }

    [Test]
    public async Task ToleranceAbsorbsFloatingPointNoise()
    {
        var mixed = BuildMixed();
        var interpreter = new GraphInterpreter(mixed.Data, mixed.Control, mixed.State);
        var noisy = new Perturbing(interpreter, mixed.Stride.Port(1),
            static value => value is double d ? d + 1e-12 : value);
        var source = new GraphSource(mixed.Editor.Document);

        var strict = await DifferentialHarness.CompareAsync(source, interpreter, noisy, MixedScenario());
        var tolerant = await DifferentialHarness.CompareAsync(source, interpreter, noisy, MixedScenario(),
            new() { Tolerance = 1e-9 });

        await Assert.That(strict.Equivalent).IsFalse();
        await Assert.That(tolerant.Equivalent).IsTrue();
    }

    [Test]
    public async Task CompileFailuresAndUnknownStepsAreReported()
    {
        var mixed = BuildMixed();
        var interpreter = new GraphInterpreter(mixed.Data, mixed.Control, mixed.State);
        var source = new GraphSource(mixed.Editor.Document);

        var failed = await DifferentialHarness.CompareAsync(source, interpreter, new Failing(), MixedScenario());

        await Assert.That(failed.CompileFailed).IsTrue();
        await Assert.That(failed.Equivalent).IsFalse();
        await Assert.That(failed.Diagnostics.Single().Id).IsEqualTo("TEST01");
        await Assert.That(async () => await DifferentialHarness.CompareAsync(source, interpreter, interpreter,
                new Scenario("odd", 1, [new OddStep()]) { CreateContext = Context }))
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task FeatureContractsMustMatchAndBeProvided()
    {
        var mixed = BuildMixed();
        var interpreter = new GraphInterpreter(mixed.Data, mixed.Control, mixed.State);
        var source = new GraphSource(mixed.Editor.Document);
        var demanding = new Perturbing(interpreter, default, null, typeof(Journal));

        var mismatch = await DifferentialHarness.CompareAsync(source, interpreter, demanding, MixedScenario());

        await Assert.That(mismatch.Equivalent).IsFalse();
        await Assert.That(mismatch.CompileFailed).IsFalse();
        await Assert.That(mismatch.Diagnostics.Single().Id).IsEqualTo(DifferentialDiagnosticIds.FeaturesDiffer);
        await Assert.That(mismatch.Diagnostics.Single().Message).Contains("Journal");
        await Assert.That(async () => await DifferentialHarness.CompareAsync(source, interpreter, interpreter,
                new Scenario("no input", 1, [new EvaluateStep()])))
            .ThrowsExactly<MissingFeatureException>();
    }

    [Test]
    public async Task RegisteredComparersDecideEqualityForTheirType()
    {
        var always = new Dictionary<Type, System.Collections.IEqualityComparer> { [typeof(string)] = new Always() };

        await Assert.That(DifferentialHarness.Same("a", "b", 0, always)).IsTrue();
        await Assert.That(DifferentialHarness.Same(1.0, 2.0, 0, always)).IsFalse();
        await Assert.That(DifferentialHarness.Same(null, null, 0, always)).IsTrue();
    }

    static EvaluationContext Context() => new EvaluationContext().Set(new Locomotion());

    sealed class Always : System.Collections.IEqualityComparer
    {
        public new bool Equals(object? x, object? y) => true;

        public int GetHashCode(object obj) => 0;
    }

    [Test]
    [Arguments(1.0, 1.0, 0.0, true)]
    [Arguments(1.0, 1.5, 0.0, false)]
    [Arguments(1.0, 1.5, 1.0, true)]
    [Arguments(double.NaN, double.NaN, 0.0, true)]
    public async Task FloatingPointComparisonHonorsTolerance(double a, double b, double tolerance, bool same)
    {
        await Assert.That(DifferentialHarness.Same(a, b, tolerance)).IsEqualTo(same);
        await Assert.That(DifferentialHarness.Same((float)a, (float)b, tolerance)).IsEqualTo(same);
        await Assert.That(DifferentialHarness.Same("x", "x", tolerance)).IsTrue();
        await Assert.That(DifferentialHarness.Same(1.0, "1", tolerance)).IsFalse();
    }

    /// <summary>Three instances; one stops while the others keep walking, with host input changing mid-run.</summary>
    static Scenario MixedScenario() => new("mixed", 3,
    [
        new EvaluateStep(0.1),
        new ConfigureStep(static context =>
        {
            context.TryGet<Locomotion>(out var locomotion);
            locomotion!.Speed = 1;
        }),
        new EvaluateStep(0.1),
        new EvaluateStep(0.5),
        new RaiseStep(Stop.Id, Instance: 1),
        new EvaluateStep(0.1),
        new RaiseStep(Go.Id),
        new EvaluateStep(0.25),
    ])
    {
        CreateContext = static () => new EvaluationContext().Set(new Locomotion()),
    };

    sealed record OddStep : ScenarioStep;

    /// <summary>A candidate backend that corrupts or drops the observation of one element.</summary>
    sealed class Perturbing(
        IGraphBackend inner,
        ElementId target,
        Func<object?, object?>? change,
        params Type[] extraFeatures) : IGraphBackend
    {
        public string Name => "perturbing";

        public async ValueTask<CompileResult> CompileAsync(
            GraphSource source,
            IProgress<EvaluationProgress>? progress = null,
            CancellationToken cancellation = default)
        {
            var result = await inner.CompileAsync(source, progress, cancellation);
            return result with { Graph = new PerturbedGraph(result.Graph!, target, change, extraFeatures) };
        }
    }

    sealed class PerturbedGraph(
        ICompiledGraph inner,
        ElementId target,
        Func<object?, object?>? change,
        Type[] extraFeatures) : ICompiledGraph
    {
        public StableId AssetId => inner.AssetId;

        public InstanceLayout InstanceLayout => inner.InstanceLayout;

        public IReadOnlyList<Type> RequiredFeatures { get; } = [.. inner.RequiredFeatures, .. extraFeatures];

        public IReadOnlyList<ElementId> Outputs => inner.Outputs;

        public T ReadOutput<T>(
            EvaluationContext context,
            InstanceBuffer instances,
            InstanceHandle instance,
            ElementId output)
            where T : unmanaged => inner.ReadOutput<T>(context, instances, instance, output);

        public T? ReadOutputReference<T>(
            EvaluationContext context,
            InstanceBuffer instances,
            InstanceHandle instance,
            ElementId output)
            where T : class => inner.ReadOutputReference<T>(context, instances, instance, output);

        public bool SetVariable<T>(InstanceBuffer instances, InstanceHandle instance, ElementId variable, T value)
            where T : unmanaged => inner.SetVariable(instances, instance, variable, value);

        public void Initialize(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance) =>
            inner.Initialize(context, instances, instance);

        public ValueTask EvaluateAsync(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance) =>
            inner.EvaluateAsync(context, instances, instance);

        public bool Raise(InstanceBuffer instances, InstanceHandle instance, StableId signal) =>
            inner.Raise(instances, instance, signal);

        public bool SetVariable(InstanceBuffer instances, InstanceHandle instance, ElementId variable, object? value) =>
            inner.SetVariable(instances, instance, variable, value);

        public void Observe(
            EvaluationContext context,
            InstanceBuffer instances,
            InstanceHandle instance,
            ICollection<GraphObservation> observations)
        {
            List<GraphObservation> seen = [];
            inner.Observe(context, instances, instance, seen);
            foreach (var observation in seen)
            {
                if (observation.Element != target)
                {
                    observations.Add(observation);
                }
                else if (change is not null)
                {
                    observations.Add(observation with { Value = change(observation.Value) });
                }
                else
                {
                    observations.Add(new(ElementId.New(), observation.Value));
                }
            }
        }
    }

    sealed class Failing : IGraphBackend
    {
        public string Name => "failing";

        public ValueTask<CompileResult> CompileAsync(
            GraphSource source,
            IProgress<EvaluationProgress>? progress = null,
            CancellationToken cancellation = default) =>
            ValueTask.FromResult(new CompileResult(null,
                [new("TEST01", DiagnosticSeverity.Error, null, "Not implemented.")]));
    }
}
