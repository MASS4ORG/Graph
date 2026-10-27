using MASS4.Graph.Samples.Compositor;

namespace MASS4.Graph.Samples.Tests;

public class CompositorSampleTests
{
    static readonly RenderSettings Size = new() { Width = 8, Height = 2 };

    [Test]
    public async Task FramesMatchTheCompositingFormulaForTheirTime()
    {
        var (example, graph) = Compile();
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var instance = instances.Allocate();
        var context = new EvaluationContext().Set(new Timeline { Seconds = 1.25 }).Set(Size);

        await graph.EvaluateAsync(context, instances, instance);
        var frame = graph.ReadOutputReference<Frame>(context, instances, instance, example.Output.Id)!;
        var expected = Enumerable.Range(0, 16).Select(i => CompositionExample.Expected(i % 8, 8, 1.25)).ToArray();

        await Assert.That(frame.Luma).IsEquivalentTo(expected);
        await Assert.That(graph.Outputs).IsEquivalentTo([example.Output.Id]);
        await Assert.That(graph.Data.IsCached(example.Blend)).IsFalse();
    }

    [Test]
    public async Task NewNodesGetTheirTypesDefaultPayloads()
    {
        await Assert.That(new AddNodeCommand(CompositorLibrary.Ramp).Payload).IsEqualTo(new RampPayload(0));
        await Assert.That(new AddNodeCommand(CompositorLibrary.Pulse).Payload).IsEqualTo(new PulsePayload(1));
        await Assert.That(new AddNodeCommand(CompositorLibrary.Envelope).Payload).IsEqualTo(new EnvelopePayload(1));
    }

    [Test]
    public async Task MissingFeaturesFailLoudlyInsteadOfDefaulting()
    {
        var (_, graph) = Compile();
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var instance = instances.Allocate();
        var timeOnly = new EvaluationContext().Set(new Timeline());

        await Assert.That(graph.RequiredFeatures).IsEquivalentTo([typeof(Timeline), typeof(RenderSettings)]);
        await Assert.That(async () => await graph.EvaluateAsync(timeOnly, instances, instance))
            .ThrowsExactly<MissingFeatureException>();
        await Assert.That(() => graph.Initialize(new(), instances, instance)).ThrowsExactly<MissingFeatureException>();
    }

    [Test]
    public async Task RendersAreReproducibleAndSurviveSaveAndLoad()
    {
        var (example, graph) = Compile();
        var registry = new GraphTypeRegistry();
        CompositorLibrary.Register(registry);
        var loaded = GraphJson.Deserialize(GraphJson.Serialize(example.Document), registry).Document!;
        var interpreter = new GraphInterpreter(CompositorLibrary.CreateDataflow());
        var reloaded = interpreter.Compile(loaded).Graph!;

        var first = await Render(graph, example.Output.Id, 12);
        var second = await Render(graph, example.Output.Id, 12);
        var fromDisk = await Render(reloaded, example.Output.Id, 12);

        await Assert.That(second).IsEquivalentTo(first);
        await Assert.That(fromDisk).IsEquivalentTo(first);
        await Assert.That(first.Select(frame => frame.Checksum).Distinct().Count()).IsGreaterThan(1);
    }

