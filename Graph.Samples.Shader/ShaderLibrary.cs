using System.Text.Json;
using MASS4.Graph.Dataflow;

namespace MASS4.Graph.Samples.Shader;

/// <summary>Payload of a scalar constant.</summary>
/// <param name="Value">The constant.</param>
public sealed record ScalarPayload(float Value);

/// <summary>Payload of a colour constant.</summary>
/// <param name="Value">The constant.</param>
public sealed record ColorPayload(Color Value);

/// <summary>
/// The material domain, registered by hand: port types, node types with their port keys, payload and value
/// serializers, and dataflow behaviors addressed by port index.
/// </summary>
public static class ShaderLibrary
{
    /// <summary>Scalar values.</summary>
    public static PortType Scalar { get; } = new(Id(0x001), "scalar") { ValueSerializer = ScalarSerializer.Instance };

    /// <summary>Colour values.</summary>
    public static PortType ColorType { get; } = new(Id(0x002), "color") { ValueSerializer = ColorSerializer.Instance };

    /// <summary>Texture coordinates.</summary>
    public static PortType UvType { get; } = new(Id(0x003), "uv");

    /// <summary>Textures; object references set per material instance.</summary>
    public static PortType TextureType { get; } = new(Id(0x004), "texture");

    /// <summary>Output of every single-output node.</summary>
    public static StableId OutKey { get; } = Id(0x100);

    /// <summary>First input of two-input nodes and the only input of one-input nodes.</summary>
    public static StableId AKey { get; } = Id(0x101);

    /// <summary>Second input of two-input nodes.</summary>
    public static StableId BKey { get; } = Id(0x102);

    /// <summary>Interpolation factor of the lerp node.</summary>
    public static StableId TKey { get; } = Id(0x103);

    /// <summary>Albedo input of the material output.</summary>
    public static StableId AlbedoKey { get; } = Id(0x104);

    /// <summary>A scalar constant; payload <see cref="ScalarPayload"/>.</summary>
    public static NodeType ScalarConstant { get; } = new(Id(0x201), "Scalar", [Out(Scalar)])
    {
        PayloadSerializer = ScalarPayloadSerializer.Instance,
    };

    /// <summary>A colour constant; payload <see cref="ColorPayload"/>.</summary>
    public static NodeType ColorConstant { get; } = new(Id(0x202), "Color", [Out(ColorType)])
    {
        PayloadSerializer = ColorPayloadSerializer.Instance,
    };

    /// <summary>The texture coordinates of the fragment being shaded.</summary>
    public static NodeType TexCoord { get; } = new(Id(0x203), "Texture coordinates", [Out(UvType)]);

    /// <summary>Samples a texture at coordinates.</summary>
    public static NodeType SampleTexture { get; } = new(Id(0x204), "Sample texture",
        [In(AKey, "texture", TextureType), In(BKey, "uv", UvType), Out(ColorType)]);

    /// <summary>Multiplies two colours.</summary>
    public static NodeType Multiply { get; } = new(Id(0x205), "Multiply",
        [In(AKey, "a", ColorType), In(BKey, "b", ColorType), Out(ColorType)]);

    /// <summary>Scales a colour by a scalar.</summary>
    public static NodeType ScaleColor { get; } = new(Id(0x206), "Scale",
        [In(AKey, "color", ColorType), In(BKey, "factor", Scalar), Out(ColorType)]);

    /// <summary>Interpolates between two colours.</summary>
    public static NodeType Lerp { get; } = new(Id(0x207), "Lerp",
        [In(AKey, "a", ColorType), In(BKey, "b", ColorType), In(TKey, "t", Scalar), Out(ColorType)]);

    /// <summary>Conversion editors insert between scalar outputs and colour inputs.</summary>
    public static NodeType ScalarToColor { get; } = new(Id(0x208), "Scalar to color",
        [In(AKey, "scalar", Scalar), Out(ColorType)]);

    /// <summary>The material output; renderers read its albedo input.</summary>
    public static NodeType MaterialOutput { get; } = new(Id(0x209), "Material output",
        [In(AlbedoKey, "albedo", ColorType)]);

