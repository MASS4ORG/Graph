using MASS4.Graph.Samples.Shader;

namespace MASS4.Graph.Samples.Tests;

public class ShaderSampleTests
{
    static readonly Color Light = new(1, 1, 1, 1);
    static readonly Color Dark = new(0.2f, 0.2f, 0.2f, 1);

    [Test]
    public async Task MaterialInstancesRenderTheirOwnParametersAndTextures()
    {
        var (example, graph) = Compile();
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var stock = instances.Allocate();
        var tuned = instances.Allocate();
        var checker = Texture.Checker(Light, Dark);
        var blue = new Color(0, 0, 1, 1);
        graph.SetVariable(instances, stock, example.AlbedoMap.Id, checker);
        graph.SetVariable(instances, tuned, example.AlbedoMap.Id, checker);
        var typed = graph.SetVariable(instances, tuned, example.Tint.Id, blue);
        graph.SetVariable(instances, tuned, example.Intensity.Id, 1f);

        var stockPixels = await MaterialRenderer.RenderAsync(graph, instances, stock, example.Albedo.Id, 4, 4);
        var tunedPixels = await MaterialRenderer.RenderAsync(graph, instances, tuned, example.Albedo.Id, 4, 4);

        await Assert.That(typed).IsTrue();
        await Assert.That(graph.Outputs).IsEquivalentTo([example.Albedo.Id]);
        await Assert.That(stockPixels[0]).IsEqualTo(MaterialExample.Expected(Light, MaterialExample.DefaultTint, 2));
        await Assert.That(stockPixels[2]).IsEqualTo(MaterialExample.Expected(Dark, MaterialExample.DefaultTint, 2));
        await Assert.That(tunedPixels[0]).IsEqualTo(MaterialExample.Expected(Light, blue, 1));
        await Assert.That(tunedPixels[5]).IsEqualTo(MaterialExample.Expected(Light, blue, 1));
    }

    [Test]
    public async Task ParameterSubgraphsAreCachedWhileFragmentWorkIsNot()
    {
        var (example, graph) = Compile();

        await Assert.That(graph.Data.IsCached(example.Grey)).IsTrue();
        await Assert.That(graph.Data.IsCached(example.Mix)).IsFalse();
        await Assert.That(graph.RequiredFeatures).IsEquivalentTo([typeof(Fragment)]);
        await Assert.That(example.Document.Nodes.Count(node => node.TypeId == ShaderLibrary.ScalarToColor.Id))
            .IsEqualTo(1);
    }

    [Test]
    public async Task SavedMaterialsReloadAndRenderIdentically()
    {
        var (example, graph) = Compile();
        var registry = new GraphTypeRegistry();
        ShaderLibrary.Register(registry);
        var json = GraphJson.Serialize(example.Document);
        var loaded = GraphJson.Deserialize(json, registry);
        var reloaded = new GraphInterpreter(ShaderLibrary.CreateDataflow()).Compile(loaded.Document!).Graph!;
        var original = await Render(graph, example);
        var copy = await Render(reloaded, example);

        await Assert.That(loaded.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(GraphJson.Serialize(loaded.Document!)).IsEqualTo(json);
        await Assert.That(copy).IsEquivalentTo(original);
    }

    [Test]
    public async Task TheInterpreterMatchesItselfOverAShadingScenario()
    {
        var (example, _) = Compile();
        var interpreter = new GraphInterpreter(ShaderLibrary.CreateDataflow());
        var checker = Texture.Checker(Light, Dark);
        List<ScenarioStep> steps =
        [
            new SetVariableStep(example.AlbedoMap.Id, checker),
            new SetVariableStep(example.Tint.Id, new Color(0, 1, 0, 1), Instance: 1),
        ];
        foreach (var uv in new Uv[] { new(0.1f, 0.1f), new(0.6f, 0.1f), new(0.6f, 0.6f) })
        {
            steps.Add(new ConfigureStep(context => context.Get<Fragment>().Uv = uv));
            steps.Add(new EvaluateStep());
        }
        var scenario = new Scenario("shading", 2, steps)
        {
            CreateContext = static () => new EvaluationContext().Set(new Fragment()),
        };

        var report = await DifferentialHarness.CompareAsync(new(example.Document), interpreter, interpreter, scenario);

        await Assert.That(report.Equivalent).IsTrue();
    }

    [Test]
    public async Task DefaultPayloadsAndDeclaredOutputsRenderAndBrokenRegistriesFailLoudly()
    {
        var registry = new GraphTypeRegistry();
        ShaderLibrary.Register(registry);
        var data = ShaderLibrary.CreateDataflow();
        var editor = new GraphEditor(new GraphDocument(registry), data.Conversions, data.Rules);
        var albedo = new GraphVariable(ElementId.New(), "albedo", ShaderLibrary.ColorType.Id, VariableScope.Output);
        var red = new AddNodeCommand(ShaderLibrary.ColorConstant, new ColorPayload(new(1, 0, 0, 1)));
        var black = new AddNodeCommand(ShaderLibrary.ColorConstant);
        var output = new AddNodeCommand(DataflowNodes.Output, new VariableReference(albedo.Id));
        editor.Execute(new CompositeCommand("Flat red", [new SetVariableCommand(albedo), red, black, output]));
        editor.Connect(red.PortId(ShaderLibrary.OutKey), output.PortId(DataflowNodes.ResultKey));
        var loaded = GraphJson.Deserialize(GraphJson.Serialize(editor.Document), registry).Document!;
        var graph = new GraphInterpreter(data).Compile(loaded).Graph!;
        var instances = new InstanceBuffer(graph.InstanceLayout);

        var pixels = await MaterialRenderer.RenderAsync(graph, instances, instances.Allocate(), albedo.Id, 1, 1);

        await Assert.That(pixels.Single()).IsEqualTo(new Color(1, 0, 0, 1));
        await Assert.That(black.Payload).IsEqualTo(new ColorPayload(new(0, 0, 0, 1)));
        await Assert.That(new AddNodeCommand(ShaderLibrary.ScalarConstant).Payload).IsEqualTo(new ScalarPayload(0));
        await Assert.That(() => MaterialExample.Build(data, new GraphTypeRegistry()))
            .ThrowsExactly<InvalidOperationException>();
    }

    static (MaterialExample Example, InterpretedGraph Graph) Compile()
    {
        var registry = new GraphTypeRegistry();
        ShaderLibrary.Register(registry);
        var data = ShaderLibrary.CreateDataflow();
        var example = MaterialExample.Build(data, registry);
        var result = new GraphInterpreter(data).Compile(example.Document);
        return (example, (InterpretedGraph)(result.Graph ?? throw new InvalidOperationException(
            string.Join("; ", result.Diagnostics))));
    }

    static async Task<Color[]> Render(ICompiledGraph graph, MaterialExample example)
    {
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var instance = instances.Allocate();
        graph.SetVariable(instances, instance, example.AlbedoMap.Id, Texture.Checker(Light, Dark));
        return await MaterialRenderer.RenderAsync(graph, instances, instance, example.Albedo.Id, 4, 4);
    }
}
