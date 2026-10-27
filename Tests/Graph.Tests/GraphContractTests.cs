namespace MASS4.Graph.Tests;

public class GraphContractTests
{
    [Test]
    public async Task LargeSparseGraphBuildsValidatesSavesAndUndoes()
    {
        const int count = 5000;
        var editor = CreateEditor();
        var previous = editor.AddNode(Constant, new ConstantPayload(0));
        List<IGraphCommand> steps = [];
        var previousOutput = previous.PortId(ValueKey);
        for (var i = 0; i < count; i++)
        {
            var add = new AddNodeCommand(Add);
            steps.Add(add);
            steps.Add(new ConnectCommand(previousOutput, add.PortId(AKey)));
            previousOutput = add.PortId(SumKey);
        }

        var result = editor.Execute(new CompositeCommand("Build chain", steps));
        var diagnostics = GraphValidator.Validate(editor.Document, editor.Rules);
        var json = GraphJson.Serialize(editor.Document);
        var loaded = GraphJson.Deserialize(json, CreateRegistry());

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(diagnostics.Count).IsEqualTo(0);
        await Assert.That(editor.Document.Nodes.Count).IsEqualTo(count + 1);
        await Assert.That(editor.Document.Edges.Count).IsEqualTo(count);
        await Assert.That(loaded.Document!.Edges.Count).IsEqualTo(count);

        editor.Undo();
        await Assert.That(editor.Document.Nodes.Count).IsEqualTo(1);
        await Assert.That(editor.Document.Edges.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GraphValidatorReportsEveryElementOfLoadedDocuments()
    {
        var editor = CreateEditor();
        var constant = editor.AddNode(Constant);
        var relay = editor.AddNode(Relay);
        editor.Connect(constant.PortId(ValueKey), relay.PortId(InKey));
        var registry = new GraphTypeRegistry();
        registry.PortTypes.Register(Number);
        registry.NodeTypes.Register(Constant);

        var loaded = GraphJson.Deserialize(GraphJson.Serialize(editor.Document), registry).Document!;
        IGraphRule[] rules = [KnownTypesRule.Instance, new PortCompatibilityRule(ExactPortCompatibility.Instance)];
        var diagnostics = GraphValidator.Validate(loaded, rules);

        await Assert.That(diagnostics.Select(d => d.Id).ToArray()).IsEquivalentTo(
            [GraphDiagnosticIds.UnknownNodeType, GraphDiagnosticIds.UnknownPortType]);
        await Assert.That(diagnostics.All(d => d.Severity == DiagnosticSeverity.Warning)).IsTrue();
    }

    [Test]
    public async Task SubgraphExposedVariablesBecomePorts()
    {
        var subgraphEditor = CreateEditor();
        var gain = new GraphVariable(ElementId.New(), "gain", NumberId, VariableScope.Input);
        var label = new GraphVariable(ElementId.New(), "label", TextId, VariableScope.Output);
        subgraphEditor.Execute(new SetVariableCommand(gain));
        subgraphEditor.Execute(new SetVariableCommand(label));
        subgraphEditor.Execute(new SetVariableCommand(new(ElementId.New(), "scratch", NumberId, VariableScope.Local)));

        var nodeType = Subgraphs.CreateNodeType(subgraphEditor.Document, "Labeler");
        var registry = CreateRegistry();
        registry.NodeTypes.Register(nodeType);
        var host = new GraphEditor(new(registry));
        var constant = host.AddNode(Constant);
        var call = host.AddNode(nodeType);
        var print = host.AddNode(Print);

        await Assert.That(nodeType.Id).IsEqualTo(subgraphEditor.Document.AssetId);
        await Assert.That(nodeType.Ports.Count).IsEqualTo(2);
        await Assert.That(nodeType.TryGetPort(new(gain.Id.Value), out var input)).IsTrue();
        await Assert.That(input!.Direction).IsEqualTo(PortDirection.Input);
        await Assert.That(host.Connect(constant.PortId(ValueKey), call.PortId(new(gain.Id.Value))).Succeeded).IsTrue();
        await Assert.That(host.Connect(call.PortId(new(label.Id.Value)), print.PortId(InKey)).Succeeded).IsTrue();
    }

    [Test]
    public async Task EvaluationContextExposesTypedFeaturesAndProgress()
    {
        var clock = new Clock(2.5);
        List<EvaluationProgress> reports = [];
        var context = new EvaluationContext
        {
            Progress = new SynchronousProgress(reports.Add),
            HostFunctions = CreateRegistry().HostFunctions,
        }.Set(clock);
        context.Progress!.Report(new(0.5, "half"));

        await Assert.That(context.TryGet<Clock>(out var found)).IsTrue();
        await Assert.That(found).IsSameReferenceAs(clock);
        await Assert.That(context.TryGet<string>(out _)).IsFalse();
        await Assert.That(reports.Single()).IsEqualTo(new EvaluationProgress(0.5, "half"));
        await Assert.That(context.Cancellation.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task BackendCompilesThroughTheContract()
    {
        var editor = CreateEditor();
        editor.AddNode(Constant);
        IGraphBackend backend = new CountingBackend();

        var result = await backend.CompileAsync(new(editor.Document));

        await Assert.That(result.Graph).IsNull();
        await Assert.That(result.Diagnostics.Single().Message).IsEqualTo("1 nodes");
        await Assert.That(backend.Name).IsEqualTo("counting");
    }

    [Test]
    public async Task NodeTypesSupplyDefaultPayloadsToNewNodesOnly()
    {
        var withDefault = new NodeType(Id(610), "Defaulted", [])
        {
            DefaultPayload = static () => new ConstantPayload(7),
        };

        var defaulted = new AddNodeCommand(withDefault);
        var explicitPayload = new AddNodeCommand(withDefault, new ConstantPayload(1));
        var plain = new AddNodeCommand(Add);

        await Assert.That(defaulted.Payload).IsEqualTo(new ConstantPayload(7));
        await Assert.That(explicitPayload.Payload).IsEqualTo(new ConstantPayload(1));
        await Assert.That(plain.Payload).IsNull();
    }

    [Test]
    public async Task RequiredFeaturesFailLoudlyWhenMissing()
    {
        var context = new EvaluationContext().Set(new Clock(1));
        Type[] required = [typeof(Clock), typeof(SynchronousProgress)];
        MissingFeatureException? missing = null;

        try
        {
            context.Require(required);
        }
        catch (MissingFeatureException exception)
        {
            missing = exception;
        }

        await Assert.That(context.Get<Clock>().Seconds).IsEqualTo(1);
        await Assert.That(context.Contains(typeof(Clock))).IsTrue();
        await Assert.That(missing!.Feature).IsEqualTo(typeof(SynchronousProgress));
        await Assert.That(missing.Message).Contains(nameof(SynchronousProgress));
        await Assert.That(() => context.Get<SynchronousProgress>()).ThrowsExactly<MissingFeatureException>();
    }

    [Test]
    public async Task SignalsAreRenameAwareRegistryIdentities()
    {
        var registry = new GraphTypeRegistry();
        var jump = new SignalType(Id(620), "Jump");
        registry.Signals.Register(jump);
        registry.Signals.AddRename(Id(619), jump.Id);

        await Assert.That(registry.Signals.Resolve(Id(619))).IsSameReferenceAs(jump);
        await Assert.That(jump.DisplayName).IsEqualTo("Jump");
    }

    [Test]
    public async Task NodeGetPortThrowsForUnknownKey()
    {
        var editor = CreateEditor();
        editor.AddNode(Constant);
        var node = editor.Document.Nodes.Single();

        await Assert.That(editor.Document.GetNode(node.Id)).IsSameReferenceAs(node);
        await Assert.That(() => editor.Document.GetNode(ElementId.New())).ThrowsExactly<KeyNotFoundException>();
        await Assert.That(editor.Document.GetPort(node.Ports[0].Id)).IsSameReferenceAs(node.Ports[0]);
        await Assert.That(() => editor.Document.GetPort(ElementId.New())).ThrowsExactly<KeyNotFoundException>();
        await Assert.That(node.TryGetPort(SumKey, out _)).IsFalse();
        await Assert.That(() => node.GetPort(SumKey)).ThrowsExactly<KeyNotFoundException>();
        await Assert.That(node.GetPort(ValueKey).TypeId).IsEqualTo(NumberId);
        await Assert.That(editor.Document.TryGetNodeType(node, out var type)).IsTrue();
        await Assert.That(type).IsSameReferenceAs(Constant);
    }

    sealed record Clock(double Seconds);

    sealed class SynchronousProgress(Action<EvaluationProgress> report) : IProgress<EvaluationProgress>
    {
        public void Report(EvaluationProgress value) => report(value);
    }

    sealed class CountingBackend : IGraphBackend
    {
        public string Name => "counting";

        public ValueTask<CompileResult> CompileAsync(
            GraphSource source,
            IProgress<EvaluationProgress>? progress = null,
            CancellationToken cancellation = default) =>
            ValueTask.FromResult(new CompileResult(null,
                [new("TEST02", DiagnosticSeverity.Info, null, $"{source.Document.Nodes.Count} nodes")]));
    }
}
