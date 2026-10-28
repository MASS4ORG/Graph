using System.Text.Json;
using MASS4.Graph.Dataflow;

namespace MASS4.Graph.Samples.Compositor;

/// <summary>Payload of the ramp source.</summary>
/// <param name="Speed">Horizontal scroll in frame widths per second.</param>
public sealed record RampPayload(double Speed);

/// <summary>Payload of the pulse source.</summary>
/// <param name="Frequency">Pulses per second.</param>
public sealed record PulsePayload(double Frequency);

/// <summary>Payload of the envelope.</summary>
/// <param name="Duration">Seconds the envelope takes to rise from 0 to 1.</param>
public sealed record EnvelopePayload(double Duration);

/// <summary>The pixel math of the compositor nodes, shared with tests that state expected images.</summary>
public static class Compositing
{
    /// <summary>Luminance of a horizontally scrolling ramp.</summary>
    public static float Ramp(int x, int width, double speed, double seconds)
    {
        var position = (double)x / width + speed * seconds;
        return (float)(position - Math.Floor(position));
    }

    /// <summary>Luminance of a uniform sine pulse.</summary>
    public static float Pulse(double frequency, double seconds) =>
        (float)(0.5 + 0.5 * Math.Sin(2 * Math.PI * frequency * seconds));

    /// <summary>A linear rise from 0 to 1 over <paramref name="duration"/> seconds.</summary>
    public static double Envelope(double seconds, double duration) => Math.Clamp(seconds / duration, 0, 1);

    /// <summary>Blends a top pixel over a bottom pixel.</summary>
    public static float Over(float top, float bottom, double mix) => (float)(top * mix + bottom * (1 - mix));
}

/// <summary>
/// The compositor domain, registered by hand: numbers and frames, time-driven sources, an envelope, a blend and an
/// output. Nothing here references an engine.
/// </summary>
public static class CompositorLibrary
{
    /// <summary>Numbers (times, mixes).</summary>
    public static PortType Number { get; } = new(Id(0x001), "number");

    /// <summary>Frames; object references compared by content.</summary>
    public static PortType FrameType { get; } = new(Id(0x002), "frame");

    /// <summary>Output of every single-output node.</summary>
    public static StableId OutKey { get; } = Id(0x100);

    /// <summary>The only or first input.</summary>
    public static StableId AKey { get; } = Id(0x101);

    /// <summary>The second input.</summary>
    public static StableId BKey { get; } = Id(0x102);

    /// <summary>The mix input of the blend.</summary>
    public static StableId MixKey { get; } = Id(0x103);

    /// <summary>The timeline time.</summary>
    public static NodeType Time { get; } = new(Id(0x201), "Time", [Out(Number)]);

    /// <summary>A scrolling ramp; payload <see cref="RampPayload"/>, still by default.</summary>
    public static NodeType Ramp { get; } = new(Id(0x202), "Ramp", [Out(FrameType)])
    {
        PayloadSerializer = new NumberPayloadSerializer<RampPayload>("speed", static v => new(v), static p => p.Speed),
        DefaultPayload = static () => new RampPayload(0),
    };

    /// <summary>A uniform pulse; payload <see cref="PulsePayload"/>, 1 Hz by default.</summary>
    public static NodeType Pulse { get; } = new(Id(0x203), "Pulse", [Out(FrameType)])
    {
        PayloadSerializer =
            new NumberPayloadSerializer<PulsePayload>("frequency", static v => new(v), static p => p.Frequency),
        DefaultPayload = static () => new PulsePayload(1),
    };

    /// <summary>A rise from 0 to 1 over time; payload <see cref="EnvelopePayload"/>, one second by default.</summary>
    public static NodeType Envelope { get; } = new(Id(0x204), "Envelope",
        [PortDeclaration.Input(AKey, "time", Number.Id), Out(Number)])
    {
        PayloadSerializer =
            new NumberPayloadSerializer<EnvelopePayload>("duration", static v => new(v), static p => p.Duration),
        DefaultPayload = static () => new EnvelopePayload(1),
    };

    /// <summary>Blends the top frame over the bottom frame.</summary>
    public static NodeType Over { get; } = new(Id(0x205), "Over",
    [
        PortDeclaration.Input(AKey, "top", FrameType.Id), PortDeclaration.Input(BKey, "bottom", FrameType.Id),
        PortDeclaration.Input(MixKey, "mix", Number.Id), Out(FrameType),
    ]);

