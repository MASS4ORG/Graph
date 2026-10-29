using System.Text.Json.Serialization;
using MASS4.Graph.Dataflow;

[assembly: MASS4.Graph.GraphModule("MASS4.Graph.Samples.Compositor.Generated.CompositorModule", typeof(DataflowNodes))]

namespace MASS4.Graph.Samples.Compositor.Generated;

/// <summary>Timeline number declaration.</summary>
[GraphValue("c0b0f000-0000-4000-8000-000000000001", "number", typeof(double))]
public static partial class Number;

/// <summary>Frame reference declaration.</summary>
[GraphValue("c0b0f000-0000-4000-8000-000000000002", "frame", typeof(Frame))]
public static partial class Frames;

/// <summary>Timeline feature declaration.</summary>
[GraphNode("c0b0f000-0000-4000-8000-000000000201", "Time")]
[GraphPort("Out", "c0b0f000-0000-4000-8000-000000000100", "out", PortDirection.Output, typeof(Number))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Time), Method = nameof(Evaluate), Volatile = true, Features = new[] { typeof(Timeline) })]
public static partial class Time
{
    internal static void Evaluate(DataflowContext c) => c.Write(Indices.Out, c.Evaluation.Get<Timeline>().Seconds);
}

/// <summary>Scrolling ramp declaration with reusable frame output.</summary>
[GraphNode("c0b0f000-0000-4000-8000-000000000202", "Ramp")]
[GraphPort("Out", "c0b0f000-0000-4000-8000-000000000100", "out", PortDirection.Output, typeof(Frames))]
[GraphPayload(typeof(RampPayload), JsonContext = typeof(CompositorJson), DefaultFactory = nameof(Default))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Ramp), Method = nameof(Evaluate), Volatile = true, Features = new[] { typeof(Timeline), typeof(RenderSettings) })]
public static partial class Ramp
{
    static RampPayload Default() => new(0);
    internal static void Evaluate(DataflowContext c)
    {
        var size = c.Evaluation.Get<RenderSettings>();
        var frame = ReuseOut(c, size, static (previous, settings) => Frame.Reuse(previous, settings.Width, settings.Height));
        var speed = ((RampPayload)c.Payload!).Speed;
        var seconds = c.Evaluation.Get<Timeline>().Seconds;
        for (var i = 0; i < frame.Luma.Length; i++)
        {
            frame.Luma[i] = Compositing.Ramp(i % frame.Width, frame.Width, speed, seconds);
        }
        c.WriteReference(Indices.Out, frame);
    }
}

/// <summary>Pulsing frame declaration.</summary>
[GraphNode("c0b0f000-0000-4000-8000-000000000203", "Pulse")]
[GraphPort("Out", "c0b0f000-0000-4000-8000-000000000100", "out", PortDirection.Output, typeof(Frames))]
[GraphPayload(typeof(PulsePayload), JsonContext = typeof(CompositorJson), DefaultFactory = nameof(Default))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Pulse), Method = nameof(Evaluate), Volatile = true, Features = new[] { typeof(Timeline), typeof(RenderSettings) })]
public static partial class Pulse
{
    static PulsePayload Default() => new(1);
    internal static void Evaluate(DataflowContext c)
    {
        var size = c.Evaluation.Get<RenderSettings>();
        var frame = ReuseOut(c, size, static (previous, settings) => Frame.Reuse(previous, settings.Width, settings.Height));
        frame.Luma.AsSpan().Fill(Compositing.Pulse(((PulsePayload)c.Payload!).Frequency, c.Evaluation.Get<Timeline>().Seconds));
        c.WriteReference(Indices.Out, frame);
    }
}

/// <summary>Envelope declaration.</summary>
[GraphNode("c0b0f000-0000-4000-8000-000000000204", "Envelope")]
[GraphPort("Time", "c0b0f000-0000-4000-8000-000000000101", "time", PortDirection.Input, typeof(Number))]
[GraphPort("Out", "c0b0f000-0000-4000-8000-000000000100", "out", PortDirection.Output, typeof(Number))]
[GraphPayload(typeof(EnvelopePayload), JsonContext = typeof(CompositorJson), DefaultFactory = nameof(Default))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Envelope), Method = nameof(Evaluate))]
public static partial class Envelope
{
    static EnvelopePayload Default() => new(1);
    internal static void Evaluate(DataflowContext c) =>
        c.Write(Indices.Out, Compositing.Envelope(c.Read<double>(Indices.Time), ((EnvelopePayload)c.Payload!).Duration));
}

/// <summary>Frame blend declaration.</summary>
[GraphNode("c0b0f000-0000-4000-8000-000000000205", "Over")]
[GraphPort("Top", "c0b0f000-0000-4000-8000-000000000101", "top", PortDirection.Input, typeof(Frames))]
[GraphPort("Bottom", "c0b0f000-0000-4000-8000-000000000102", "bottom", PortDirection.Input, typeof(Frames))]
[GraphPort("Mix", "c0b0f000-0000-4000-8000-000000000103", "mix", PortDirection.Input, typeof(Number))]
[GraphPort("Out", "c0b0f000-0000-4000-8000-000000000100", "out", PortDirection.Output, typeof(Frames))]
[GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Over), Method = nameof(Evaluate), Features = new[] { typeof(RenderSettings) })]
public static partial class Over
{
    internal static void Evaluate(DataflowContext c)
    {
        var size = c.Evaluation.Get<RenderSettings>();
        var frame = ReuseOut(c, size, static (previous, settings) => Frame.Reuse(previous, settings.Width, settings.Height));
        var top = c.ReadReference<Frame>(Indices.Top);
        var bottom = c.ReadReference<Frame>(Indices.Bottom);
        var mix = c.Read<double>(Indices.Mix);
        for (var i = 0; i < frame.Luma.Length; i++)
        {
            frame.Luma[i] = Compositing.Over(Pixel(top, i), Pixel(bottom, i), mix);
        }
        c.WriteReference(Indices.Out, frame);
    }
    static float Pixel(Frame? frame, int index) => frame is not null && index < frame.Luma.Length ? frame.Luma[index] : 0;
}

/// <summary>The composition asset compiled ahead of time into a generated backend.</summary>
[GraphAsset("Assets/composition.graph.json")]
public sealed partial class CompositionGraph;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RampPayload))]
[JsonSerializable(typeof(PulsePayload))]
[JsonSerializable(typeof(EnvelopePayload))]
partial class CompositorJson : JsonSerializerContext;
