using System.Text.Json.Serialization;
using MASS4.Graph.Dataflow;

[assembly: MASS4.Graph.GraphModule("MASS4.Graph.Samples.Shader.Generated.ShaderModule", typeof(DataflowNodes))]

namespace MASS4.Graph.Samples.Shader.Generated;

/// <summary>Wire-compatible scalar declaration.</summary>
[GraphValue("5ade0000-0000-4000-8000-000000000001", "scalar", typeof(float), JsonContext = typeof(ShaderJson))]
public static partial class Scalar;

/// <summary>Wire-compatible colour declaration.</summary>
[GraphValue("5ade0000-0000-4000-8000-000000000002", "color", typeof(Color), Serializer = typeof(ShaderLibrary.ColorSerializer))]
public static partial class Colour;

/// <summary>Texture coordinate declaration.</summary>
[GraphValue("5ade0000-0000-4000-8000-000000000003", "uv", typeof(Uv))]
public static partial class Coordinates;

/// <summary>Texture reference declaration.</summary>
[GraphValue("5ade0000-0000-4000-8000-000000000004", "texture", typeof(Texture))]
public static partial class Textures;

/// <summary>Scalar constant declaration.</summary>
[GraphNode("5ade0000-0000-4000-8000-000000000201", "Scalar")]
[GraphPort("Out", "5ade0000-0000-4000-8000-000000000100", "out", PortDirection.Output, "5ade0000-0000-4000-8000-000000000001", typeof(float))]
[GraphPayload(typeof(ScalarPayload), Value = typeof(Scalar), DefaultFactory = nameof(Default))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Constant), Method = nameof(Evaluate))]
public static partial class Constant
{
    static ScalarPayload Default() => new(0);
    internal static void Evaluate(DataflowContext c) => c.Write(Indices.Out, ((ScalarPayload)c.Payload!).Value);
}

/// <summary>Colour constant declaration.</summary>
[GraphNode("5ade0000-0000-4000-8000-000000000202", "Color")]
[GraphPort("Out", "5ade0000-0000-4000-8000-000000000100", "out", PortDirection.Output, "5ade0000-0000-4000-8000-000000000002", typeof(Color))]
[GraphPayload(typeof(ColorPayload), Value = typeof(Colour), DefaultFactory = nameof(Default))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(ColourConstant), Method = nameof(Evaluate))]
public static partial class ColourConstant
{
    static ColorPayload Default() => new(new(0, 0, 0, 1));
    internal static void Evaluate(DataflowContext c) => c.Write(Indices.Out, ((ColorPayload)c.Payload!).Value);
}

/// <summary>Fragment coordinate declaration.</summary>
[GraphNode("5ade0000-0000-4000-8000-000000000203", "Texture coordinates")]
[GraphPort("Out", "5ade0000-0000-4000-8000-000000000100", "out", PortDirection.Output, "5ade0000-0000-4000-8000-000000000003", typeof(Uv))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(TexCoord), Method = nameof(Evaluate), Volatile = true, Features = new[] { typeof(Fragment) })]
public static partial class TexCoord
{
    internal static void Evaluate(DataflowContext c) => c.Write(Indices.Out, c.Evaluation.Get<Fragment>().Uv);
}

/// <summary>Texture sampling declaration.</summary>
[GraphNode("5ade0000-0000-4000-8000-000000000204", "Sample texture")]
[GraphPort("Texture", "5ade0000-0000-4000-8000-000000000101", "texture", PortDirection.Input, "5ade0000-0000-4000-8000-000000000004", typeof(Texture))]
[GraphPort("Uv", "5ade0000-0000-4000-8000-000000000102", "uv", PortDirection.Input, "5ade0000-0000-4000-8000-000000000003", typeof(Uv))]
[GraphPort("Out", "5ade0000-0000-4000-8000-000000000100", "out", PortDirection.Output, "5ade0000-0000-4000-8000-000000000002", typeof(Color))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(SampleTexture), Method = nameof(Evaluate))]
public static partial class SampleTexture
{
    internal static void Evaluate(DataflowContext c) =>
        c.Write(Indices.Out, c.ReadReference<Texture>(Indices.Texture)?.Sample(c.Read<Uv>(Indices.Uv)) ?? default);
}

