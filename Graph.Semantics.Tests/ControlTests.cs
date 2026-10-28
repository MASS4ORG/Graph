namespace MASS4.Graph.Semantics.Tests;

public class ControlTests
{
    [Test]
    public async Task TicksRunDepthFirstInRequestOrderAndFanOutByNodeIdentity()
    {
        var editor = CreateEditor(CreateDataflow());
        var tick = editor.AddNode(OnTick);
        var outer = editor.AddNode(Sequence);
        var inner = editor.AddNode(Sequence);
        var a1 = editor.AddNode(Log, new LogPayload("a1"));
        var a2 = editor.AddNode(Log, new LogPayload("a2"));
        var b = editor.AddNode(Log, new LogPayload("b"));
        var c = editor.AddNode(Log, new LogPayload("c"));
        editor.Wire(tick, 0, outer, 0);
        editor.Wire(outer, 1, inner, 0);
        editor.Wire(inner, 1, a1, 0);
        editor.Wire(inner, 2, a2, 0);
        editor.Wire(outer, 2, b, 0);
        editor.Wire(outer, 2, c, 0);
        var layout = new InstanceLayoutBuilder();
        var program = CreateControl().Compile(editor.Document, layout, null, new());
        var instances = new InstanceBuffer(layout.Build());
        var journal = new Journal();
        var fanOut = b.NodeId.CompareTo(c.NodeId) < 0 ? "b,c" : "c,b";

        var result = program.Tick(new EvaluationContext().Set(journal), instances, instances.Allocate());

        await Assert.That(result).IsEqualTo(ControlRunResult.Completed);
        await Assert.That(string.Join(",", journal.Entries)).IsEqualTo($"a1,a2,{fanOut}");
    }

    [Test]
    public async Task ControlNodesReadGuardsAndPublishMemoryThroughDataflow()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var tick = editor.AddNode(OnTick);
        var branch = editor.AddNode(Branch);
        var counter = editor.AddNode(Counter);
        var speed = editor.AddNode(Speed);
        var threshold = editor.AddNode(Constant, new ConstantPayload(0.5));
        var faster = editor.AddNode(GreaterThan);
        var doubled = editor.AddNode(Scale);
        editor.Wire(tick, 0, branch, 0);
        editor.Wire(branch, 2, counter, 0);
        editor.Wire(speed, 0, faster, 0);
        editor.Wire(threshold, 0, faster, 1);
        editor.Wire(faster, 2, branch, 1);
        editor.Wire(counter, 2, doubled, 0);
        var layout = new InstanceLayoutBuilder();
        var plane = CompileData(data, editor.Document, layout);
        var program = CreateControl().Compile(editor.Document, layout, plane, new());
        var instances = new InstanceBuffer(layout.Build());
        var moving = instances.Allocate();
        var resting = instances.Allocate();
        var fast = new EvaluationContext().Set(new Locomotion { Speed = 1 });
        var still = new EvaluationContext().Set(new Locomotion { Speed = 0 });

        for (var i = 0; i < 3; i++)
        {
            program.Tick(fast, instances, moving);
            program.Tick(still, instances, resting);
        }

