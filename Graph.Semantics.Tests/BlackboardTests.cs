namespace MASS4.Graph.Semantics.Tests;

public class BlackboardTests
{
    [Test]
    public async Task VariablesFeedDataflowWithDefaultsAndPerInstanceValues()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var gain = Variable(editor, "gain", Number, 3.0);
        var read = editor.AddNode(DataflowNodes.GetVariable, new VariableReference(gain.Id));
        var doubled = editor.AddNode(Scale);
        editor.Wire(read, 0, doubled, 0);
        var layout = new InstanceLayoutBuilder();
        var program = CompileData(data, editor.Document, layout);
        var instances = new InstanceBuffer(layout.Build());
        var tuned = instances.Allocate();
        var stock = instances.Allocate();
        var context = new EvaluationContext();

        var initial = program.Read<double>(context, instances, tuned, doubled.Port(1));
        program.SetVariable(instances, tuned, gain.Id, 5.0);
        var changed = program.Read<double>(context, instances, tuned, doubled.Port(1));
        var untouched = program.Read<double>(context, instances, stock, doubled.Port(1));
        var stored = program.GetVariable<double>(instances, tuned, gain.Id);
        program.Initialize(instances, tuned);
        program.TryResolve(read.Port(0), out var port);
        var boxedWrite = program.TrySetVariable(instances, stock, gain.Id, 7.0);

        await Assert.That(boxedWrite).IsTrue();
        await Assert.That(program.ReadBoxed(context, instances, stock, port)).IsEqualTo(7.0);

        await Assert.That(initial).IsEqualTo(6);
        await Assert.That(changed).IsEqualTo(10);
        await Assert.That(untouched).IsEqualTo(6);
        await Assert.That(stored).IsEqualTo(5);
        await Assert.That(program.Read<double>(context, instances, tuned, doubled.Port(1))).IsEqualTo(6);
        await Assert.That(program.IsCached(doubled.NodeId)).IsTrue();
        await Assert.That(program.ResolvedType(read.Port(0))).IsEqualTo(Number.Id);
        await Assert.That(program.Variables).Contains(gain.Id);
    }

    [Test]
    public async Task ReferenceVariablesAndUntypedWritesWork()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var label = Variable(editor, "label", Text, "abc");
        var read = editor.AddNode(DataflowNodes.GetVariable, new VariableReference(label.Id));
        var length = editor.AddNode(TextLength);
        editor.Wire(read, 0, length, 0);
        var layout = new InstanceLayoutBuilder();
        var program = CompileData(data, editor.Document, layout);
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();
        var context = new EvaluationContext();

        var initial = program.Read<double>(context, instances, instance, length.Port(1));
        program.SetVariableReference(instances, instance, label.Id, "abcdef");
        var longer = program.Read<double>(context, instances, instance, length.Port(1));
        var accepted = program.TrySetVariable(instances, instance, label.Id, "xy");
        var rejected = program.TrySetVariable(instances, instance, label.Id, 4.0);
        var unknown = program.TrySetVariable(instances, instance, ElementId.New(), "z");
        program.TryResolve(read.Port(0), out var port);

        await Assert.That(initial).IsEqualTo(3);
        await Assert.That(longer).IsEqualTo(6);
        await Assert.That(accepted).IsTrue();
        await Assert.That(rejected).IsFalse();
        await Assert.That(unknown).IsFalse();
        await Assert.That(program.GetVariableReference<string>(instances, instance, label.Id)).IsEqualTo("xy");
        await Assert.That(program.ReadBoxed(context, instances, instance, port)).IsEqualTo("xy");
        await Assert.That(() => program.GetVariable<double>(instances, instance, label.Id))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => program.SetVariable(instances, instance, ElementId.New(), 1.0))
            .ThrowsExactly<KeyNotFoundException>();
    }

    [Test]
    public async Task EditorsRejectBrokenOrMistypedVariableReads()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var gain = Variable(editor, "gain", Number, 1.0);
        var flag = Variable(editor, "flow", StateNodes.FlowType, null);
        var read = editor.AddNode(DataflowNodes.GetVariable, new VariableReference(gain.Id));
        var negate = editor.AddNode(Not);

        var mistyped = editor.Link(read, 0, negate, 0);
        var dangling = new VariableReference(ElementId.New());
        var missing = editor.Execute(new AddNodeCommand(DataflowNodes.GetVariable, dangling));
        var notData = editor.Execute(new AddNodeCommand(DataflowNodes.GetVariable, new VariableReference(flag.Id)));
        var orphaning = editor.Execute(new RemoveVariableCommand(gain.Id));

        await Assert.That(mistyped.Diagnostics.Single().Id).IsEqualTo(DataflowDiagnosticIds.TypeConflict);
        await Assert.That(missing.Diagnostics.Single().Id).IsEqualTo(DataflowDiagnosticIds.VariableNotFound);
        await Assert.That(notData.Diagnostics.Single().Id).IsEqualTo(DataflowDiagnosticIds.VariableNotData);
        await Assert.That(orphaning.Diagnostics.Single().Id).IsEqualTo(DataflowDiagnosticIds.VariableNotFound);
        await Assert.That(editor.Document.Variables.Count).IsEqualTo(2);
    }

    [Test]
    public async Task VariableReadsRoundTripThroughJson()
    {
        var data = CreateDataflow();
        var editor = CreateEditor(data);
        var gain = Variable(editor, "gain", Number, 2.0);
        editor.AddNode(DataflowNodes.GetVariable, new VariableReference(gain.Id));

        var loaded = GraphJson.Deserialize(GraphJson.Serialize(editor.Document), CreateRegistry()).Document!;

        await Assert.That(loaded.Nodes.Single().Payload).IsEqualTo(new VariableReference(gain.Id));
        await Assert.That(GraphValidator.Validate(loaded, data.Rules).Count).IsEqualTo(0);
    }

    static GraphVariable Variable(GraphEditor editor, string name, PortType type, object? value)
    {
        var variable = new GraphVariable(ElementId.New(), name, type.Id, VariableScope.Input) { DefaultValue = value };
        editor.Execute(new SetVariableCommand(variable));
        return variable;
    }
}