    /// <summary>Compares frames by content, for differential tests of backends producing distinct frames.</summary>
    public static FrameComparer FrameComparer { get; } = new();

    /// <summary>
    /// Registers the port and node types. Compositions declare their result as an output-scope frame variable defined
    /// by a <see cref="DataflowNodes.Output"/> node.
    /// </summary>
    public static void Register(GraphTypeRegistry registry)
    {
        registry.PortTypes.Register(Number);
        registry.PortTypes.Register(FrameType);
        foreach (var node in new[] { Time, Ramp, Pulse, Envelope, Over })
        {
            registry.NodeTypes.Register(node);
        }
        DataflowNodes.Register(registry);
    }

    /// <summary>
    /// Creates dataflow semantics with the compositor value types and behaviors. Time-driven nodes require a
    /// <see cref="Timeline"/> and frame-producing nodes a <see cref="RenderSettings"/>; neither is defaulted.
    /// </summary>
    public static DataflowSemantics CreateDataflow() => new DataflowSemantics()
        .AddValueType<double>(Number.Id)
        .AddReferenceType<Frame>(FrameType.Id)
        .AddNode(Time.Id, new DataflowNode(static c => c.Write(0, c.Evaluation.Get<Timeline>().Seconds),
            isVolatile: true, typeof(Timeline)))
        .AddNode(Ramp.Id, new DataflowNode(RenderRamp, isVolatile: true, typeof(Timeline), typeof(RenderSettings)))
        .AddNode(Pulse.Id, new DataflowNode(RenderPulse, isVolatile: true, typeof(Timeline), typeof(RenderSettings)))
        .AddNode(Envelope.Id, new DataflowNode(static c =>
            c.Write(1, Compositing.Envelope(c.Read<double>(0), ((EnvelopePayload)c.Payload!).Duration))))
        .AddNode(Over.Id, new DataflowNode(Blend, isVolatile: false, typeof(RenderSettings)));

    static void RenderRamp(DataflowContext context)
    {
        var frame = Target(context, 0);
        var speed = ((RampPayload)context.Payload!).Speed;
        var seconds = context.Evaluation.Get<Timeline>().Seconds;
        for (var i = 0; i < frame.Luma.Length; i++)
        {
            frame.Luma[i] = Compositing.Ramp(i % frame.Width, frame.Width, speed, seconds);
        }
        context.WriteReference(0, frame);
    }

    static void RenderPulse(DataflowContext context)
    {
        var frame = Target(context, 0);
        var seconds = context.Evaluation.Get<Timeline>().Seconds;
        frame.Luma.AsSpan().Fill(Compositing.Pulse(((PulsePayload)context.Payload!).Frequency, seconds));
        context.WriteReference(0, frame);
    }

    static void Blend(DataflowContext context)
    {
        var frame = Target(context, 3);
        var top = context.ReadReference<Frame>(0);
        var bottom = context.ReadReference<Frame>(1);
        var mix = context.Read<double>(2);
        for (var i = 0; i < frame.Luma.Length; i++)
        {
            frame.Luma[i] = Compositing.Over(Pixel(top, i), Pixel(bottom, i), mix);
        }
        context.WriteReference(3, frame);
    }

    /// <summary>The node's previous output frame when its size still matches, so evaluations reuse buffers.</summary>
    static Frame Target(DataflowContext context, int output)
    {
        var size = context.Evaluation.Get<RenderSettings>();
        return Frame.Reuse(context.ReadReference<Frame>(output), size.Width, size.Height);
    }

    static float Pixel(Frame? frame, int index) =>
        frame is not null && index < frame.Luma.Length ? frame.Luma[index] : 0;

    static PortDeclaration Out(PortType type) => PortDeclaration.Output(OutKey, "out", type.Id);

    static StableId Id(int value) => new(new Guid($"c0b0f000-0000-4000-8000-{value:x12}"));

    /// <summary>Saves a payload holding one number as <c>{"name": value}</c>.</summary>
    sealed class NumberPayloadSerializer<T>(string name, Func<double, T> create, Func<T, double> value)
        : IValueSerializer where T : class
    {
        public void Write(Utf8JsonWriter writer, object? payload)
        {
            writer.WriteStartObject();
            writer.WriteNumber(name, value((T)payload!));
            writer.WriteEndObject();
        }

        public object? Read(JsonElement element) => create(element.GetProperty(name).GetDouble());
    }
}
