using MASS4.Graph.Dataflow;

namespace MASS4.Graph.Samples.Shader;

/// <summary>
/// An example material: albedo = lerp(texture × tint × intensity, grey, 0.25). Tint and intensity are exposed
/// parameters, the texture is an external reference, the grey comes from a scalar through a conversion node the
/// editor inserts, and the albedo is the material's declared output.
/// </summary>
/// <param name="Document">The material graph.</param>
/// <param name="Tint">Exposed colour parameter.</param>
/// <param name="Intensity">Exposed scalar parameter.</param>
/// <param name="AlbedoMap">External texture reference.</param>
/// <param name="Albedo">The declared albedo output; renderers read it.</param>
/// <param name="Grey">The inserted conversion node, cached per instance.</param>
/// <param name="Mix">The lerp node, recomputed per fragment.</param>
public sealed record MaterialExample(
    GraphDocument Document,
    GraphVariable Tint,
    GraphVariable Intensity,
    GraphVariable AlbedoMap,
    GraphVariable Albedo,
    ElementId Grey,
    ElementId Mix)
{
    /// <summary>Default tint.</summary>
    public static Color DefaultTint { get; } = new(1, 0.5f, 0.25f, 1);

    /// <summary>Builds the material through editor commands, as an authoring tool would.</summary>
    /// <exception cref="InvalidOperationException">A command fails; the example is broken.</exception>
    public static MaterialExample Build(DataflowSemantics data, GraphTypeRegistry registry)
    {
        var editor = new GraphEditor(new GraphDocument(registry), data.Conversions, data.Rules);
        var tint = Variable(editor, "tint", ShaderLibrary.ColorType, VariableScope.Input, DefaultTint);
        var intensity = Variable(editor, "intensity", ShaderLibrary.Scalar, VariableScope.Input, 2f);
        var albedoMap = Variable(editor, "albedo map", ShaderLibrary.TextureType, VariableScope.External, null);
        var albedo = Variable(editor, "albedo", ShaderLibrary.ColorType, VariableScope.Output, new Color(0, 0, 0, 1));
        var map = Add(editor, DataflowNodes.GetVariable, new VariableReference(albedoMap.Id));
        var tintRead = Add(editor, DataflowNodes.GetVariable, new VariableReference(tint.Id));
        var intensityRead = Add(editor, DataflowNodes.GetVariable, new VariableReference(intensity.Id));
        var uv = Add(editor, ShaderLibrary.TexCoord);
        var sample = Add(editor, ShaderLibrary.SampleTexture);
        var tinted = Add(editor, ShaderLibrary.Multiply);
        var boosted = Add(editor, ShaderLibrary.ScaleColor);
        var half = Add(editor, ShaderLibrary.ScalarConstant, new ScalarPayload(0.5f));
        var amount = Add(editor, ShaderLibrary.ScalarConstant, new ScalarPayload(0.25f));
        var mix = Add(editor, ShaderLibrary.Lerp);
        var output = Add(editor, DataflowNodes.Output, new VariableReference(albedo.Id));
        Connect(editor, map, DataflowNodes.ValueKey, sample, ShaderLibrary.AKey);
        Connect(editor, uv, ShaderLibrary.OutKey, sample, ShaderLibrary.BKey);
        Connect(editor, sample, ShaderLibrary.OutKey, tinted, ShaderLibrary.AKey);
        Connect(editor, tintRead, DataflowNodes.ValueKey, tinted, ShaderLibrary.BKey);
        Connect(editor, tinted, ShaderLibrary.OutKey, boosted, ShaderLibrary.AKey);
        Connect(editor, intensityRead, DataflowNodes.ValueKey, boosted, ShaderLibrary.BKey);
        Connect(editor, boosted, ShaderLibrary.OutKey, mix, ShaderLibrary.AKey);
        Connect(editor, half, ShaderLibrary.OutKey, mix, ShaderLibrary.BKey);
        Connect(editor, amount, ShaderLibrary.OutKey, mix, ShaderLibrary.TKey);
        Connect(editor, mix, ShaderLibrary.OutKey, output, DataflowNodes.ResultKey);
        var grey = editor.Document.Nodes.Single(node => node.TypeId == ShaderLibrary.ScalarToColor.Id);
        return new(editor.Document, tint, intensity, albedoMap, albedo, grey.Id, mix.NodeId);
    }

    /// <summary>The albedo the example computes for one texel, tint and intensity.</summary>
    public static Color Expected(Color texel, Color tint, float intensity) =>
        Color.Lerp(texel * tint * intensity, Color.Splat(0.5f), 0.25f);

    static GraphVariable Variable(GraphEditor editor, string name, PortType type, VariableScope scope, object? value)
    {
        var variable = new GraphVariable(ElementId.New(), name, type.Id, scope) { DefaultValue = value };
        Require(editor.Execute(new SetVariableCommand(variable)));
        return variable;
    }

    static AddNodeCommand Add(GraphEditor editor, NodeType type, object? payload = null)
    {
        var command = new AddNodeCommand(type, payload);
        Require(editor.Execute(command));
        return command;
    }

    static void Connect(GraphEditor editor, AddNodeCommand from, StableId output, AddNodeCommand to, StableId input) =>
        Require(editor.Connect(from.PortId(output), to.PortId(input)));

    static void Require(CommandResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
        }
    }
}

/// <summary>
/// Shades a grid of fragments with any backend: one evaluation per fragment, then a typed read of the material's
/// declared albedo output.
/// </summary>
public static class MaterialRenderer
{
    /// <summary>Renders a width × height grid of fragments sampled at texel centres.</summary>
    /// <exception cref="MissingFeatureException">The graph requires a feature the renderer lacks.</exception>
    public static async Task<Color[]> RenderAsync(
        ICompiledGraph graph,
        InstanceBuffer instances,
        InstanceHandle instance,
        ElementId albedo,
        int width,
        int height)
    {
        var fragment = new Fragment();
        var context = new EvaluationContext().Set(fragment);
        var pixels = new Color[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                fragment.Uv = new((x + 0.5f) / width, (y + 0.5f) / height);
                await graph.EvaluateAsync(context, instances, instance);
                pixels[y * width + x] = graph.ReadOutput<Color>(context, instances, instance, albedo);
            }
        }
        return pixels;
    }
}
