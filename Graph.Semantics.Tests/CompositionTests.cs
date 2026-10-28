namespace MASS4.Graph.Semantics.Tests;

public class CompositionTests
{
    [Test]
    public async Task OneDocumentMixesStateDataflowAndControl()
    {
        var data = CreateDataflow();
        var state = CreateState();
        var control = CreateControl();
        var editor = CreateEditor(data, state.Rules, control.Rules);
        var entry = editor.AddNode(StateNodes.Entry);
        var idle = editor.AddNode(StateNodes.State);
        var walk = editor.AddNode(Walk);
        var start = editor.AddNode(Guarded, new TransitionPayload());
        var stop = editor.AddNode(StateNodes.Transition, new TransitionPayload(Stop.Id));
        var speed = editor.AddNode(Speed);
        var threshold = editor.AddNode(Constant, new ConstantPayload(0.5));
        var faster = editor.AddNode(GreaterThan);
        var stride = editor.AddNode(Scale);
        var total = editor.AddNode(Add);
        var tick = editor.AddNode(OnTick);
        var steps = editor.AddNode(Counter);
        editor.Wire(entry, 0, idle, 0);
        editor.Wire(idle, 1, start, 0);
        editor.Wire(start, 1, walk, 0);
        editor.Wire(walk, 1, stop, 0);
        editor.Wire(stop, 1, idle, 0);
        editor.Wire(speed, 0, faster, 0);
        editor.Wire(threshold, 0, faster, 1);
        editor.Wire(faster, 2, start, 2);
        editor.Wire(walk, 2, stride, 0);
        editor.Wire(walk, 2, total, 0);
        editor.Wire(steps, 2, total, 1);
        editor.Wire(tick, 0, steps, 0);
        var layout = new InstanceLayoutBuilder();
        var diagnostics = new DiagnosticBag();
        var plane = data.Compile(editor.Document, layout, diagnostics)!;
        var machine = state.Compile(editor.Document, layout, plane, diagnostics)!;
        var flow = control.Compile(editor.Document, layout, plane, diagnostics);
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();
        var locomotion = new Locomotion();
        var context = new EvaluationContext().Set(locomotion);

        machine.Tick(context, instances, instance, 0.1);
        var resting = machine.CurrentState(instances, instance).Id;
        locomotion.Speed = 1;
        machine.Tick(context, instances, instance, 0.1);
        machine.Tick(context, instances, instance, 0.5);
        flow.Tick(context, instances, instance);
        flow.Tick(context, instances, instance);
        var strideLength = plane.Read<double>(context, instances, instance, stride.Port(1));
        var sum = plane.Read<double>(context, instances, instance, total.Port(2));
        machine.Raise(instances, instance, machine.ResolveTrigger(Stop.Id));
        machine.Tick(context, instances, instance, 0.1);
        var stopped = machine.CurrentState(instances, instance).Id;
        machine.Tick(context, instances, instance, 0.1);

        await Assert.That(diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)).IsEqualTo(0);
        await Assert.That(resting).IsEqualTo(idle.NodeId);
        await Assert.That(strideLength).IsEqualTo(1);
        await Assert.That(sum).IsEqualTo(2.5);
        await Assert.That(stopped).IsEqualTo(idle.NodeId);
        await Assert.That(machine.CurrentState(instances, instance).Id).IsEqualTo(walk.NodeId);
    }

    [Test]
    public async Task StateCyclesAreLegalWhileDataCyclesAreRejectedInTheSameDocument()
    {
        var data = CreateDataflow();
        var state = CreateState();
        var editor = CreateEditor(data, state.Rules);
        var entry = editor.AddNode(StateNodes.Entry);
        var a = editor.AddNode(StateNodes.State);
        var b = editor.AddNode(StateNodes.State);
        var there = editor.AddNode(StateNodes.Transition, new TransitionPayload(Go.Id));
        var back = editor.AddNode(StateNodes.Transition, new TransitionPayload(Stop.Id));
        var x = editor.AddNode(Scale);
        var y = editor.AddNode(Scale);
        editor.Wire(entry, 0, a, 0);
        editor.Wire(a, 1, there, 0);
        editor.Wire(there, 1, b, 0);
        editor.Wire(b, 1, back, 0);
        var closesStateCycle = editor.Link(back, 1, a, 0);
        editor.Wire(x, 1, y, 0);
        var closesDataCycle = editor.Link(y, 1, x, 0);

        IGraphRule[] rules = [.. data.Rules, .. state.Rules];
        await Assert.That(closesStateCycle.Succeeded).IsTrue();
        await Assert.That(closesDataCycle.Diagnostics.Single().Id).IsEqualTo(DataflowDiagnosticIds.DataCycle);
        await Assert.That(GraphValidator.Validate(editor.Document, rules).Count).IsEqualTo(0);
    }

    [Test]
    public async Task SemanticsPackagesReferenceOnlyTheCore()
    {
        string[] semantics = ["MASS4.Graph.Dataflow", "MASS4.Graph.Control", "MASS4.Graph.State"];
        var assemblies = new[] { typeof(DataflowSemantics), typeof(ControlSemantics), typeof(StateSemantics) }
            .Select(static type => type.Assembly);

        foreach (var assembly in assemblies)
        {
            var references = assembly.GetReferencedAssemblies().Select(static name => name.Name).ToArray();
            await Assert.That(references).Contains("MASS4.Graph");
            await Assert.That(references.Intersect(semantics).Any()).IsFalse();
        }
    }
}
