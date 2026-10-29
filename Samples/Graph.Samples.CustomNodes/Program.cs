using System.Text.Json.Nodes;
using MASS4.Graph;
using MASS4.Graph.Samples.CustomNodes;

// Registration comes only from the generated module: no reflection, no semantics package.
var registry = new GraphTypeRegistry().Add(new ReviewModule());
var editor = new GraphEditor(new GraphDocument(registry));
var first = Sticky.Add(editor, new StickyPayload("Check the bounds", true));
var second = Sticky.Add(editor);
var list = Checklist.Add(editor);
editor.Connect(first.Text, list.Items);
editor.Connect(second.Text, list.Items);

var saved = JsonNode.Parse(GraphJson.Serialize(editor.Document))!;
var node = saved["nodes"]!.AsArray().First(n => n!["id"]!.GetValue<string>() == first.NodeId.ToString())!;
node["type"] = "7e710000-0000-4000-8000-000000000100";
node["version"] = 1;
node["payload"] = new JsonObject { ["body"] = "Check the bounds", ["pinned"] = true };

var loaded = GraphJson.Deserialize(saved.ToJsonString(), registry);
var migrated = loaded.Document?.GetNode(first.NodeId);
if (loaded.Diagnostics.Count != 0 || migrated?.TypeId != Sticky.Type.Id || migrated.TypeVersion != 2 ||
    !Equals(migrated.Payload, new StickyPayload("Check the bounds", true)) || loaded.Document!.Edges.Count != 2 ||
    registry.Signals.Resolve(Reviewed.Type.Id).DisplayName != "Reviewed")
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, loaded.Diagnostics));
    return 1;
}
Console.WriteLine("Custom nodes registered, edited, migrated and reloaded from a core-only assembly.");
return 0;