/// <summary>Colour multiplication declaration.</summary>
[GraphNode("5ade0000-0000-4000-8000-000000000205", "Multiply")]
[GraphPort("A", "5ade0000-0000-4000-8000-000000000101", "a", PortDirection.Input, "5ade0000-0000-4000-8000-000000000002", typeof(Color))]
[GraphPort("B", "5ade0000-0000-4000-8000-000000000102", "b", PortDirection.Input, "5ade0000-0000-4000-8000-000000000002", typeof(Color))]
[GraphPort("Out", "5ade0000-0000-4000-8000-000000000100", "out", PortDirection.Output, "5ade0000-0000-4000-8000-000000000002", typeof(Color))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Multiply), Method = nameof(Evaluate))]
public static partial class Multiply
{
    internal static void Evaluate(DataflowContext c) => c.Write(Indices.Out, c.Read<Color>(Indices.A) * c.Read<Color>(Indices.B));
}

/// <summary>Colour scaling declaration.</summary>
[GraphNode("5ade0000-0000-4000-8000-000000000206", "Scale")]
[GraphPort("Colour", "5ade0000-0000-4000-8000-000000000101", "color", PortDirection.Input, "5ade0000-0000-4000-8000-000000000002", typeof(Color))]
[GraphPort("Factor", "5ade0000-0000-4000-8000-000000000102", "factor", PortDirection.Input, "5ade0000-0000-4000-8000-000000000001", typeof(float))]
[GraphPort("Out", "5ade0000-0000-4000-8000-000000000100", "out", PortDirection.Output, "5ade0000-0000-4000-8000-000000000002", typeof(Color))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Scale), Method = nameof(Evaluate))]
public static partial class Scale
{
    internal static void Evaluate(DataflowContext c) => c.Write(Indices.Out, c.Read<Color>(Indices.Colour) * c.Read<float>(Indices.Factor));
}

/// <summary>Colour interpolation declaration.</summary>
[GraphNode("5ade0000-0000-4000-8000-000000000207", "Lerp")]
[GraphPort("A", "5ade0000-0000-4000-8000-000000000101", "a", PortDirection.Input, "5ade0000-0000-4000-8000-000000000002", typeof(Color))]
[GraphPort("B", "5ade0000-0000-4000-8000-000000000102", "b", PortDirection.Input, "5ade0000-0000-4000-8000-000000000002", typeof(Color))]
[GraphPort("T", "5ade0000-0000-4000-8000-000000000103", "t", PortDirection.Input, "5ade0000-0000-4000-8000-000000000001", typeof(float))]
[GraphPort("Out", "5ade0000-0000-4000-8000-000000000100", "out", PortDirection.Output, "5ade0000-0000-4000-8000-000000000002", typeof(Color))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Lerp), Method = nameof(Evaluate))]
public static partial class Lerp
{
    internal static void Evaluate(DataflowContext c) =>
        c.Write(Indices.Out, Color.Lerp(c.Read<Color>(Indices.A), c.Read<Color>(Indices.B), c.Read<float>(Indices.T)));
}

/// <summary>Explicit scalar-to-colour conversion declaration.</summary>
[GraphNode("5ade0000-0000-4000-8000-000000000208", "Scalar to color", Conversion = true)]
[GraphPort("Scalar", "5ade0000-0000-4000-8000-000000000101", "scalar", PortDirection.Input, "5ade0000-0000-4000-8000-000000000001", typeof(float))]
[GraphPort("Out", "5ade0000-0000-4000-8000-000000000100", "out", PortDirection.Output, "5ade0000-0000-4000-8000-000000000002", typeof(Color))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(ScalarToColor), Method = nameof(Evaluate))]
public static partial class ScalarToColor
{
    internal static void Evaluate(DataflowContext c) => c.Write(Indices.Out, Color.Splat(c.Read<float>(Indices.Scalar)));
}

[JsonSerializable(typeof(float))]
partial class ShaderJson : JsonSerializerContext;
