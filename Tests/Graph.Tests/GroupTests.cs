using System.Text.Json.Nodes;

namespace MASS4.Graph.Tests;

public class GroupTests
{
    [Test]
    public async Task GroupingPartOfAGroupNestsItAndUndoRestoresMembership()
    {
        var editor = CreateEditor();
        var a = editor.AddNode(Constant, new ConstantPayload(1)).NodeId;
        var b = editor.AddNode(Constant, new ConstantPayload(2)).NodeId;
        var c = editor.AddNode(Print).NodeId;
        var outer = new AddGroupCommand("Outer", nodes: [a, b, c]);
        await Assert.That(editor.Execute(outer).Succeeded).IsTrue();
        var inner = new AddGroupCommand("Inner", outer.GroupId, [a, b]);
        await Assert.That(editor.Execute(inner).Succeeded).IsTrue();

        var document = editor.Document;
        await Assert.That(document.GroupOf(a)).IsEqualTo(inner.GroupId);
        await Assert.That(document.GroupOf(c)).IsEqualTo(outer.GroupId);
        await Assert.That(document.ChildGroups(outer.GroupId).Single().Id).IsEqualTo(inner.GroupId);
        await Assert.That(document.ChildGroups(null).Single().Id).IsEqualTo(outer.GroupId);
        await Assert.That(document.IsWithin(inner.GroupId, outer.GroupId)).IsTrue();
        await Assert.That(document.IsWithin(outer.GroupId, inner.GroupId)).IsFalse();

        editor.Undo();
        await Assert.That(document.GroupOf(a)).IsEqualTo(outer.GroupId);
        await Assert.That(document.Groups.Count).IsEqualTo(1);
        editor.Redo();
        await Assert.That(document.GroupOf(b)).IsEqualTo(inner.GroupId);
        await Assert.That(document.ContainsElement(inner.GroupId)).IsTrue();
    }

    [Test]
    public async Task UngroupingLiftsNodesAndChildGroupsAndUndoRestoresCollapse()
    {
        var editor = CreateEditor();
        var a = editor.AddNode(Constant, new ConstantPayload(1)).NodeId;
        var b = editor.AddNode(Print).NodeId;
        var top = Group(editor, "Top", null, b);
        var middle = Group(editor, "Middle", top, a);
        var bottom = Group(editor, "Bottom", middle);
        editor.Execute(new SetGroupCollapsedCommand(middle, true));

        await Assert.That(editor.Execute(new RemoveGroupCommand(middle)).Succeeded).IsTrue();
        var document = editor.Document;
        await Assert.That(document.GroupOf(a)).IsEqualTo(top);
        await Assert.That(document.TryGetGroup(bottom, out var lifted) ? lifted.Parent : null).IsEqualTo(top);
        await Assert.That(document.CollapsedGroups.Count).IsEqualTo(0);

        editor.Undo();
        await Assert.That(document.GroupOf(a)).IsEqualTo(middle);
        await Assert.That(document.TryGetGroup(bottom, out var restored) ? restored.Parent : null).IsEqualTo(middle);
        await Assert.That(document.CollapsedGroups.Contains(middle)).IsTrue();
        await Assert.That(document.GetGroupTitle(middle)).IsEqualTo("Middle");
    }

    [Test]
    public async Task NestingRejectsCyclesAndMissingGroups()
    {
        var editor = CreateEditor();
        var outer = Group(editor, "Outer", null);
        var inner = Group(editor, "Inner", outer);

        var self = editor.Execute(new SetGroupParentCommand(outer, outer));
        var descendant = editor.Execute(new SetGroupParentCommand(outer, inner));
        var missing = editor.Execute(new SetGroupParentCommand(inner, ElementId.New()));
        var unknown = editor.Execute(new RenameGroupCommand(ElementId.New(), "x"));
        var reused = editor.Execute(new AddGroupCommand("Reused", groupId: inner));

        await Assert.That(self.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.GroupCycle);
        await Assert.That(descendant.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.GroupCycle);
        await Assert.That(missing.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.GroupNotFound);
        await Assert.That(unknown.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.GroupNotFound);
        await Assert.That(reused.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.DuplicateElement);
        await Assert.That(editor.Execute(new SetGroupParentCommand(inner, null)).Succeeded).IsTrue();
        await Assert.That(editor.Document.ChildGroups(null).Count()).IsEqualTo(2);
        editor.Undo();
        await Assert.That(editor.Document.ChildGroups(outer).Single().Id).IsEqualTo(inner);
    }