    [Test]
    public async Task RenderingReportsProgressAndStopsWhenCancelled()
    {
        var (example, graph) = Compile();
        var instances = new InstanceBuffer(graph.InstanceLayout);
        using var cancellation = new CancellationTokenSource();
        List<EvaluationProgress> reports = [];
        var progress = new Listener(report =>
        {
            reports.Add(report);
            if (reports.Count == 3)
            {
                cancellation.Cancel();
            }
        });

        var completed = await CompositionRenderer.RenderAsync(graph, instances, instances.Allocate(),
            example.Output.Id, 4, 24, Size, new Listener(_ => { }));

        await Assert.That(completed.Count).IsEqualTo(4);
        await Assert.That(async () => await CompositionRenderer.RenderAsync(graph, instances, instances.Allocate(),
                example.Output.Id, 24, 24, Size, progress, cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(reports.Count).IsEqualTo(3);
        await Assert.That(reports[^1].Stage).IsEqualTo("frame 3 of 24");
    }

    [Test]
    public async Task NodesReuseTheirFramesBetweenEvaluations()
    {
        var (example, graph) = Compile();
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var instance = instances.Allocate();
        var timeline = new Timeline();
        var context = new EvaluationContext().Set(timeline).Set(Size);

        await graph.EvaluateAsync(context, instances, instance);
        var early = graph.ReadOutputReference<Frame>(context, instances, instance, example.Output.Id)!;
        var earlyMean = early.Mean;
        timeline.Seconds = 1;
        await graph.EvaluateAsync(context, instances, instance);
        var later = graph.ReadOutputReference<Frame>(context, instances, instance, example.Output.Id)!;

        await Assert.That(later).IsSameReferenceAs(early);
        await Assert.That(later.Mean).IsNotEqualTo(earlyMean);
    }

    [Test]
    public async Task TheInterpreterMatchesItselfOverATimelineScenarioWithAFrameComparer()
    {
        var (example, _) = Compile();
        var interpreter = new GraphInterpreter(CompositorLibrary.CreateDataflow());
        var source = new GraphSource(example.Document);
        var scenario = Timeline(static () => new EvaluationContext().Set(new Timeline()).Set(Size));
        var byContent = new DifferentialOptions
        {
            Comparers = new Dictionary<Type, System.Collections.IEqualityComparer>
            {
                [typeof(Frame)] = CompositorLibrary.FrameComparer,
            },
        };

        var compared = await DifferentialHarness.CompareAsync(source, interpreter, interpreter, scenario, byContent);
        var byIdentity = await DifferentialHarness.CompareAsync(source, interpreter, interpreter, scenario);

        await Assert.That(compared.Equivalent).IsTrue();
        await Assert.That(byIdentity.Equivalent).IsFalse();
        await Assert.That(async () => await DifferentialHarness.CompareAsync(source, interpreter, interpreter,
                Timeline(static () => new EvaluationContext().Set(new Timeline()))))
            .ThrowsExactly<MissingFeatureException>();
    }

    [Test]
    public async Task TheCompositorReferencesNoEngineAssembly()
    {
        var closure = Closure(typeof(CompositorLibrary).Assembly);

        await Assert.That(closure).Contains("MASS4.Graph");
        await Assert.That(closure.Any(name => name.Contains("Turian", StringComparison.OrdinalIgnoreCase))).IsFalse();
        await Assert.That(() => CompositionExample.Build(CompositorLibrary.CreateDataflow(), new GraphTypeRegistry()))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task FramesCompareByContentThroughTheComparer()
    {
        var comparer = CompositorLibrary.FrameComparer;
        System.Collections.IEqualityComparer untyped = comparer;
        var a = new Frame(2, 1);
        var b = new Frame(2, 1);
        b.Luma[1] = 0.5f;

        await Assert.That(comparer.Equals(a, new Frame(2, 1))).IsTrue();
        await Assert.That(comparer.Equals(a, a)).IsTrue();
        await Assert.That(comparer.Equals(a, b)).IsFalse();
        await Assert.That(comparer.Equals(a, new Frame(1, 2))).IsFalse();
        await Assert.That(comparer.Equals(a, null)).IsFalse();
        await Assert.That(untyped.Equals(a, new Frame(2, 1))).IsTrue();
        object hashed = new Frame(2, 1);
        await Assert.That(untyped.GetHashCode(hashed)).IsEqualTo(comparer.GetHashCode(new Frame(2, 1)));
        await Assert.That(new Frame(0, 0).Mean).IsEqualTo(0);
        await Assert.That(Frame.Reuse(a, 3, 1)).IsNotSameReferenceAs(a);
    }

    static Scenario Timeline(Func<EvaluationContext> createContext)
    {
        List<ScenarioStep> steps = [];
        foreach (var seconds in new[] { 0, 0.5, 1.5, 3 })
        {
            steps.Add(new ConfigureStep(context => context.Get<Timeline>().Seconds = seconds));
            steps.Add(new EvaluateStep());
        }
        return new("timeline", 2, steps) { CreateContext = createContext };
    }

    /// <summary>Names of every MASS4 assembly reachable from the root, plus their direct references.</summary>
    static HashSet<string> Closure(System.Reflection.Assembly root)
    {
        HashSet<string> names = [];
        Queue<System.Reflection.Assembly> pending = new([root]);
        while (pending.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (names.Add(reference.Name!) && reference.Name!.StartsWith("MASS4", StringComparison.Ordinal))
                {
                    pending.Enqueue(System.Reflection.Assembly.Load(reference));
                }
            }
        }
        return names;
    }

    static async Task<IReadOnlyList<RenderedFrame>> Render(ICompiledGraph graph, ElementId output, int frames)
    {
        var instances = new InstanceBuffer(graph.InstanceLayout);
        return await CompositionRenderer.RenderAsync(graph, instances, instances.Allocate(), output, frames, 24, Size);
    }

    static (CompositionExample Example, InterpretedGraph Graph) Compile()
    {
        var registry = new GraphTypeRegistry();
        CompositorLibrary.Register(registry);
        var data = CompositorLibrary.CreateDataflow();
        var example = CompositionExample.Build(data, registry);
        var result = new GraphInterpreter(data).Compile(example.Document);
        return (example, (InterpretedGraph)(result.Graph ?? throw new InvalidOperationException(
            string.Join("; ", result.Diagnostics))));
    }

    sealed class Listener(Action<EvaluationProgress> report) : IProgress<EvaluationProgress>
    {
        public void Report(EvaluationProgress value) => report(value);
    }
}
