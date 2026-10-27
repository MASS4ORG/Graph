using MASS4.Graph.Dataflow;

namespace MASS4.Graph.Samples.Compositor;

/// <summary>
/// An example composition: a pulse fades in over a scrolling ramp, frame = over(pulse, ramp, envelope(time)). Every
/// value depends on the timeline, so the composition is reproducible for a given time; the frame is the declared
/// output.
/// </summary>
/// <param name="Document">The composition.</param>
/// <param name="Output">The declared frame output; renderers read it.</param>
/// <param name="Blend">The blend node.</param>
public sealed record CompositionExample(GraphDocument Document, GraphVariable Output, ElementId Blend)
{
    /// <summary>Ramp scroll speed in frame widths per second.</summary>
    public const double Speed = 0.25;

    /// <summary>Pulse frequency in hertz.</summary>
    public const double Frequency = 0.5;

    /// <summary>Fade-in duration in seconds.</summary>
    public const double FadeIn = 2;

    /// <summary>Builds the composition through editor commands.</summary>
    /// <exception cref="InvalidOperationException">A command fails; the example is broken.</exception>
    public static CompositionExample Build(DataflowSemantics data, GraphTypeRegistry registry)
    {
        var editor = new GraphEditor(new GraphDocument(registry), data.Conversions, data.Rules);
        var frame = new GraphVariable(ElementId.New(), "frame", CompositorLibrary.FrameType.Id, VariableScope.Output);
        var time = Add(registry, CompositorLibrary.Time);
        var ramp = Add(registry, CompositorLibrary.Ramp, new RampPayload(Speed));
        var pulse = Add(registry, CompositorLibrary.Pulse, new PulsePayload(Frequency));
        var envelope = Add(registry, CompositorLibrary.Envelope, new EnvelopePayload(FadeIn));
        var blend = Add(registry, CompositorLibrary.Over);
        var output = new AddNodeCommand(DataflowNodes.Output, new VariableReference(frame.Id));
        Require(editor.Execute(new CompositeCommand("Fade a pulse over a ramp",
        [
            new SetVariableCommand(frame), time, ramp, pulse, envelope, blend, output,
            Wire(time, envelope, CompositorLibrary.AKey),
            Wire(pulse, blend, CompositorLibrary.AKey),
            Wire(ramp, blend, CompositorLibrary.BKey),
            Wire(envelope, blend, CompositorLibrary.MixKey),
            new ConnectCommand(blend.PortId(CompositorLibrary.OutKey), output.PortId(DataflowNodes.ResultKey)),
        ])));
        return new(editor.Document, frame, blend.NodeId);
    }

    /// <summary>The luminance the composition produces at a pixel column and time.</summary>
    public static float Expected(int x, int width, double seconds) =>
        Compositing.Over(Compositing.Pulse(Frequency, seconds), Compositing.Ramp(x, width, Speed, seconds),
            Compositing.Envelope(seconds, FadeIn));

    static AddNodeCommand Add(GraphTypeRegistry registry, NodeType type, object? payload = null) =>
        registry.NodeTypes.TryResolve(type.Id, out var registered) ? new(registered, payload)
            : throw new InvalidOperationException($"Node type {type.Id} is not registered.");

    static ConnectCommand Wire(AddNodeCommand from, AddNodeCommand to, StableId input) =>
        new(from.PortId(CompositorLibrary.OutKey), to.PortId(input));

    static void Require(CommandResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
        }
    }
}

/// <summary>
/// Renders a composition frame by frame with any backend. Node evaluation is synchronous; this host loop is
/// asynchronous, reporting progress after every frame and honoring cancellation between frames.
/// </summary>
public static class CompositionRenderer
{
    /// <summary>Renders <paramref name="frames"/> frames at <paramref name="framesPerSecond"/>.</summary>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static async Task<IReadOnlyList<RenderedFrame>> RenderAsync(
        ICompiledGraph graph,
        InstanceBuffer instances,
        InstanceHandle instance,
        ElementId output,
        int frames,
        double framesPerSecond,
        RenderSettings settings,
        IProgress<EvaluationProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var timeline = new Timeline();
        var context = new EvaluationContext { Cancellation = cancellation, Progress = progress }
            .Set(timeline)
            .Set(settings);
        List<RenderedFrame> rendered = [];
        for (var i = 0; i < frames; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            timeline.Seconds = i / framesPerSecond;
            await graph.EvaluateAsync(context, instances, instance);
            var frame = graph.ReadOutputReference<Frame>(context, instances, instance, output)!;
            rendered.Add(new(timeline.Seconds, frame.Mean, frame.Checksum()));
            context.Progress?.Report(new((i + 1) / (double)frames, $"frame {i + 1} of {frames}"));
            await Task.Yield();
        }
        return rendered;
    }
}
