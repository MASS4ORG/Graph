namespace MASS4.Graph.Interpreter.Tests;

public class InterpreterTests
{
    [Test]
    public async Task CompilesTheMixedDocumentAndEvaluatesThroughTheContract()
    {
        var mixed = BuildMixed();
        var interpreter = new GraphInterpreter(mixed.Data, mixed.Control, mixed.State);
        List<EvaluationProgress> progress = [];
        var result = await interpreter.CompileAsync(new(mixed.Editor.Document), new Progress(progress.Add));
        var graph = (InterpretedGraph)result.Graph!;
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var instance = instances.Allocate();
        var context = new EvaluationContext()
            .Set(new Locomotion { Speed = 1 })
            .Set(new StateTime { DeltaSeconds = 0.5 });

        graph.Initialize(context, instances, instance);
        await graph.EvaluateAsync(context, instances, instance);
        await graph.EvaluateAsync(context, instances, instance);
        var values = Observe(graph, context, instances, instance);

        await Assert.That(values[mixed.Stride.Port(1)]).IsEqualTo(1.0);
        await Assert.That(values[mixed.Total.Port(2)]).IsEqualTo(2.5);
        await Assert.That(values[graph.StateMachine!.EntryNodeId]).IsEqualTo(mixed.Walk.NodeId);
        await Assert.That(graph.Raise(instances, instance, Stop.Id)).IsTrue();
        await Assert.That(graph.Raise(instances, instance, Id(499))).IsFalse();
        await Assert.That(graph.SetVariable(instances, instance, ElementId.New(), 1.0)).IsFalse();
        await Assert.That(graph.AssetId).IsEqualTo(mixed.Editor.Document.AssetId);
        await Assert.That(graph.Control).IsNotNull();
        await Assert.That(progress.Single().Fraction).IsEqualTo(1);
    }

    [Test]
    public async Task ReportsCompileErrorsFromEverySemantics()
    {
        var mixed = BuildMixed();
        var permissive = new GraphEditor(mixed.Editor.Document);
        permissive.AddNode(StateNodes.Entry);
        var x = permissive.AddNode(Scale);
        var y = permissive.AddNode(Scale);
        permissive.Wire(x, 1, y, 0);
        permissive.Wire(y, 1, x, 0);

        var result = new GraphInterpreter(mixed.Data, mixed.Control, mixed.State).Compile(mixed.Editor.Document);

        await Assert.That(result.Graph).IsNull();
        await Assert.That(result.Diagnostics.Select(d => d.Id)).Contains(DataflowDiagnosticIds.DataCycle);
        await Assert.That(result.Diagnostics.Select(d => d.Id)).Contains(StateDiagnosticIds.EntryCount);
    }

    [Test]
    public async Task DocumentsWithoutStateOrControlEvaluateDataflowOnly()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var value = editor.AddNode(Constant, new ConstantPayload(2));
        var doubled = editor.AddNode(Scale);
        editor.Wire(value, 0, doubled, 0);
        var graph = (InterpretedGraph)new GraphInterpreter(data, CreateControl(), CreateState())
            .Compile(editor.Document).Graph!;
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var instance = instances.Allocate();
        var context = new EvaluationContext();

        graph.Initialize(context, instances, instance);
        await graph.EvaluateAsync(context, instances, instance);

