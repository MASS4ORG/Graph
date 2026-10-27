using System.Text.Json.Nodes;

namespace MASS4.Graph.Tests;

public class AnnotationTests
{
    static readonly SignalType Ping = new(Id(401), "Ping");
    static readonly AnnotationType Cue = new(Id(411), "cue", AnnotationKind.Signal);
    static readonly AnnotationType Rank = new(Id(412), "rank", AnnotationKind.Integer);
    static readonly AnnotationType Weight = new(Id(413), "weight", AnnotationKind.Number);
    static readonly AnnotationType Muted = new(Id(414), "muted", AnnotationKind.Boolean);
    static readonly AnnotationType Note = new(Id(415), "note", AnnotationKind.Text);

    [Test]
    [Arguments(AnnotationKind.Signal, 0)]
    [Arguments(AnnotationKind.Integer, 1)]
    [Arguments(AnnotationKind.Number, 2)]
    [Arguments(AnnotationKind.Boolean, 3)]
    [Arguments(AnnotationKind.Text, 4)]
    public async Task EachKindAcceptsExactlyItsValueType(AnnotationKind kind, int accepted)
    {
        object[] values = [Ping.Id, 3, 1.5, true, "text"];
        var type = new AnnotationType(Id(499), "probe", kind);

        var accepts = values.Select(type.Accepts).ToArray();

        await Assert.That(accepts[accepted]).IsTrue();
        await Assert.That(accepts.Count(static a => a)).IsEqualTo(1);
        await Assert.That(type.Accepts(null)).IsFalse();
    }

