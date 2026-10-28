namespace MASS4.Graph.Semantics.Tests;

public class DataflowTests
{
    [Test]
    public async Task PullEvaluatesUpstreamOnceAndCachesUntilInvalidated()
    {
        var evaluations = 0;
        var data = CreateDataflow();
        var counted = new NodeType(Id(150), "Counted", [Scale.Ports[0], Scale.Ports[1]]);
        data.AddNode(counted.Id, new DataflowNode(c =>
        {
            evaluations++;
            c.Write(1, c.Read<double>(0) * 2);
        }));
        var editor = CreateEditor(data);
        editor.Document.Registry.NodeTypes.Register(counted);
        var two = editor.AddNode(Constant, new ConstantPayload(2));
        var three = editor.AddNode(Constant, new ConstantPayload(3));
        var doubled = editor.AddNode(counted);
        var sum = editor.AddNode(Add);
        editor.Wire(two, 0, doubled, 0);
        editor.Wire(doubled, 1, sum, 0);
        editor.Wire(three, 0, sum, 1);
        var layout = new InstanceLayoutBuilder();
        var program = CompileData(data, editor.Document, layout);
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();
        var context = new EvaluationContext();

        var first = program.Read<double>(context, instances, instance, sum.Port(2));
        program.BeginEvaluation(instances, instance);
        var second = program.Read<double>(context, instances, instance, sum.Port(2));
        var cachedEvaluations = evaluations;
        program.Invalidate(instances, instance);
        program.Read<double>(context, instances, instance, sum.Port(2));

        await Assert.That(first).IsEqualTo(7);
        await Assert.That(second).IsEqualTo(7);
        await Assert.That(cachedEvaluations).IsEqualTo(1);
        await Assert.That(evaluations).IsEqualTo(2);
        await Assert.That(program.IsCached(sum.NodeId)).IsTrue();
        await Assert.That(program.Document).IsSameReferenceAs(editor.Document);
    }

    [Test]
    public async Task VolatileNodesRecomputeOncePerEvaluation()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var speed = editor.AddNode(Speed);
        var doubled = editor.AddNode(Scale);
        editor.Wire(speed, 0, doubled, 0);
        var layout = new InstanceLayoutBuilder();
        var program = CompileData(data, editor.Document, layout);
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();
        var locomotion = new Locomotion { Speed = 1 };
        var context = new EvaluationContext().Set(locomotion);

        var before = program.Read<double>(context, instances, instance, doubled.Port(1));
        locomotion.Speed = 4;
        var sameEvaluation = program.Read<double>(context, instances, instance, doubled.Port(1));
        program.BeginEvaluation(instances, instance);
        var next = program.Read<double>(context, instances, instance, doubled.Port(1));