        await Assert.That(graph.StateMachine).IsNull();
        await Assert.That(graph.Control).IsNull();
        await Assert.That(graph.Raise(instances, instance, Stop.Id)).IsFalse();
        await Assert.That(Observe(graph, context, instances, instance)[doubled.Port(1)]).IsEqualTo(4.0);
    }

    [Test]
    public async Task RunawayControlFlowAndCancellationFailEvaluation()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var tick = editor.AddNode(OnTick);
        var loop = editor.AddNode(Forever);
        editor.Wire(tick, 0, loop, 0);
        editor.Wire(loop, 1, loop, 0);
        var interpreter = new GraphInterpreter(data, CreateControl(new() { MaxStepsPerRun = 10 }));
        var graph = interpreter.Compile(editor.Document).Graph!;
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var instance = instances.Allocate();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.That(async () => await graph.EvaluateAsync(new(), instances, instance))
            .ThrowsExactly<InvalidOperationException>();
        var stopping = new EvaluationContext { Cancellation = cancelled.Token };
        await Assert.That(async () => await graph.EvaluateAsync(stopping, instances, instance))
            .ThrowsExactly<OperationCanceledException>();
        await Assert.That(async () => await interpreter.CompileAsync(new(editor.Document), null, cancelled.Token))
            .ThrowsExactly<OperationCanceledException>();
    }

    [Test]
    public async Task SessionsRecompileAfterEditsAndKeepTheLastGoodGraph()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var value = editor.AddNode(Constant, new ConstantPayload(2));
        var doubled = editor.AddNode(Scale);
        editor.Wire(value, 0, doubled, 0);
        using var session = new InterpreterSession(new GraphInterpreter(data), editor.Document, instanceCount: 2);
        var context = new EvaluationContext();

        await session.EvaluateAsync(context);
        var first = Read(session, doubled);
        editor.Execute(new SetPayloadCommand(value.NodeId, new ConstantPayload(5)));
        await session.EvaluateAsync(context);
        var edited = Read(session, doubled);
        var permissive = new GraphEditor(editor.Document);
        var x = permissive.AddNode(Scale);
        var y = permissive.AddNode(Scale);
        permissive.Wire(x, 1, y, 0);
        permissive.Wire(y, 1, x, 0);
        var broken = session.Refresh(context);
        var unchanged = session.Refresh(context);

        await Assert.That(first).IsEqualTo(4);
        await Assert.That(edited).IsEqualTo(10);
        await Assert.That(broken).IsFalse();
        await Assert.That(unchanged).IsFalse();
        await Assert.That(session.Revision).IsEqualTo(2);
        await Assert.That(session.Handles.Count).IsEqualTo(2);
        await Assert.That(session.Diagnostics.Select(d => d.Id)).Contains(DataflowDiagnosticIds.DataCycle);
        await Assert.That(Read(session, doubled)).IsEqualTo(10);
        await Assert.That(session.Document).IsSameReferenceAs(editor.Document);
        await Assert.That(() => new InterpreterSession(new GraphInterpreter(data), editor.Document, 0))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task DisposedSessionsStopWatchingAndBrokenFirstCompilesRunNothing()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var permissive = new GraphEditor(editor.Document);
        var x = permissive.AddNode(Scale);
        var y = permissive.AddNode(Scale);
        permissive.Wire(x, 1, y, 0);
        permissive.Wire(y, 1, x, 0);
        var session = new InterpreterSession(new GraphInterpreter(data), editor.Document);

        await session.EvaluateAsync(new());
        session.Dispose();
        editor.Execute(new RemoveNodeCommand(x.NodeId));

        await Assert.That(session.Graph).IsNull();
        await Assert.That(session.Instances).IsNull();
        await Assert.That(session.Refresh(new())).IsFalse();
    }

    [Test]
    public async Task ThousandInstancesEvaluateThroughTheContractWithoutAllocating()
    {
        var mixed = BuildMixed();
        var graph = new GraphInterpreter(mixed.Data, mixed.Control, mixed.State)
            .Compile(mixed.Editor.Document).Graph!;
        var instances = new InstanceBuffer(graph.InstanceLayout, 1000);
        var handles = new InstanceHandle[1000];
        var context = new EvaluationContext()
            .Set(new Locomotion { Speed = 1 })
            .Set(new StateTime { DeltaSeconds = 1.0 / 60 });
        for (var i = 0; i < handles.Length; i++)
        {
            handles[i] = instances.Allocate();
            graph.Initialize(context, instances, handles[i]);
        }

        Frame(graph, context, instances, handles, 0);
        Frame(graph, context, instances, handles, 1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 2; frame < 12; frame++)
        {
            Frame(graph, context, instances, handles, frame);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
    }

    static void Frame(
        ICompiledGraph graph,
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle[] handles,
        int frame)
    {
        foreach (var handle in handles)
        {
            if (frame % 2 == 0)
            {
                graph.Raise(instances, handle, Stop.Id);
            }
            var evaluation = graph.EvaluateAsync(context, instances, handle);
            if (!evaluation.IsCompleted)
            {
                throw new InvalidOperationException("The interpreter must complete synchronously.");
            }
            evaluation.GetAwaiter().GetResult();
        }
    }

    static double Read(InterpreterSession session, AddNodeCommand node) =>
        session.Graph!.Data.Read<double>(new(), session.Instances!, session.Handles[0], node.Port(1));

    static Dictionary<ElementId, object?> Observe(
        ICompiledGraph graph,
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle instance)
    {
        List<GraphObservation> observations = [];
        graph.Observe(context, instances, instance, observations);
        return observations.ToDictionary(static o => o.Element, static o => o.Value);
    }

    sealed class Progress(Action<EvaluationProgress> report) : IProgress<EvaluationProgress>
    {
        public void Report(EvaluationProgress value) => report(value);
    }
}