    [Test]
    public async Task SetAnnotationAppliesRemovesAndUndoes()
    {
        var editor = Editor();
        var node = editor.AddNode(Constant, new ConstantPayload(1));
        var graphNode = editor.Document.GetNode(node.NodeId);

        await Assert.That(editor.Execute(new SetAnnotationCommand(node.NodeId, Rank.Id, 4)).Succeeded).IsTrue();
        await Assert.That(graphNode.Annotations[Rank.Id]).IsEqualTo(4);
        editor.Execute(new SetAnnotationCommand(node.NodeId, Rank.Id, 9));
        editor.Undo();
        await Assert.That(graphNode.Annotations[Rank.Id]).IsEqualTo(4);
        editor.Execute(new SetAnnotationCommand(node.NodeId, Rank.Id, null));
        await Assert.That(graphNode.Annotations.ContainsKey(Rank.Id)).IsFalse();
        editor.Undo();
        editor.Undo();
        await Assert.That(graphNode.Annotations.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetAnnotationRejectsUnregisteredTypesMismatchedValuesAndMissingNodes()
    {
        var editor = Editor();
        var node = editor.AddNode(Constant, new ConstantPayload(1));

        var unregistered = editor.Execute(new SetAnnotationCommand(node.NodeId, Id(498), 1));
        var mismatch = editor.Execute(new SetAnnotationCommand(node.NodeId, Rank.Id, "high"));
        var missing = editor.Execute(new SetAnnotationCommand(ElementId.New(), Rank.Id, 1));

        await Assert.That(unregistered.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.AnnotationTypeNotRegistered);
        await Assert.That(mismatch.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.AnnotationValueMismatch);
        await Assert.That(missing.Succeeded).IsFalse();
        await Assert.That(editor.Document.GetNode(node.NodeId).Annotations.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AddNodeValidatesAndCanonicalizesItsAnnotations()
    {
        var registry = Registry();
        var renamed = new AnnotationType(Id(421), "rank", AnnotationKind.Integer);
        registry.Annotations.Register(renamed);
        registry.Annotations.AddRename(Id(420), renamed.Id);
        var editor = new GraphEditor(new GraphDocument(registry));

        var added = new AddNodeCommand(Constant, new ConstantPayload(1))
        {
            Annotations = new Dictionary<StableId, object> { [Id(420)] = 2 },
        };
        var rejected = editor.Execute(new AddNodeCommand(Constant, new ConstantPayload(1))
        {
            Annotations = new Dictionary<StableId, object> { [Rank.Id] = 2.5 },
        });

        await Assert.That(editor.Execute(added).Succeeded).IsTrue();
        await Assert.That(editor.Document.GetNode(added.NodeId).Annotations[renamed.Id]).IsEqualTo(2);
        await Assert.That(rejected.Diagnostics[0].Id).IsEqualTo(GraphDiagnosticIds.AnnotationValueMismatch);
        await Assert.That(editor.Document.Nodes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AnnotationsRoundTripDeterministicallyInEveryKind()
    {
        var editor = Editor();
        var node = editor.AddNode(Constant, new ConstantPayload(1));
        foreach (var (type, value) in new (AnnotationType, object)[]
                 {
                     (Note, "hello"), (Muted, true), (Weight, 0.25), (Rank, -3), (Cue, Ping.Id),
                 })
        {
            editor.Execute(new SetAnnotationCommand(node.NodeId, type.Id, value));
        }

        var saved = GraphJson.Serialize(editor.Document);
        var loaded = GraphJson.Deserialize(saved, Registry());
        var annotations = loaded.Document!.GetNode(node.NodeId).Annotations;

        await Assert.That(loaded.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(GraphJson.Serialize(loaded.Document)).IsEqualTo(saved);
        await Assert.That(saved.IndexOf(Cue.Id.ToString(), StringComparison.Ordinal))
            .IsLessThan(saved.IndexOf(Note.Id.ToString(), StringComparison.Ordinal));
        await Assert.That(annotations[Cue.Id]).IsEqualTo(Ping.Id);
        await Assert.That(annotations[Rank.Id]).IsEqualTo(-3);
        await Assert.That(annotations[Weight.Id]).IsEqualTo(0.25);
        await Assert.That((bool)annotations[Muted.Id]).IsTrue();
        await Assert.That(annotations[Note.Id]).IsEqualTo("hello");
    }

    [Test]
    public async Task NodesWithoutAnnotationsOmitTheProperty()
    {
        var editor = Editor();
        editor.AddNode(Constant, new ConstantPayload(1));

        await Assert.That(GraphJson.Serialize(editor.Document)).DoesNotContain("\"annotations\"");
    }

    [Test]
    public async Task UnknownAnnotationsAndMismatchedValuesArePreservedVerbatim()
    {
        var saved = Saved(out var node);
        var annotations = saved["nodes"]![0]!["annotations"]!.AsObject();
        annotations[Id(497).ToString()] = new JsonObject { ["future"] = new JsonArray(1, "two") };
        annotations[Rank.Id.ToString()] = "not a number";
        var json = saved.ToJsonString();

        var loaded = GraphJson.Deserialize(json, Registry());
        var resaved = JsonNode.Parse(GraphJson.Serialize(loaded.Document!))!["nodes"]![0]!["annotations"]!;

        await Assert.That(loaded.Diagnostics.Single().Id).IsEqualTo(GraphDiagnosticIds.PayloadUnreadable);
        await Assert.That(loaded.Document!.GetNode(node).Annotations[Id(497)]).IsTypeOf<UnknownPayload>();
        await Assert.That(resaved[Id(497).ToString()]!.ToJsonString()).IsEqualTo("""{"future":[1,"two"]}""");
        await Assert.That(resaved[Rank.Id.ToString()]!.GetValue<string>()).IsEqualTo("not a number");
    }

    [Test]
    public async Task RenamedAnnotationTypesAndSignalsResolveOnLoad()
    {
        var json = Saved(out var node).ToJsonString();
        var registry = Registry(renamed: true);
        var cue = new AnnotationType(Id(431), "cue", AnnotationKind.Signal);
        var ping = new SignalType(Id(432), "Ping");
        registry.Annotations.Register(cue);
        registry.Annotations.AddRename(Cue.Id, cue.Id);
        registry.Signals.Register(ping);
        registry.Signals.AddRename(Ping.Id, ping.Id);

        var loaded = GraphJson.Deserialize(json, registry);

        await Assert.That(loaded.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(loaded.Document!.GetNode(node).Annotations[cue.Id]).IsEqualTo(ping.Id);
    }

    [Test]
    [Arguments("""[1]""")]
    [Arguments("""{"not-an-id":1}""")]
    public async Task MalformedAnnotationsDropTheNode(string annotations)
    {
        var saved = Saved(out var node);
        saved["nodes"]![0]!["annotations"] = JsonNode.Parse(annotations);

        var loaded = GraphJson.Deserialize(saved.ToJsonString(), Registry());

        await Assert.That(loaded.HasErrors).IsTrue();
        await Assert.That(loaded.Document!.Nodes.Any(n => n.Id == node)).IsFalse();
    }

    [Test]
    public async Task SchemaOneDocumentsStillLoad()
    {
        var saved = JsonNode.Parse(GraphJson.Serialize(Editor().Document))!;
        saved["schemaVersion"] = 1;

        var loaded = GraphJson.Deserialize(saved.ToJsonString(), Registry());

        await Assert.That(loaded.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(GraphJson.Serialize(loaded.Document!)).Contains("\"schemaVersion\": 2");
    }

    static JsonNode Saved(out ElementId node)
    {
        var editor = Editor();
        var added = editor.AddNode(Constant, new ConstantPayload(1));
        editor.Execute(new SetAnnotationCommand(added.NodeId, Cue.Id, Ping.Id));
        editor.Execute(new SetAnnotationCommand(added.NodeId, Rank.Id, 1));
        node = added.NodeId;
        return JsonNode.Parse(GraphJson.Serialize(editor.Document))!;
    }

    static GraphEditor Editor() => new(new GraphDocument(Registry(), Id(900)));

    /// <summary>The test registry; <paramref name="renamed"/> leaves out the cue type and the ping signal.</summary>
    static GraphTypeRegistry Registry(bool renamed = false)
    {
        var registry = CreateRegistry();
        foreach (var type in new[] { Rank, Weight, Muted, Note })
        {
            registry.Annotations.Register(type);
        }
        if (!renamed)
        {
            registry.Annotations.Register(Cue);
            registry.Signals.Register(Ping);
        }
        return registry;
    }
}