        await Assert.That(plane.Read<double>(fast, instances, moving, doubled.Port(1))).IsEqualTo(6);
        await Assert.That(plane.Read<double>(still, instances, resting, doubled.Port(1))).IsEqualTo(0);
    }

    [Test]
    public async Task RunsStopAtTheStepLimitAndStackCapacity()
    {
        var editor = CreateEditor(CreateDataflow());
        var tick = editor.AddNode(OnTick);
        var loop = editor.AddNode(Forever);
        var fan = editor.AddNode(Sequence);
        editor.Wire(tick, 0, loop, 0);
        editor.Wire(loop, 1, loop, 0);
        for (var i = 0; i < 3; i++)
        {
            editor.Wire(fan, 1, editor.AddNode(Log, new LogPayload("x")), 0);
        }
        var layout = new InstanceLayoutBuilder();
        var limited = CreateControl(new() { MaxStepsPerRun = 50 }).Compile(editor.Document, layout, null, new());
        var shallow = CreateControl(new() { StackCapacity = 2 }).Compile(editor.Document, layout, null, new());
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();

        await Assert.That(limited.Tick(new(), instances, instance)).IsEqualTo(ControlRunResult.StepLimitExceeded);
        await Assert.That(shallow.Run(new(), instances, instance, fan.NodeId))
            .IsEqualTo(ControlRunResult.StackOverflow);
        await Assert.That(() => limited.Run(new(), instances, instance, ElementId.New()))
            .ThrowsExactly<KeyNotFoundException>();
    }

    [Test]
    public async Task RunsSkipNodesWithoutBehaviorAndEditorsWarnAboutThem()
    {
        var orphan = new NodeType(Id(250), "Orphan", [ControlSemantics.ExecInput(Id(251))]);
        var pass = new NodeType(Id(252), "Pass",
            [ControlSemantics.ExecInput(Id(253)), ControlSemantics.ExecOutput(Id(254))]);
        var control = CreateControl().AddNode(pass.Id, new Passthrough());
        var editor = CreateEditor(CreateDataflow(), control.Rules);
        editor.Document.Registry.NodeTypes.Register(orphan);
        editor.Document.Registry.NodeTypes.Register(pass);
        var tick = editor.AddNode(OnTick);
        var through = editor.AddNode(pass);
        var add = new AddNodeCommand(orphan);
        var added = editor.Execute(add);
        editor.Wire(tick, 0, through, 0);
        editor.Wire(through, 1, add, 0);
        var layout = new InstanceLayoutBuilder();
        var diagnostics = new DiagnosticBag();
        var program = control.Compile(editor.Document, layout, null, diagnostics);
        var instances = new InstanceBuffer(layout.Build());

        await Assert.That(added.Diagnostics.Single().Id).IsEqualTo(ControlDiagnosticIds.UnboundExecNode);
        await Assert.That(diagnostics.Single().Id).IsEqualTo(ControlDiagnosticIds.UnboundExecNode);
        await Assert.That(program.Tick(new(), instances, instances.Allocate())).IsEqualTo(ControlRunResult.Completed);
    }

    [Test]
    public async Task MisbehavingNodesThrow()
    {
        var wrongPort = new NodeType(Id(260), "Wrong port", [ControlSemantics.ExecInput(Id(261))]);
        var hungry = new NodeType(Id(262), "Hungry", [ControlSemantics.ExecInput(Id(263))]);
        var control = CreateControl(new() { MaxContinuations = 1 })
            .AddNode(wrongPort.Id, new ControlNode(static (ref c) => c.Continue(0)))
            .AddNode(hungry.Id, new ControlNode(static (ref c) => c.Memory<long>() = 1, memorySize: 1));
        var editor = CreateEditor(CreateDataflow());
        editor.Document.Registry.NodeTypes.Register(wrongPort);
        editor.Document.Registry.NodeTypes.Register(hungry);
        var sequence = editor.AddNode(Sequence);
        var wrong = editor.AddNode(wrongPort);
        var greedy = editor.AddNode(hungry);
        var branch = editor.AddNode(Branch);
        var layout = new InstanceLayoutBuilder();
        var program = control.Compile(editor.Document, layout, null, new());
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();

        foreach (var node in new[] { sequence, wrong, greedy, branch })
        {
            await Assert.That(() => program.Run(new(), instances, instance, node.NodeId))
                .ThrowsExactly<InvalidOperationException>();
        }
        await Assert.That(() => control.AddNode(OnTick.Id, new ControlNode(static (ref _) => { })))
            .ThrowsExactly<ArgumentException>();
    }

    /// <summary>Continues its only exec output; relies on the default entry and memory members.</summary>
    sealed class Passthrough : IControlNode
    {
        public void Execute(ref ControlContext context) => context.Continue(1);
    }
}
