namespace MASS4.Graph.Semantics.Tests;

public class CompositionTests
{
    [Test]
    public async Task OneDocumentMixesStateDataflowAndControl()
    {
        var mixed = BuildMixed();
        var (plane, machine, flow, instances, diagnostics) = Compile(mixed);
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
        var strideLength = plane.Read<double>(context, instances, instance, mixed.Stride.Port(1));
        var sum = plane.Read<double>(context, instances, instance, mixed.Total.Port(2));
        machine.Raise(instances, instance, machine.ResolveTrigger(Stop.Id));
        machine.Tick(context, instances, instance, 0.1);
        var stopped = machine.CurrentState(instances, instance).Id;
        machine.Tick(context, instances, instance, 0.1);

        await Assert.That(diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)).IsEqualTo(0);
        await Assert.That(resting).IsEqualTo(mixed.Idle.NodeId);
        await Assert.That(strideLength).IsEqualTo(1);
        await Assert.That(sum).IsEqualTo(2.5);
        await Assert.That(stopped).IsEqualTo(mixed.Idle.NodeId);
        await Assert.That(machine.CurrentState(instances, instance).Id).IsEqualTo(mixed.Walk.NodeId);
    }

    [Test]
    public async Task PublishesAreVolatileRootsForDataflowCaching()
    {
        var mixed = BuildMixed();
        var (plane, _, _, instances, _) = Compile(mixed);
        var instance = instances.Allocate();
        var context = new EvaluationContext();
        plane.TryResolve(mixed.Walk.Port(2), out var elapsed);
        plane.TryResolve(mixed.Threshold.Port(0), out var threshold);
        plane.TryResolve(mixed.Stride.Port(0), out var strideInput);

        plane.Write(instances, instance, elapsed, 2.0);
        var before = plane.Read<double>(context, instances, instance, mixed.Stride.Port(1));
        plane.Write(instances, instance, elapsed, 3.0);
        var after = plane.Read<double>(context, instances, instance, mixed.Stride.Port(1));

        await Assert.That(before).IsEqualTo(4);
        await Assert.That(after).IsEqualTo(6);
        await Assert.That(plane.IsCached(mixed.Threshold.NodeId)).IsTrue();
        await Assert.That(plane.IsCached(mixed.Stride.NodeId)).IsFalse();
        await Assert.That(plane.IsCached(mixed.Total.NodeId)).IsFalse();
        await Assert.That(plane.IsCached(mixed.Faster.NodeId)).IsFalse();
        foreach (var computed in new[] { threshold, strideInput })
        {
            await Assert.That(() => plane.Write(instances, instance, computed, 1.0))
                .ThrowsExactly<InvalidOperationException>();
        }
    }

    static (DataflowProgram, StateMachineProgram, ControlProgram, InstanceBuffer, DiagnosticBag) Compile(
        MixedDocument mixed)
    {
        var layout = new InstanceLayoutBuilder();
        var diagnostics = new DiagnosticBag();
        var document = mixed.Editor.Document;
        var plane = mixed.Data.Compile(document, layout, diagnostics)!;
        var machine = mixed.State.Compile(document, layout, plane, diagnostics)!;
        var flow = mixed.Control.Compile(document, layout, plane, diagnostics);
        return (plane, machine, flow, new(layout.Build()), diagnostics);
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
        var there = editor.AddTransition(StateNodes.Transition, Go.Id);
        var back = editor.AddTransition(StateNodes.Transition, Stop.Id);
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
