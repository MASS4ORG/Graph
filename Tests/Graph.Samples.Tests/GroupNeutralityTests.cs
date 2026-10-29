using MASS4.Graph.Samples.Compositor;
using MASS4.Graph.Samples.Fsm;
using MASS4.Graph.Samples.Shader;
using MASS4.Graph.State;

namespace MASS4.Graph.Samples.Tests;

/// <summary>Groups and their collapsed state are never semantic: every sample validates and runs identically.</summary>
public class GroupNeutralityTests
{
    [Test]
    public async Task GroupedShaderValidatesAndRendersIdentically()
    {
        var registry = new GraphTypeRegistry();
        ShaderLibrary.Register(registry);
        var data = ShaderLibrary.CreateDataflow();
        var example = MaterialExample.Build(data, registry);
        var scenario = new Scenario("grouped shader", 2,
        [
            new SetVariableStep(example.AlbedoMap.Id, Texture.Checker(new(1, 0, 0, 1), new(0, 1, 0, 1))),
            new EvaluateStep(), new ConfigureStep(static c => c.Get<Fragment>().Uv = new(.8f, .8f)), new EvaluateStep(),
        ])
        { CreateContext = static () => new EvaluationContext().Set(new Fragment()) };
        await Neutral(example.Document, new(data), [.. data.Rules], scenario);
    }

    [Test]
    public async Task GroupedGuardValidatesAndWalksIdentically()
    {
        var registry = new GraphTypeRegistry();
        GuardLibrary.Register(registry);
        var data = GuardLibrary.CreateDataflow();
        var state = GuardLibrary.CreateState();
        var example = GuardExample.Build(data, state, registry);
        var scenario = new Scenario("grouped guard", 2,
        [
            new EvaluateStep(.1), new RaiseStep(GuardLibrary.SeePlayer.Id), new EvaluateStep(.1),
            new SetVariableStep(example.Distance.Id, 1.0), new EvaluateStep(.1), new RaiseStep(GuardLibrary.Hit.Id),
            new EvaluateStep(.1),
        ]);
        await Neutral(example.Document, new(data, state: state), [.. data.Rules, .. state.Rules], scenario);
    }

    [Test]
    public async Task GroupedCompositorValidatesAndRendersIdentically()
    {
        var registry = new GraphTypeRegistry();
        CompositorLibrary.Register(registry);
        var data = CompositorLibrary.CreateDataflow();
        var example = CompositionExample.Build(data, registry);
        var scenario = new Scenario("grouped compositor", 2,
        [
            new EvaluateStep(), new ConfigureStep(static c => c.Get<Timeline>().Seconds = .5), new EvaluateStep(),
        ])
        {
            CreateContext = static () => new EvaluationContext().Set(new Timeline()).Set(new RenderSettings { Width = 4, Height = 3 }),
        };
        var options = new DifferentialOptions
        {
            Comparers = new Dictionary<Type, System.Collections.IEqualityComparer>
            { [typeof(Frame)] = CompositorLibrary.FrameComparer },
        };
        await Neutral(example.Document, new(data), [.. data.Rules], scenario, options);
    }

    static async Task Neutral(GraphDocument document, GraphInterpreter interpreter, IGraphRule[] rules,
        Scenario scenario, DifferentialOptions? options = null)
    {
        var grouped = Grouped(document, rules);
        await Assert.That(grouped.Groups.Count).IsGreaterThan(1);
        await Assert.That(grouped.CollapsedGroups.Count).IsEqualTo(1);
        await Assert.That(Describe(GraphValidator.Validate(grouped, rules)))
            .IsEqualTo(Describe(GraphValidator.Validate(document, rules)));
        var report = await DifferentialHarness.CompareAsync(new(document), interpreter,
            new GroupedBackend(interpreter, rules), scenario, options);
        await Assert.That(report.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(report.Equivalent).IsTrue();
    }

    /// <summary>
    /// A reloaded copy of the document with its nodes split across nested groups, the inner one collapsed, then saved
    /// and reloaded again so persisted groups are what gets compiled. Element identities are unchanged.
    /// </summary>
    static GraphDocument Grouped(GraphDocument document, IGraphRule[] rules)
    {
        var copy = GraphJson.Deserialize(GraphJson.Serialize(document), document.Registry).Document!;
        var editor = new GraphEditor(copy, additionalRules: rules);
        var nodes = copy.Nodes.Select(static n => n.Id).Order().ToArray();
        var outer = new AddGroupCommand("Outer", nodes: nodes[..(nodes.Length / 2)]);
        var inner = new AddGroupCommand("Inner", outer.GroupId, nodes[(nodes.Length / 2)..]);
        TypedGraphBuilder.Require(editor, new CompositeCommand("Group", [outer, inner,
            new SetGroupCollapsedCommand(inner.GroupId, true)]));
        return GraphJson.Deserialize(GraphJson.Serialize(copy), document.Registry).Document!;
    }

    static string Describe(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(static d => $"{d.Id}:{d.Severity}:{d.ElementId}").Order(StringComparer.Ordinal));

    sealed class GroupedBackend(GraphInterpreter interpreter, IGraphRule[] rules) : IGraphBackend
    {
        public string Name => "grouped document";

        public ValueTask<CompileResult> CompileAsync(GraphSource source, IProgress<EvaluationProgress>? progress = null,
            CancellationToken cancellation = default) =>
            interpreter.CompileAsync(new(Grouped(source.Document, rules)), progress, cancellation);
    }
}