    /// <summary>Registers the port and node types, plus the built-in dataflow types they rely on.</summary>
    public static void Register(GraphTypeRegistry registry)
    {
        foreach (var type in new[] { Scalar, ColorType, UvType, TextureType })
        {
            registry.PortTypes.Register(type);
        }
        NodeType[] nodes =
        [
            ScalarConstant, ColorConstant, TexCoord, SampleTexture, Multiply, ScaleColor, Lerp, ScalarToColor,
            MaterialOutput,
        ];
        foreach (var node in nodes)
        {
            registry.NodeTypes.Register(node);
        }
        DataflowNodes.Register(registry);
    }

    /// <summary>Creates dataflow semantics with the material value types, behaviors and conversion.</summary>
    public static DataflowSemantics CreateDataflow() => AddOperators(AddSources(new DataflowSemantics()
        .AddValueType<float>(Scalar.Id)
        .AddValueType<Color>(ColorType.Id)
        .AddValueType<Uv>(UvType.Id)
        .AddReferenceType<Texture>(TextureType.Id)));

    static DataflowSemantics AddSources(DataflowSemantics semantics) => semantics
        .AddNode(ScalarConstant.Id, new DataflowNode(static c => c.Write(0, ((ScalarPayload)c.Payload!).Value)))
        .AddNode(ColorConstant.Id, new DataflowNode(static c => c.Write(0, ((ColorPayload)c.Payload!).Value)))
        .AddNode(TexCoord.Id, new DataflowNode(static c =>
            c.Write(0, c.Evaluation.TryGet<Fragment>(out var fragment) ? fragment.Uv : default), isVolatile: true));

    static DataflowSemantics AddOperators(DataflowSemantics semantics) => semantics
        .AddNode(SampleTexture.Id, new DataflowNode(static c =>
            c.Write(2, c.ReadReference<Texture>(0)?.Sample(c.Read<Uv>(1)) ?? default)))
        .AddNode(Multiply.Id, new DataflowNode(static c => c.Write(2, c.Read<Color>(0) * c.Read<Color>(1))))
        .AddNode(ScaleColor.Id, new DataflowNode(static c => c.Write(2, c.Read<Color>(0) * c.Read<float>(1))))
        .AddNode(Lerp.Id, new DataflowNode(static c =>
            c.Write(3, Color.Lerp(c.Read<Color>(0), c.Read<Color>(1), c.Read<float>(2)))))
        .AddConversionNode(ScalarToColor, new DataflowNode(static c => c.Write(1, Color.Splat(c.Read<float>(0)))));

    static PortDeclaration In(StableId key, string name, PortType type) => PortDeclaration.Input(key, name, type.Id);

    static PortDeclaration Out(PortType type) => PortDeclaration.Output(OutKey, "out", type.Id);

    static StableId Id(int value) => new(new Guid($"5ade0000-0000-4000-8000-{value:x12}"));

    sealed class ScalarSerializer : IValueSerializer
    {
        public static ScalarSerializer Instance { get; } = new();

        public void Write(Utf8JsonWriter writer, object? value) => writer.WriteNumberValue((float)value!);

        public object? Read(JsonElement element) => element.GetSingle();
    }

    sealed class ColorSerializer : IValueSerializer
    {
        public static ColorSerializer Instance { get; } = new();

        public void Write(Utf8JsonWriter writer, object? value)
        {
            var color = (Color)value!;
            writer.WriteStartArray();
            writer.WriteNumberValue(color.R);
            writer.WriteNumberValue(color.G);
            writer.WriteNumberValue(color.B);
            writer.WriteNumberValue(color.A);
            writer.WriteEndArray();
        }

        public object? Read(JsonElement element) =>
            new Color(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle(), element[3].GetSingle());
    }

    sealed class ScalarPayloadSerializer : IValueSerializer
    {
        public static ScalarPayloadSerializer Instance { get; } = new();

        public void Write(Utf8JsonWriter writer, object? value) =>
            ScalarSerializer.Instance.Write(writer, ((ScalarPayload)value!).Value);

        public object? Read(JsonElement element) => new ScalarPayload(element.GetSingle());
    }

    sealed class ColorPayloadSerializer : IValueSerializer
    {
        public static ColorPayloadSerializer Instance { get; } = new();

        public void Write(Utf8JsonWriter writer, object? value) =>
            ColorSerializer.Instance.Write(writer, ((ColorPayload)value!).Value);

        public object? Read(JsonElement element) => new ColorPayload((Color)ColorSerializer.Instance.Read(element)!);
    }
}
