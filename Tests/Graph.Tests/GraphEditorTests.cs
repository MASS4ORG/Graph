namespace MASS4.Graph.Tests;

public class GraphEditorTests
{
    [Test]
    public async Task ConnectAddsEdgeAndUndoRedoRestoreIt()
    {
        var editor = CreateEditor();
        var constant = editor.AddNode(Constant, new ConstantPayload(2));
        var add = editor.AddNode(Add);

        var result = editor.Connect(constant.PortId(ValueKey), add.PortId(AKey));

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(editor.Document.Edges.Count).IsEqualTo(1);
        await Assert.That(editor.Document.GetEdges(add.PortId(AKey)).Count).IsEqualTo(1);
        await Assert.That(editor.NextUndo?.Description).IsEqualTo("Connect");

        await Assert.That(editor.Undo().Succeeded).IsTrue();
        await Assert.That(editor.Document.Edges.Count).IsEqualTo(0);
        await Assert.That(editor.CanRedo).IsTrue();
        await Assert.That(editor.NextRedo?.Description).IsEqualTo("Connect");

        await Assert.That(editor.Redo().Succeeded).IsTrue();
        await Assert.That(editor.Document.Edges.Count).IsEqualTo(1);
        await Assert.That(editor.CanRedo).IsFalse();
    }

    [Test]
    public async Task ConnectRejectsWrongDirectionDuplicatesAndSingleInputOverflow()
    {
        var editor = CreateEditor();
        var first = editor.AddNode(Constant);
        var second = editor.AddNode(Constant);
        var add = editor.AddNode(Add);
        editor.Connect(first.PortId(ValueKey), add.PortId(AKey));

        var backwards = editor.Connect(add.PortId(AKey), first.PortId(ValueKey));
        var duplicate = editor.Connect(first.PortId(ValueKey), add.PortId(AKey));
        var overflow = editor.Connect(second.PortId(ValueKey), add.PortId(AKey));
        var missing = editor.Connect(ElementId.New(), add.PortId(BKey));

        await Assert.That(backwards.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.DirectionMismatch);
        await Assert.That(duplicate.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.DuplicateEdge);
        await Assert.That(overflow.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.MultiplicityExceeded);
        await Assert.That(overflow.Diagnostics[0].ElementId).IsEqualTo(add.PortId(AKey));
        await Assert.That(missing.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.PortNotFound);
        await Assert.That(editor.Document.Edges.Count).IsEqualTo(1);
    }