    [Test]
    public async Task MembershipMovesRejectMissingAndRepeatedNodesAtomically()
    {
        var editor = CreateEditor();
        var a = editor.AddNode(Constant, new ConstantPayload(1)).NodeId;
        var group = Group(editor, "G", null, a);

        var repeated = editor.Execute(new AddGroupCommand("Twice", nodes: [a, a]));
        var missing = editor.Execute(new SetNodeGroupCommand([a, ElementId.New()], null));

        await Assert.That(repeated.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.DuplicateElement);
        await Assert.That(missing.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.NodeNotFound);
        await Assert.That(editor.Document.Groups.Count).IsEqualTo(1);
        await Assert.That(editor.Document.GroupOf(a)).IsEqualTo(group);
        await Assert.That(editor.Execute(new SetNodeGroupCommand([a], null)).Succeeded).IsTrue();
        await Assert.That(editor.Document.GroupOf(a)).IsNull();
        editor.Undo();
        await Assert.That(editor.Document.GroupOf(a)).IsEqualTo(group);
    }

    [Test]
    public async Task RemovingANodeLeavesItsGroupAndUndoRestoresIt()
    {
        var editor = CreateEditor();
        var a = editor.AddNode(Constant, new ConstantPayload(1)).NodeId;
        var group = Group(editor, "G", null, a);

        editor.Execute(new RemoveNodeCommand(a));
        await Assert.That(editor.Document.TryGetGroup(group, out var emptied) ? emptied.Nodes.Count : -1).IsEqualTo(0);
        await Assert.That(editor.Document.GroupOf(a)).IsNull();
        editor.Undo();
        await Assert.That(editor.Document.GroupOf(a)).IsEqualTo(group);
        await Assert.That(editor.Document.TryGetGroup(group, out var restored) ? restored.Nodes.Single() : default)
            .IsEqualTo(a);
    }

    [Test]
    public async Task CollapseIsViewStateResolvedToTheOutermostCollapsedGroup()
    {
        var editor = CreateEditor();
        var a = editor.AddNode(Constant, new ConstantPayload(1)).NodeId;
        var outer = Group(editor, "Outer", null);
        var inner = Group(editor, "Inner", outer, a);
        List<GraphChange> changes = [];
        editor.Document.Changed += (_, recorded) => changes.AddRange(recorded);

        editor.Execute(new SetGroupCollapsedCommand(inner, true));
        await Assert.That(editor.Document.CollapsedContainer(a)).IsEqualTo(inner);
        await Assert.That(editor.Document.CollapsedContainer(inner)).IsNull();
        editor.Execute(new SetGroupCollapsedCommand(outer, true));
        await Assert.That(editor.Document.CollapsedContainer(a)).IsEqualTo(outer);
        await Assert.That(editor.Document.CollapsedContainer(inner)).IsEqualTo(outer);
        await Assert.That(changes.All(static c => c.Kind == GraphChangeKind.LayoutChanged)).IsTrue();

        var withoutLayout = GraphJson.Serialize(editor.Document, new GraphWriteOptions { IncludeLayout = false });
        var reloaded = GraphJson.Deserialize(withoutLayout, editor.Document.Registry).Document!;
        await Assert.That(reloaded.Groups.Count).IsEqualTo(2);
        await Assert.That(reloaded.CollapsedGroups.Count).IsEqualTo(0);
        var layout = GraphJson.SerializeLayout(editor.Document);
        await Assert.That(GraphJson.DeserializeLayout(reloaded, layout).Count).IsEqualTo(0);
        await Assert.That(reloaded.CollapsedGroups.Order()).IsEquivalentTo(new[] { inner, outer }.Order());
        editor.Undo();
        await Assert.That(editor.Document.CollapsedContainer(a)).IsEqualTo(inner);
    }

