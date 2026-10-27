namespace MASS4.Graph.Interpreter;

/// <summary>
/// The always-available reference backend: it compiles a document with every semantics it participates in and
/// evaluates the resulting programs directly. It serves editor live edit and acts as the oracle generated backends
/// are compared against.
/// </summary>
/// <param name="dataflow">Dataflow semantics; also the data plane of control and state nodes.</param>
/// <param name="control">Control semantics, or null when documents contain no control flow.</param>
/// <param name="state">State semantics, or null when documents contain no state machines.</param>
public sealed class GraphInterpreter(
    DataflowSemantics dataflow,
    ControlSemantics? control = null,
    StateSemantics? state = null) : IGraphBackend
{
    /// <inheritdoc />
    public string Name => "interpreter";

    /// <inheritdoc />
    public ValueTask<CompileResult> CompileAsync(
        GraphSource source,
        IProgress<EvaluationProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var result = Compile(source.Document);
        progress?.Report(new(1, "compiled"));
        return ValueTask.FromResult(result);
    }

    /// <summary>Compiles a document synchronously; the graph is null when any semantics reports errors.</summary>
    public CompileResult Compile(GraphDocument document)
    {
        var diagnostics = new DiagnosticBag();
        var layout = new InstanceLayoutBuilder();
        var data = dataflow.Compile(document, layout, diagnostics);
        var machine = state is not null && state.Participates(document)
            ? state.Compile(document, layout, data, diagnostics)
            : null;
        var flow = control is not null && control.Participates(document)
            ? control.Compile(document, layout, data, diagnostics)
            : null;
        var graph = diagnostics.HasErrors || data is null
            ? null
            : new InterpretedGraph(document, layout.Build(), data, machine, flow);
        return new(graph, [.. diagnostics]);
    }
}