    [Test]
    public async Task IncompatibleConnectionRollsBackWithDiagnosticNamingTheEdge()
    {
        var editor = CreateEditor();
        var constant = editor.AddNode(Constant);
        var print = editor.AddNode(Print);
        var changes = 0;
        editor.Document.Changed += (_, _) => changes++;
        var command = new ConnectCommand(constant.PortId(ValueKey), print.PortId(InKey));

        var result = editor.Execute(command);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Diagnostics.Single().Id).IsEqualTo(GraphDiagnosticIds.IncompatiblePortTypes);
        await Assert.That(result.Diagnostics.Single().ElementId).IsEqualTo(command.Edge.Id);
        await Assert.That(result.Diagnostics.Single().ToString()).Contains("MG0204");
        await Assert.That(editor.Document.Edges.Count).IsEqualTo(0);
        await Assert.That(changes).IsEqualTo(0);
        await Assert.That(editor.NextUndo is ConnectCommand).IsFalse();
    }

    [Test]
    public async Task ConnectInsertsConversionNodeBetweenPositionedPorts()
    {
        var editor = CreateEditor(new ConversionTable().AddConversion(ToText));
        var constant = editor.AddNode(Constant);
        var print = editor.AddNode(Print);
        editor.Execute(new MoveNodeCommand(constant.NodeId, new(0, 0)));
        editor.Execute(new MoveNodeCommand(print.NodeId, new(100, 40)));

        var result = editor.Connect(constant.PortId(ValueKey), print.PortId(InKey));

        var conversion = editor.Document.Nodes.Single(node => node.TypeId == ToText.Id);
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(editor.Document.Edges.Count).IsEqualTo(2);
        await Assert.That(editor.Document.Positions[conversion.Id]).IsEqualTo(new GraphPoint(50, 20));
        await Assert.That(editor.NextUndo?.Description).IsEqualTo("Connect through To text");

        editor.Undo();
        await Assert.That(editor.Document.Nodes.Count).IsEqualTo(2);
        await Assert.That(editor.Document.Edges.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DirectEdgeAcrossConversionIsRejected()
    {
        var editor = CreateEditor(new ConversionTable().AddConversion(ToText));
        var constant = editor.AddNode(Constant);
        var print = editor.AddNode(Print);

        var result = editor.Execute(new ConnectCommand(constant.PortId(ValueKey), print.PortId(InKey)));

        await Assert.That(result.Diagnostics.Single().Id).IsEqualTo(GraphDiagnosticIds.ConversionRequired);
        await Assert.That(result.Diagnostics.Single().Message).Contains("To text");
    }

    [Test]
    public async Task ImplicitCoercionAndWildcardsAllowDirectEdges()
    {
        var editor = CreateEditor(new ConversionTable().AddImplicit(NumberId, TextId));
        var constant = editor.AddNode(Constant);
        var print = editor.AddNode(Print);
        var relay = editor.AddNode(Relay);

        await Assert.That(editor.Connect(constant.PortId(ValueKey), print.PortId(InKey)).Succeeded).IsTrue();
        await Assert.That(editor.Connect(constant.PortId(ValueKey), relay.PortId(InKey)).Succeeded).IsTrue();
        var unrelated = new ConversionTable().Check(Number, Trigger);
        await Assert.That(unrelated.Kind).IsEqualTo(PortCompatibilityKind.Incompatible);
        await Assert.That(() => new ConversionTable().AddConversion(Add)).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task CyclesAreAllowedByTheCore()
    {
        var editor = CreateEditor();
        var idle = editor.AddNode(State);
        var walk = editor.AddNode(State);

        await Assert.That(editor.Connect(idle.PortId(ExitKey), walk.PortId(EnterKey)).Succeeded).IsTrue();
        await Assert.That(editor.Connect(walk.PortId(ExitKey), idle.PortId(EnterKey)).Succeeded).IsTrue();
        await Assert.That(editor.Connect(idle.PortId(ExitKey), idle.PortId(EnterKey)).Succeeded).IsTrue();
        await Assert.That(editor.Document.Edges.Count).IsEqualTo(3);
    }

    [Test]
    public async Task RemoveNodeUndoRestoresEdgesAndPosition()
    {
        var editor = CreateEditor();
        var idle = editor.AddNode(State);
        var walk = editor.AddNode(State);
        editor.Connect(idle.PortId(ExitKey), walk.PortId(EnterKey));
        editor.Connect(idle.PortId(ExitKey), idle.PortId(EnterKey));
        editor.Execute(new MoveNodeCommand(idle.NodeId, new(3, 4)));
        List<GraphChange> seen = [];
        editor.Document.Changed += (_, changes) => seen.AddRange(changes);

        editor.Execute(new RemoveNodeCommand(idle.NodeId));

        await Assert.That(editor.Document.Nodes.Count).IsEqualTo(1);
        await Assert.That(editor.Document.Edges.Count).IsEqualTo(0);
        await Assert.That(editor.Document.Positions.ContainsKey(idle.NodeId)).IsFalse();
        await Assert.That(seen.Count(change => change.Kind == GraphChangeKind.EdgeRemoved)).IsEqualTo(2);

        editor.Undo();

        await Assert.That(editor.Document.Edges.Count).IsEqualTo(2);
        await Assert.That(editor.Document.Positions[idle.NodeId]).IsEqualTo(new GraphPoint(3, 4));
        await Assert.That(editor.Document.TryGetPort(idle.PortId(EnterKey), out _)).IsTrue();
    }

    [Test]
    public async Task CompositeFailureRevertsEarlierSteps()
    {
        var editor = CreateEditor();
        var add = new AddNodeCommand(Add);
        var composite = new CompositeCommand("Broken", [add, new ConnectCommand(add.PortId(SumKey), ElementId.New())]);

        var result = editor.Execute(composite);

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(editor.Document.Nodes.Count).IsEqualTo(0);
        await Assert.That(editor.CanUndo).IsFalse();
    }

    [Test]
    public async Task AddNodeRejectsUnregisteredTypesAndDuplicateIdentities()
    {
        var editor = CreateEditor();
        var stranger = new NodeType(Id(777), "Stranger", []);
        var first = editor.AddNode(Constant);

        var unregistered = editor.Execute(new AddNodeCommand(stranger));
        var duplicate = editor.Execute(new AddNodeCommand(Constant, nodeId: first.NodeId));

        await Assert.That(unregistered.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.NodeTypeNotRegistered);
        await Assert.That(duplicate.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.DuplicateElement);
        await Assert.That(() => first.PortId(SumKey)).ThrowsExactly<KeyNotFoundException>();
    }

    [Test]
    public async Task SetPayloadIsUndoable()
    {
        var editor = CreateEditor();
        var constant = editor.AddNode(Constant, new ConstantPayload(1));

        editor.Execute(new SetPayloadCommand(constant.NodeId, new ConstantPayload(5)));
        var node = editor.Document.Nodes.Single();
        await Assert.That(node.Payload).IsEqualTo(new ConstantPayload(5));

        editor.Undo();
        await Assert.That(node.Payload).IsEqualTo(new ConstantPayload(1));
        await Assert.That(editor.Execute(new SetPayloadCommand(ElementId.New(), null)).Succeeded).IsFalse();
    }

    [Test]
    public async Task VariablesAddReplaceRemoveAndUndo()
    {
        var editor = CreateEditor();
        var speed = new GraphVariable(ElementId.New(), "speed", NumberId, VariableScope.Input) { DefaultValue = 1.0 };

        editor.Execute(new SetVariableCommand(speed));
        editor.Execute(new SetVariableCommand(speed with { Name = "velocity" }));
        await Assert.That(editor.Document.Variables.Single().Name).IsEqualTo("velocity");

        editor.Undo();
        await Assert.That(editor.Document.Variables.Single().Name).IsEqualTo("speed");

        editor.Execute(new RemoveVariableCommand(speed.Id));
        await Assert.That(editor.Document.Variables.Count).IsEqualTo(0);
        editor.Undo();
        editor.Undo();
        await Assert.That(editor.Document.Variables.Count).IsEqualTo(0);
        await Assert.That(editor.Execute(new RemoveVariableCommand(speed.Id)).Succeeded).IsFalse();
    }

    [Test]
    public async Task VariablesWithUnknownTypesWarnButApply()
    {
        var editor = CreateEditor();
        var node = editor.AddNode(Constant);

        var mystery = new GraphVariable(ElementId.New(), "mystery", Id(555), VariableScope.Local);
        var result = editor.Execute(new SetVariableCommand(mystery));
        var clash = editor.Execute(new SetVariableCommand(new(node.NodeId, "clash", NumberId, VariableScope.Local)));

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Diagnostics.Single().Id).IsEqualTo(GraphDiagnosticIds.UnknownPortType);
        await Assert.That(clash.Diagnostics.Single().Id).IsEqualTo(GraphDiagnosticIds.DuplicateElement);
    }

    [Test]
    public async Task UndoLimitDiscardsOldestSteps()
    {
        var editor = CreateEditor();
        editor.UndoLimit = 2;
        editor.AddNode(Constant);
        editor.AddNode(Constant);
        editor.AddNode(Constant);

        editor.Undo();
        editor.Undo();

        await Assert.That(editor.CanUndo).IsFalse();
        await Assert.That(editor.Undo().Succeeded).IsFalse();
        await Assert.That(editor.Document.Nodes.Count).IsEqualTo(1);
        editor.ClearHistory();
        await Assert.That(editor.Redo().Succeeded).IsFalse();
    }

    [Test]
    public async Task DisconnectIsUndoableAndReportsMissingEdges()
    {
        var editor = CreateEditor();
        var constant = editor.AddNode(Constant);
        var add = editor.AddNode(Add);
        var connect = new ConnectCommand(constant.PortId(ValueKey), add.PortId(BKey));
        editor.Execute(connect);

        editor.Execute(new DisconnectCommand(connect.Edge.Id));
        await Assert.That(editor.Document.Edges.Count).IsEqualTo(0);
        editor.Undo();
        await Assert.That(editor.Document.TryGetEdge(connect.Edge.Id, out _)).IsTrue();

        var missing = editor.Execute(new DisconnectCommand(ElementId.New()));
        await Assert.That(missing.Diagnostics.Single().Id).IsEqualTo(GraphDiagnosticIds.EdgeNotFound);
        await Assert.That(editor.Execute(new MoveNodeCommand(ElementId.New(), null)).Succeeded).IsFalse();
        await Assert.That(editor.Execute(new RemoveNodeCommand(ElementId.New())).Succeeded).IsFalse();
    }

    [Test]
    public async Task CustomRulesRunAfterBuiltInRules()
    {
        var editor = new GraphEditor(new(CreateRegistry()), null, [new MaxNodesRule(1)]);
        editor.AddNode(Constant);

        var result = editor.Execute(new AddNodeCommand(Constant));

        await Assert.That(editor.Rules.Count).IsEqualTo(3);
        await Assert.That(result.Diagnostics.Single().Id).IsEqualTo("TEST01");
        await Assert.That(editor.Document.Nodes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CommandsLabelTheirHistorySteps()
    {
        var editor = CreateEditor();
        var constant = editor.AddNode(Constant);
        var add = editor.AddNode(Add);
        var connect = new ConnectCommand(constant.PortId(ValueKey), add.PortId(AKey));
        var variable = new GraphVariable(ElementId.New(), "gain", NumberId, VariableScope.Local);
        IGraphCommand[] commands =
        [
            connect,
            new DisconnectCommand(connect.Edge.Id),
            new SetPayloadCommand(constant.NodeId, new ConstantPayload(3)),
            new MoveNodeCommand(constant.NodeId, new(1, 1)),
            new SetVariableCommand(variable),
            new RemoveVariableCommand(variable.Id),
            new RemoveNodeCommand(add.NodeId),
        ];
        List<string> labels = [Constant.DisplayName];

        foreach (var command in commands)
        {
            editor.Execute(command);
            labels.Add(editor.NextUndo!.Description);
        }

        await Assert.That(labels).IsEquivalentTo(
        [
            "Constant", "Connect", "Disconnect", "Edit node", "Move node", "Set variable gain", "Remove variable",
            "Remove node",
        ]);
        await Assert.That(new AddNodeCommand(Add).Description).IsEqualTo("Add Add");
        await Assert.That(new SetAnnotationCommand(constant.NodeId, Id(999), 1).Description)
            .IsEqualTo("Edit annotation");
    }

    [Test]
    public async Task DiagnosticBagExposesItsItems()
    {
        var bag = new DiagnosticBag();
        bag.Warning("W1", null, "first");
        bag.Error("E1", ElementId.New(), "second");
        var untyped = ((System.Collections.IEnumerable)bag).GetEnumerator();
        var enumerated = 0;
        while (untyped.MoveNext())
        {
            enumerated++;
        }

        await Assert.That(bag.Count).IsEqualTo(2);
        await Assert.That(bag[0].Id).IsEqualTo("W1");
        await Assert.That(bag.HasErrors).IsTrue();
        await Assert.That(enumerated).IsEqualTo(2);
        await Assert.That(bag[0].ToString()).IsEqualTo("warning W1: first");
    }

    [Test]
    public async Task CompatibilityResultsReportWhetherDirectEdgesAreAllowed()
    {
        var table = new ConversionTable().AddImplicit(NumberId, TriggerId).AddConversion(ToText);

        await Assert.That(table.Check(Number, Number).AllowsDirectEdge).IsTrue();
        await Assert.That(table.Check(Number, Trigger).AllowsDirectEdge).IsTrue();
        await Assert.That(table.Check(Number, Text).AllowsDirectEdge).IsFalse();
        await Assert.That(table.Check(Text, Number).AllowsDirectEdge).IsFalse();
        await Assert.That(StableId.New().CompareTo(StableId.Empty)).IsNotEqualTo(0);
    }

    sealed class MaxNodesRule(int max) : IGraphRule
    {
        public void Validate(GraphValidationContext context)
        {
            if (context.Graph.Nodes.Count > max)
            {
                context.Report(new("TEST01", DiagnosticSeverity.Error, null, "Too many nodes."));
            }
        }
    }
}
