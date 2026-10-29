using MASS4.Graph.Samples.Compositor;
using MASS4.Graph.Samples.Shader;
using MASS4.Graph.State;
using C = MASS4.Graph.Samples.Compositor.Generated;
using F = MASS4.Graph.Samples.Fsm.Generated;
using S = MASS4.Graph.Samples.Shader.Generated;

namespace MASS4.Graph.Samples.Tests;

/// <summary>
/// The generated backends are the differential harness's first independent candidate: each sample asset is compiled
/// at build time and must match the interpreter observation for observation, with an identical instance layout.
/// </summary>
public class GeneratedBackendTests
{
    [Test]
    public async Task GeneratedShaderMatchesTheInterpreter()
    {
        var module = new S.ShaderModule();
        var document = Load("material.graph.json", new GraphTypeRegistry().Add(module));
        var scenario = new Scenario("compiled shader", 3,
        [
            new SetVariableStep(Variable(document, "albedo map"), Texture.Checker(new(1, 0, 0, 1), new(0, 1, 0, 1))),
            new EvaluateStep(), new ConfigureStep(static c => c.Get<Fragment>().Uv = new(.8f, .8f)),
            new SetVariableStep(Variable(document, "intensity"), 3f, Instance: 1), new EvaluateStep(),
            new SetVariableStep(Variable(document, "tint"), new Color(0, 0, 1, 1), Instance: 2), new EvaluateStep(),
        ])
        { CreateContext = static () => new EvaluationContext().Set(new Fragment()) };
        await Equivalent(document, new(module.CreateDataflow()), S.MaterialGraph.Backend, scenario);
    }

    [Test]
    public async Task GeneratedGuardMatchesTheInterpreter()
    {
        var module = new F.GuardModule();
        var document = Load("guard.graph.json", new GraphTypeRegistry().Add(module));
        var distance = Variable(document, "distance");
        var scenario = new Scenario("compiled guard", 4,
        [
            new EvaluateStep(.1), new RaiseStep(F.SeePlayer.Type.Id), new EvaluateStep(.1),
            new SetVariableStep(distance, 1.0), new EvaluateStep(.1),
            new RaiseStep(F.Hit.Type.Id, Instance: 2), new EvaluateStep(.1),
            new SetVariableStep(distance, 5.0), new EvaluateStep(.1),
            new RaiseStep(F.LosePlayer.Type.Id), new EvaluateStep(.1), new EvaluateStep(2), new EvaluateStep(1.5),
        ]);
        await Equivalent(document, new(module.CreateDataflow(), state: module.CreateState()), F.GuardGraph.Backend, scenario);
    }

    [Test]
    public async Task GeneratedCompositorMatchesTheInterpreter()
    {
        var module = new C.CompositorModule();
        var document = Load("composition.graph.json", new GraphTypeRegistry().Add(module));
        var scenario = new Scenario("compiled compositor", 2,
        [
            new EvaluateStep(), new ConfigureStep(static c => c.Get<Timeline>().Seconds = .25), new EvaluateStep(),
            new ConfigureStep(static c => c.Get<Timeline>().Seconds = 2), new EvaluateStep(),
            new ConfigureStep(static c => c.Get<RenderSettings>().Width = 6), new EvaluateStep(),
        ])
        {
            CreateContext = static () => new EvaluationContext().Set(new Timeline()).Set(new RenderSettings { Width = 4, Height = 3 }),
        };
        var options = new DifferentialOptions
        {
            Comparers = new Dictionary<Type, System.Collections.IEqualityComparer>
            { [typeof(Frame)] = CompositorLibrary.FrameComparer },
        };
        await Equivalent(document, new(module.CreateDataflow()), C.CompositionGraph.Backend, scenario, options);
    }

    [Test]
    public async Task GeneratedGuardsTickThousandsOfInstancesWithoutAllocating()
    {
        var module = new F.GuardModule();
        var document = Load("guard.graph.json", new GraphTypeRegistry().Add(module));
        var distance = Variable(document, "distance");
        ICompiledGraph graph = new F.GuardGraph(document);
        var instances = new InstanceBuffer(graph.InstanceLayout, 1000);
        var guards = new InstanceHandle[1000];
        var time = new StateTime { DeltaSeconds = .5 };
        var context = new EvaluationContext().Set(time);
        for (var i = 0; i < guards.Length; i++)
        {
            guards[i] = instances.Allocate();
            graph.Initialize(context, instances, guards[i]);
        }
        Frames(graph, distance, context, instances, guards, 0, 2);
        var allocated = Frames(graph, distance, context, instances, guards, 2, 12);

        await Assert.That(allocated).IsEqualTo(0);
    }

