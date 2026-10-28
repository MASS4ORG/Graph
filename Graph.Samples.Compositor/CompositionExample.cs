using MASS4.Graph.Dataflow;
using MASS4.Graph.Interpreter;

namespace MASS4.Graph.Samples.Compositor;

/// <summary>
/// An example composition: a pulse fades in over a scrolling ramp, out = over(pulse, ramp, envelope(time)). Every
/// value depends on the timeline, so the composition is reproducible for a given time.
/// </summary>
/// <param name="Document">The composition.</param>
/// <param name="Output">The frame input of the output node; renderers read it.</param>
/// <param name="Blend">The blend node.</param>
public sealed record CompositionExample(GraphDocument Document, ElementId Output, ElementId Blend)
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
        var time = new AddNodeCommand(CompositorLibrary.Time);
        var ramp = new AddNodeCommand(CompositorLibrary.Ramp, new RampPayload(Speed));
        var pulse = new AddNodeCommand(CompositorLibrary.Pulse, new PulsePayload(Frequency));
        var envelope = new AddNodeCommand(CompositorLibrary.Envelope, new EnvelopePayload(FadeIn));
        var blend = new AddNodeCommand(CompositorLibrary.Over);
        var output = new AddNodeCommand(CompositorLibrary.Output);
        Require(editor.Execute(new CompositeCommand("Fade a pulse over a ramp",
        [
            time, ramp, pulse, envelope, blend, output,
            Wire(time, envelope, CompositorLibrary.AKey),
            Wire(pulse, blend, CompositorLibrary.AKey),
            Wire(ramp, blend, CompositorLibrary.BKey),
            Wire(envelope, blend, CompositorLibrary.MixKey),
            Wire(blend, output, CompositorLibrary.AKey),
        ])));
        return new(editor.Document, output.PortId(CompositorLibrary.AKey), blend.NodeId);
    }

    /// <summary>The luminance the composition produces at a pixel column and time.</summary>
    public static float Expected(int x, int width, double seconds) =>
        Compositing.Over(Compositing.Pulse(Frequency, seconds), Compositing.Ramp(x, width, Speed, seconds),
            Compositing.Envelope(seconds, FadeIn));

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
/// Renders a composition frame by frame. Node evaluation is synchronous; this host loop is asynchronous, reporting
/// progress after every frame and honoring cancellation between frames.
/// </summary>
public static class CompositionRenderer
{
    /// <summary>Renders <paramref name="frames"/> frames at <paramref name="framesPerSecond"/>.</summary>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static async Task<IReadOnlyList<RenderedFrame>> RenderAsync(
        InterpretedGraph graph,
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
        graph.Data.TryResolve(output, out var port);
        List<RenderedFrame> rendered = [];
        for (var i = 0; i < frames; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            timeline.Seconds = i / framesPerSecond;
            await graph.EvaluateAsync(context, instances, instance);
            var frame = graph.Data.ReadReference<Frame>(context, instances, instance, port)!;
            rendered.Add(new(timeline.Seconds, frame.Mean, frame.Checksum()));
            context.Progress?.Report(new((i + 1) / (double)frames, $"frame {i + 1} of {frames}"));
            await Task.Yield();
        }
        return rendered;
    }
}