        await Assert.That(before).IsEqualTo(2);
        await Assert.That(sameEvaluation).IsEqualTo(2);
        await Assert.That(next).IsEqualTo(8);
        await Assert.That(program.IsCached(doubled.NodeId)).IsFalse();
    }

    [Test]
    public async Task CoercionsConvertAndUnconnectedInputsReadDefaults()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var four = editor.AddNode(IntConstant, new IntPayload(4));
        var doubled = editor.AddNode(Scale);
        var idle = editor.AddNode(Scale);
        editor.Wire(four, 0, doubled, 0);
        var layout = new InstanceLayoutBuilder();
        var program = CompileData(data, editor.Document, layout);
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();
        var context = new EvaluationContext();

        await Assert.That(program.Read<double>(context, instances, instance, doubled.Port(1))).IsEqualTo(8);
        await Assert.That(program.Read<double>(context, instances, instance, doubled.Port(0))).IsEqualTo(4);
        await Assert.That(program.Read<double>(context, instances, instance, idle.Port(1))).IsEqualTo(0);
        await Assert.That(program.ResolvedType(doubled.Port(0))).IsEqualTo(Number.Id);
    }

    [Test]
    public async Task EditorInsertsConversionNodesAndReferenceValuesFlow()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var value = editor.AddNode(Constant, new ConstantPayload(1.5));
        var length = editor.AddNode(TextLength);

        var connected = editor.Link(value, 0, length, 0);
        var layout = new InstanceLayoutBuilder();
        var program = CompileData(data, editor.Document, layout);
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();
        var conversion = editor.Document.Nodes.Single(node => node.TypeId == NumberToText.Id);
        program.TryResolve(conversion.Ports[1].Id, out var text);

        await Assert.That(connected.Succeeded).IsTrue();
        await Assert.That(program.Read<double>(new(), instances, instance, length.Port(1))).IsEqualTo(3);
        await Assert.That(program.ReadReference<string>(new(), instances, instance, text)).IsEqualTo("1.5");
    }

    [Test]
    public async Task ExternalWritesFeedDownstreamNodes()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var probe = editor.AddNode(Probe);
        var doubled = editor.AddNode(Scale);
        var length = editor.AddNode(TextLength);
        editor.Wire(probe, 0, doubled, 0);
        editor.Wire(probe, 1, length, 0);
        var layout = new InstanceLayoutBuilder();
        var program = CompileData(data, editor.Document, layout);
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();
        IDataPlane plane = program;
        plane.TryResolve(probe.Port(0), out var number);
        plane.TryResolve(probe.Port(1), out var text);

        var initial = program.Read<double>(new(), instances, instance, doubled.Port(1));
        plane.Write(instances, instance, number, 5.0);
        plane.WriteReference(instances, instance, text, "abcd");

        await Assert.That(initial).IsEqualTo(0);
        await Assert.That(program.Read<double>(new(), instances, instance, doubled.Port(1))).IsEqualTo(10);
        await Assert.That(program.Read<double>(new(), instances, instance, length.Port(1))).IsEqualTo(4);
        await Assert.That(program.IsCached(doubled.NodeId)).IsFalse();
    }

    [Test]
    public async Task DataCyclesAreRejectedByEditorsAndCompilation()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var a = editor.AddNode(Scale);
        var b = editor.AddNode(Scale);
        editor.Wire(a, 1, b, 0);

        var rejected = editor.Link(b, 1, a, 0);
        var self = editor.Link(a, 1, a, 0);
        var permissive = new GraphEditor(editor.Document);
        permissive.Wire(b, 1, a, 0);
        var diagnostics = new DiagnosticBag();
        var program = data.Compile(editor.Document, new(), diagnostics);

        await Assert.That(rejected.Diagnostics.Single().Id).IsEqualTo(DataflowDiagnosticIds.DataCycle);
        await Assert.That(self.Diagnostics.Single().Id).IsEqualTo(DataflowDiagnosticIds.DataCycle);
        await Assert.That(program).IsNull();
        await Assert.That(diagnostics.Count(d => d.Id == DataflowDiagnosticIds.DataCycle)).IsEqualTo(1);
    }

    [Test]
    public async Task DataInputsAcceptOneEdge()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var one = editor.AddNode(Constant, new ConstantPayload(1));
        var two = editor.AddNode(Constant, new ConstantPayload(2));
        var collect = editor.AddNode(Collect);
        editor.Wire(one, 0, collect, 0);

        var second = editor.Link(two, 0, collect, 0);

        await Assert.That(second.Diagnostics.Single().Id).IsEqualTo(DataflowDiagnosticIds.MultipleDataEdges);
        await Assert.That(second.Diagnostics.Single().ElementId).IsEqualTo(collect.Port(0));
    }

    [Test]
    public async Task WildcardPortsInferTheirTypes()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var value = editor.AddNode(Constant, new ConstantPayload(3));
        var first = editor.AddNode(Relay);
        var second = editor.AddNode(Relay);
        var doubled = editor.AddNode(Scale);
        editor.Wire(value, 0, first, 0);
        editor.Wire(first, 1, second, 0);
        editor.Wire(second, 1, doubled, 0);
        var layout = new InstanceLayoutBuilder();
        var program = CompileData(data, editor.Document, layout);
        var instances = new InstanceBuffer(layout.Build());

        await Assert.That(GraphValidator.Validate(editor.Document, data.Rules).Count).IsEqualTo(0);
        await Assert.That(program.ResolvedType(first.Port(1))).IsEqualTo(Number.Id);
        await Assert.That(program.ResolvedType(second.Port(0))).IsEqualTo(Number.Id);
        await Assert.That(program.Read<double>(new(), instances, instances.Allocate(), doubled.Port(1))).IsEqualTo(6);
    }

    [Test]
    public async Task WildcardConflictsAreRejectedAndLoneWildcardsWarn()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var value = editor.AddNode(Constant, new ConstantPayload(3));
        var relay = editor.AddNode(Relay);
        var first = editor.AddNode(Relay);
        var second = editor.AddNode(Relay);
        var negate = editor.AddNode(Not);
        editor.Wire(value, 0, relay, 0);
        editor.Wire(first, 1, second, 0);

        var conflict = editor.Link(relay, 1, negate, 0);
        var warnings = GraphValidator.Validate(editor.Document, data.Rules);
        var compiled = data.Compile(editor.Document, new(), new());

        await Assert.That(conflict.Diagnostics.Single().Id).IsEqualTo(DataflowDiagnosticIds.TypeConflict);
        await Assert.That(warnings.Count(d => d.Id == DataflowDiagnosticIds.TypeUnresolved)).IsEqualTo(2);
        await Assert.That(compiled).IsNotNull();
    }

    [Test]
    public async Task MissingCoercionsFailCompilation()
    {
        var data = CreateDataflow();
        data.Conversions.AddImplicit(Int.Id, Text.Id);
        var editor = CreateEditor(data);
        var four = editor.AddNode(IntConstant, new IntPayload(4));
        var length = editor.AddNode(TextLength);
        editor.Wire(four, 0, length, 0);
        var diagnostics = new DiagnosticBag();

        var program = data.Compile(editor.Document, new(), diagnostics);

        await Assert.That(program).IsNull();
        await Assert.That(diagnostics.Single().Id).IsEqualTo(DataflowDiagnosticIds.CoercionMissing);
    }

    [Test]
    public async Task ReadsOfTheWrongTypeOrNonDataPortsThrow()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var value = editor.AddNode(Constant, new ConstantPayload(1));
        var tick = editor.AddNode(OnTick);
        var layout = new InstanceLayoutBuilder();
        var program = CompileData(data, editor.Document, layout);
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();
        program.TryResolve(value.Port(0), out var port);

        await Assert.That(() => program.Read<int>(new(), instances, instance, port))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => program.ReadReference<string>(new(), instances, instance, port))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => program.Read<double>(new(), instances, instance, tick.Port(0)))
            .ThrowsExactly<KeyNotFoundException>();
        await Assert.That(program.TryResolve(tick.Port(0), out _)).IsFalse();
        await Assert.That(program.ResolvedType(tick.Port(0))).IsNull();
        await Assert.That(program.ResolvedType(ElementId.New())).IsNull();
    }

    [Test]
    public async Task RegistrationRejectsDuplicatesAndMismatchedCoercions()
    {
        var data = CreateDataflow();

        await Assert.That(() => data.AddValueType<float>(Number.Id)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => data.AddNode(Constant.Id, new DataflowNode(static _ => { })))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(() => data.AddCoercion<float, double>(Int.Id, Number.Id, static v => v))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(data.TryGetValueType(Text.Id, out var text)).IsTrue();
        await Assert.That(text!.Storage).IsEqualTo(ValueStorage.Reference);
        await Assert.That(text.ClrType).IsEqualTo(typeof(string));
    }

    [Test]
    public async Task DefaultMembersConnectionsAndReferenceCopiesWork()
    {
        var data = CreateDataflow();
        var fallback = new NodeType(Id(152), "Fallback", [Scale.Ports[0], Scale.Ports[1]]);
        data.AddNode(fallback.Id, new OrDefault());
        var editor = CreateEditor(data);
        editor.Document.Registry.NodeTypes.Register(fallback);
        var lone = editor.AddNode(fallback);
        var value = editor.AddNode(Constant, new ConstantPayload(1.5));
        var text = editor.AddNode(NumberToText);
        var relay = editor.AddNode(Relay);
        var length = editor.AddNode(TextLength);
        editor.Wire(value, 0, text, 0);
        editor.Wire(text, 1, relay, 0);
        editor.Wire(relay, 1, length, 0);
        var layout = new InstanceLayoutBuilder();
        var program = CompileData(data, editor.Document, layout);
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();

        await Assert.That(program.Read<double>(new(), instances, instance, lone.Port(1))).IsEqualTo(42);
        await Assert.That(program.IsCached(lone.NodeId)).IsTrue();
        await Assert.That(program.Read<double>(new(), instances, instance, length.Port(1))).IsEqualTo(3);
        await Assert.That(program.ResolvedType(relay.Port(1))).IsEqualTo(Text.Id);
    }

    [Test]
    public async Task InferenceMergesBoundVariablesInEdgeOrder()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var first = editor.AddNode(Relay);
        var second = editor.AddNode(Relay);
        var doubled = editor.AddNode(Scale);

        editor.Execute(new ConnectCommand(second.Port(1), doubled.Port(0), EdgeId(1)));
        editor.Execute(new ConnectCommand(first.Port(1), second.Port(0), EdgeId(2)));
        var program = CompileData(data, editor.Document, new());

        await Assert.That(program.ResolvedType(first.Port(0))).IsEqualTo(Number.Id);
        await Assert.That(program.ResolvedType(second.Port(1))).IsEqualTo(Number.Id);
    }

    [Test]
    public async Task EpochCountersWrapByForgettingCachedValues()
    {
        var evaluations = 0;
        var data = CreateDataflow();
        var counted = new NodeType(Id(153), "Counted speed", [Speed.Ports[0]]);
        data.AddNode(counted.Id, new DataflowNode(c => c.Write(0, (double)++evaluations)));
        var editor = CreateEditor(data);
        editor.Document.Registry.NodeTypes.Register(counted);
        var node = editor.AddNode(counted);
        var layout = new InstanceLayoutBuilder();
        var program = CompileData(data, editor.Document, layout);
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();

        program.Read<double>(new(), instances, instance, node.Port(0));
        instances.Get(instance, program.EpochCounter) = int.MaxValue - 1;
        program.BeginEvaluation(instances, instance);

        await Assert.That(instances.Get(instance, program.EpochCounter)).IsEqualTo(0);
        await Assert.That(program.Read<double>(new(), instances, instance, node.Port(0))).IsEqualTo(2);
    }

    [Test]
    public async Task CopyRejectsDifferentTypes()
    {
        var data = CreateDataflow();
        var mixed = new NodeType(Id(151), "Mixed", [Constant.Ports[0], Not.Ports[1]]);
        data.AddNode(mixed.Id, new DataflowNode(static c => c.Copy(0, 1)));
        var editor = CreateEditor(data);
        editor.Document.Registry.NodeTypes.Register(mixed);
        var node = editor.AddNode(mixed);
        var layout = new InstanceLayoutBuilder();
        var program = CompileData(data, editor.Document, layout);
        var instances = new InstanceBuffer(layout.Build());

        await Assert.That(() => program.Read<bool>(new(), instances, instances.Allocate(), node.Port(1)))
            .ThrowsExactly<InvalidOperationException>();
    }

    static ElementId EdgeId(int order) => new(new Guid($"00000000-0000-4000-8000-{order:D12}"));

    /// <summary>Passes its input through, or 42 when nothing is connected; relies on the default volatility.</summary>
    sealed class OrDefault : IDataflowNode
    {
        public void Evaluate(DataflowContext context) =>
            context.Write(1, context.IsConnected(0) ? context.Read<double>(0) : 42);
    }
}