    [Test]
    public async Task GroupsRoundTripDeterministicallyAndPreserveUnknownFields()
    {
        var editor = CreateEditor();
        var a = editor.AddNode(Constant, new ConstantPayload(1)).NodeId;
        var b = editor.AddNode(Print).NodeId;
        var outer = Group(editor, "Outer", null, b);
        Group(editor, "Inner \"quoted\"", outer, a);
        editor.Execute(new SetGroupCollapsedCommand(outer, true));
        var saved = JsonNode.Parse(GraphJson.Serialize(editor.Document))!;
        saved["groups"]![0]!["color"] = "#ff8800";

        var loaded = GraphJson.Deserialize(saved.ToJsonString(), editor.Document.Registry);
        var again = GraphJson.Serialize(loaded.Document!);

        await Assert.That(loaded.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(again).Contains("\"color\": \"#ff8800\"");
        await Assert.That(GraphJson.Serialize(GraphJson.Deserialize(again, editor.Document.Registry).Document!))
            .IsEqualTo(again);
        await Assert.That(loaded.Document!.GroupOf(a)).IsNotEqualTo(loaded.Document.GroupOf(b));
        await Assert.That(loaded.Document.CollapsedGroups.Single()).IsEqualTo(outer);
    }

    [Test]
    public async Task DocumentsWithoutGroupsKeepTheirSerializedForm()
    {
        var editor = CreateEditor();
        var a = editor.AddNode(Constant, new ConstantPayload(1)).NodeId;
        editor.Execute(new MoveNodeCommand(a, new(1, 2)));
        var before = GraphJson.Serialize(editor.Document);
        var group = Group(editor, "G", null, a);
        editor.Execute(new SetGroupCollapsedCommand(group, true));
        editor.Undo();
        editor.Undo();

        await Assert.That(GraphJson.Serialize(editor.Document)).IsEqualTo(before);
        await Assert.That(before).DoesNotContain("\"groups\"");
        await Assert.That(before).DoesNotContain("\"collapsed\"");
    }

    [Test]
    public async Task InconsistentSavedGroupsLoadWithWarningsInsteadOfFailing()
    {
        var editor = CreateEditor();
        var a = editor.AddNode(Constant, new ConstantPayload(1)).NodeId;
        var first = Group(editor, "First", null, a);
        var second = Group(editor, "Second", null);
        var saved = JsonNode.Parse(GraphJson.Serialize(editor.Document))!;
        var groups = saved["groups"]!.AsArray();
        var firstJson = groups.Single(g => g!["id"]!.GetValue<string>() == first.ToString())!;
        var secondJson = groups.Single(g => g!["id"]!.GetValue<string>() == second.ToString())!;
        firstJson["parent"] = second.ToString();
        secondJson["parent"] = first.ToString();
        secondJson["nodes"] = new JsonArray(a.ToString(), ElementId.New().ToString());
        groups.Add(new JsonObject { ["id"] = ElementId.New().ToString(), ["title"] = "Orphan", ["parent"] = ElementId.New().ToString(), ["nodes"] = new JsonArray() });
        saved["layout"] = new JsonObject { ["positions"] = new JsonArray(), ["collapsed"] = new JsonArray(ElementId.New().ToString()) };

        var loaded = GraphJson.Deserialize(saved.ToJsonString(), editor.Document.Registry);

        await Assert.That(loaded.HasErrors).IsFalse();
        await Assert.That(loaded.Document!.Groups.Count).IsEqualTo(3);
        var ids = loaded.Diagnostics.Select(static d => d.Id).ToArray();
        await Assert.That(ids.Count(static id => id == GraphDiagnosticIds.GroupCycle)).IsEqualTo(1);
        await Assert.That(ids.Count(static id => id == GraphDiagnosticIds.GroupNotFound)).IsEqualTo(2);
        await Assert.That(ids).Contains(GraphDiagnosticIds.NodeNotFound);
        await Assert.That(ids).Contains(GraphDiagnosticIds.DuplicateElement);
        await Assert.That(loaded.Document.Groups.Count(g => g.Nodes.Contains(a))).IsEqualTo(1);
    }

    static ElementId Group(GraphEditor editor, string title, ElementId? parent, params ElementId[] nodes)
    {
        var command = new AddGroupCommand(title, parent, nodes);
        var result = editor.Execute(command);
        return result.Succeeded ? command.GroupId : throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
    }
}

static class GroupTestExtensions
{
    public static string? GetGroupTitle(this GraphDocument document, ElementId group) =>
        document.TryGetGroup(group, out var found) ? found.Title : null;
}
