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

    /// <summary>A scrolling ramp; payload <see cref="RampPayload"/>.</summary>
    public static NodeType Ramp { get; } = new(Id(0x202), "Ramp", [Out(FrameType)])
    {
        PayloadSerializer = new NumberPayloadSerializer<RampPayload>("speed", static v => new(v), static p => p.Speed),
    };

    /// <summary>A uniform pulse; payload <see cref="PulsePayload"/>.</summary>
    public static NodeType Pulse { get; } = new(Id(0x203), "Pulse", [Out(FrameType)])
    {
        PayloadSerializer =
            new NumberPayloadSerializer<PulsePayload>("frequency", static v => new(v), static p => p.Frequency),
    };

    /// <summary>A rise from 0 to 1 over time; payload <see cref="EnvelopePayload"/>.</summary>
    public static NodeType Envelope { get; } = new(Id(0x204), "Envelope",
        [PortDeclaration.Input(AKey, "time", Number.Id), Out(Number)])
    {
        PayloadSerializer =
            new NumberPayloadSerializer<EnvelopePayload>("duration", static v => new(v), static p => p.Duration),
    };

    /// <summary>Blends the top frame over the bottom frame.</summary>
    public static NodeType Over { get; } = new(Id(0x205), "Over",
    [
        PortDeclaration.Input(AKey, "top", FrameType.Id), PortDeclaration.Input(BKey, "bottom", FrameType.Id),
        PortDeclaration.Input(MixKey, "mix", Number.Id), Out(FrameType),
    ]);

    /// <summary>The composition output; renderers read its input.</summary>
    public static NodeType Output { get; } = new(Id(0x206), "Output",
        [PortDeclaration.Input(AKey, "frame", FrameType.Id)]);

    /// <summary>Registers the port and node types.</summary>
    public static void Register(GraphTypeRegistry registry)
    {
        registry.PortTypes.Register(Number);
        registry.PortTypes.Register(FrameType);
        foreach (var node in new[] { Time, Ramp, Pulse, Envelope, Over, Output })
        {
            registry.NodeTypes.Register(node);
        }
    }

    /// <summary>Creates dataflow semantics with the compositor value types and behaviors.</summary>
    public static DataflowSemantics CreateDataflow() => new DataflowSemantics()
        .AddValueType<double>(Number.Id)
        .AddReferenceType<Frame>(FrameType.Id)
        .AddNode(Time.Id, new DataflowNode(static c => c.Write(0, Clock(c).Seconds), isVolatile: true))
        .AddNode(Ramp.Id, new DataflowNode(RenderRamp, isVolatile: true))
        .AddNode(Pulse.Id, new DataflowNode(RenderPulse, isVolatile: true))
        .AddNode(Envelope.Id, new DataflowNode(static c =>
            c.Write(1, Compositing.Envelope(c.Read<double>(0), ((EnvelopePayload)c.Payload!).Duration))))
        .AddNode(Over.Id, new DataflowNode(Blend));

    static void RenderRamp(DataflowContext context)
    {
        var frame = Target(context, 0);
        var speed = ((RampPayload)context.Payload!).Speed;
        var seconds = Clock(context).Seconds;
        for (var i = 0; i < frame.Luma.Length; i++)
        {
            frame.Luma[i] = Compositing.Ramp(i % frame.Width, frame.Width, speed, seconds);
        }
        context.WriteReference(0, frame);
    }

    static void RenderPulse(DataflowContext context)
    {
        var frame = Target(context, 0);
        frame.Luma.AsSpan().Fill(Compositing.Pulse(((PulsePayload)context.Payload!).Frequency, Clock(context).Seconds));
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
        var size = context.Evaluation.TryGet<RenderSettings>(out var settings) ? settings : DefaultSize;
        return Frame.Reuse(context.ReadReference<Frame>(output), size.Width, size.Height);
    }

    static float Pixel(Frame? frame, int index) =>
        frame is not null && index < frame.Luma.Length ? frame.Luma[index] : 0;

    static Timeline Clock(DataflowContext context) =>
        context.Evaluation.TryGet<Timeline>(out var timeline) ? timeline : Start;

    static readonly Timeline Start = new();
    static readonly RenderSettings DefaultSize = new();

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