    [Test]
    public async Task GeneratedBackendsRejectOtherAssetsAndChangedTopologies()
    {
        var module = new S.ShaderModule();
        var registry = new GraphTypeRegistry().Add(module);
        var document = Load("material.graph.json", registry);
        var other = await S.MaterialGraph.Backend.CompileAsync(new(new GraphDocument(registry)));
        var editor = new GraphEditor(document);
        S.Constant.Add(editor);
        var changed = await S.MaterialGraph.Backend.CompileAsync(new(document));

        await Assert.That(other.Graph).IsNull();
        await Assert.That(other.Diagnostics.Single().Id).IsEqualTo("MG6004");
        await Assert.That(changed.Graph).IsNull();
        await Assert.That(changed.Diagnostics.Single().Message).Contains("topology changed");
        editor.Undo();
        await Assert.That((await S.MaterialGraph.Backend.CompileAsync(new(document))).Graph).IsNotNull();
    }

    [Test]
    public async Task PayloadsStayRuntimeDataForGeneratedBackends()
    {
        var module = new S.ShaderModule();
        var document = Load("material.graph.json", new GraphTypeRegistry().Add(module));
        var editor = new GraphEditor(document);
        var constant = document.Nodes.First(n => n.TypeId == S.Constant.Type.Id);
        TypedGraphBuilder.Require(editor, new SetPayloadCommand(constant.Id, new ScalarPayload(.125f)));
        var scenario = new Scenario("edited payload", 1,
        [
            new SetVariableStep(Variable(document, "albedo map"), Texture.Checker(new(1, 1, 1, 1), new(0, 0, 0, 1))),
            new EvaluateStep(),
        ])
        { CreateContext = static () => new EvaluationContext().Set(new Fragment()) };
        await Equivalent(document, new(module.CreateDataflow()), S.MaterialGraph.Backend, scenario);
    }

    static async Task Equivalent(GraphDocument document, GraphInterpreter interpreter, IGraphBackend generated,
        Scenario scenario, DifferentialOptions? options = null)
    {
        var expected = interpreter.Compile(document).Graph!;
        var actual = (await generated.CompileAsync(new(document))).Graph!;
        await Assert.That(actual.InstanceLayout).IsEqualTo(expected.InstanceLayout);
        await Assert.That(actual.RequiredFeatures).IsEquivalentTo(expected.RequiredFeatures);
        await Assert.That(actual.Outputs).IsEquivalentTo(expected.Outputs);
        var report = await DifferentialHarness.CompareAsync(new(document), interpreter, generated, scenario, options);
        await Assert.That(string.Join("\n", report.Diagnostics)).IsEqualTo("");
        await Assert.That(report.Equivalent).IsTrue();
    }

    static GraphDocument Load(string asset, GraphTypeRegistry registry)
    {
        var loaded = GraphJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", asset)), registry);
        return loaded.Diagnostics.Count == 0
            ? loaded.Document!
            : throw new InvalidOperationException(string.Join("; ", loaded.Diagnostics));
    }

    static ElementId Variable(GraphDocument document, string name) => document.Variables.Single(v => v.Name == name).Id;

    static long Frames(ICompiledGraph graph, ElementId distance, EvaluationContext context, InstanceBuffer instances,
        InstanceHandle[] guards, int first, int end)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = first; frame < end; frame++)
        {
            for (var i = 0; i < guards.Length; i++)
            {
                var guard = guards[i];
                graph.SetVariable(instances, guard, distance, (double)((i + frame) % 4));
                if ((i + frame) % 3 == 0)
                {
                    F.SeePlayer.Raise(graph, instances, guard);
                }
                if ((i + frame) % 5 == 0)
                {
                    F.Hit.Raise(graph, instances, guard);
                }
                var evaluation = graph.EvaluateAsync(context, instances, guard);
                if (!evaluation.IsCompleted)
                {
                    throw new InvalidOperationException("Generated backends complete synchronously.");
                }
                evaluation.GetAwaiter().GetResult();
            }
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
