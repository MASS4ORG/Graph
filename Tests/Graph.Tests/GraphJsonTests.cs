using System.Text.Json.Nodes;

namespace MASS4.Graph.Tests;

public class GraphJsonTests
{
    [Test]
    public async Task SerializeIsDeterministicAndLoadSaveIsLossless()
    {
        var document = BuildSample(CreateRegistry());

        var first = GraphJson.Serialize(document);
        var second = GraphJson.Serialize(document);
        var loaded = GraphJson.Deserialize(first, CreateRegistry());

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(loaded.HasErrors).IsFalse();
        await Assert.That(loaded.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(GraphJson.Serialize(loaded.Document!)).IsEqualTo(first);
        await Assert.That(loaded.Document!.AssetId).IsEqualTo(document.AssetId);
        await Assert.That(loaded.Document.Nodes.Single(n => n.TypeId == Constant.Id).Payload)
            .IsEqualTo(new ConstantPayload(2));
        await Assert.That(loaded.Document.Variables.Single().DefaultValue).IsEqualTo(1.5);
    }

    [Test]
    public async Task DeserializeResolvesRenamedNodeTypes()
    {
        var json = GraphJson.Serialize(BuildSample(CreateRegistry()));
        var renamed = new NodeType(Id(301), "Number constant", Constant.Ports)
        {
            PayloadSerializer = Constant.PayloadSerializer,
        };
        var registry = CreateRegistry(without: Constant);
        registry.NodeTypes.Register(renamed);
        registry.NodeTypes.AddRename(Constant.Id, renamed.Id);

        var loaded = GraphJson.Deserialize(json, registry);

        var node = loaded.Document!.Nodes.Single(n => n.Payload is ConstantPayload);
        await Assert.That(loaded.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(node.TypeId).IsEqualTo(renamed.Id);
        await Assert.That(loaded.Document.Edges.Count).IsEqualTo(4);
        await Assert.That(GraphJson.Serialize(loaded.Document)).Contains(renamed.Id.ToString());
    }

    [Test]
    public async Task UnknownNodesAndPropertiesSurviveLoadAndSave()
    {
        var saved = JsonNode.Parse(GraphJson.Serialize(BuildSample(CreateRegistry())))!;
        saved["zFuture"] = new JsonObject { ["enabled"] = true };
        saved["nodes"]![0]!["futureField"] = new JsonArray(1, 2.50, "x");
        var canonical = GraphJson.Serialize(GraphJson.Deserialize(saved.ToJsonString(), CreateRegistry()).Document!);

        var loaded = GraphJson.Deserialize(canonical, CreateRegistry(without: Constant));

        await Assert.That(loaded.HasErrors).IsFalse();
        await Assert.That(loaded.Diagnostics.Single().Id).IsEqualTo(GraphDiagnosticIds.UnknownNodeType);
        var constant = loaded.Document!.Nodes.Single(n => n.TypeId == Constant.Id);
        await Assert.That(constant.Payload).IsTypeOf<UnknownPayload>();
        await Assert.That(loaded.Document.Extensions.ContainsKey("zFuture")).IsTrue();
        await Assert.That(GraphJson.Serialize(loaded.Document)).IsEqualTo(canonical);
        await Assert.That(canonical).Contains("\"futureField\"");
    }

    [Test]
    public async Task DeserializeMigratesOlderNodes()
    {
        var json = GraphJson.Serialize(BuildSample(CreateRegistry()));
        var registry = CreateRegistry(without: Constant);
        registry.NodeTypes.Register(ConstantV2());
        registry.AddMigration(new(Constant.Id, 1, static node =>
        {
            node["payload"] = new JsonObject { ["Amount"] = node["payload"]!["Value"]!.DeepClone() };
            return node;
        }));

        var loaded = GraphJson.Deserialize(json, registry);

        var node = loaded.Document!.Nodes.Single(n => n.TypeId == Constant.Id);
        await Assert.That(loaded.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(node.Payload).IsEqualTo(new ConstantPayloadV2(2));
        await Assert.That(node.TypeVersion).IsEqualTo(2);
        await Assert.That(GraphJson.Serialize(loaded.Document)).Contains("\"version\": 2");
    }

    [Test]
    public async Task MissingOrFailingMigrationsPreserveTheNode()
    {
        var json = GraphJson.Serialize(BuildSample(CreateRegistry()));
        var missing = CreateRegistry(without: Constant);
        missing.NodeTypes.Register(ConstantV2());
        var failing = CreateRegistry(without: Constant);
        failing.NodeTypes.Register(ConstantV2());
        failing.AddMigration(new(Constant.Id, 1, static _ => throw new InvalidOperationException("boom")));

        foreach (var registry in new[] { missing, failing })
        {
            var loaded = GraphJson.Deserialize(json, registry);
            var node = loaded.Document!.Nodes.Single(n => n.TypeId == Constant.Id);
            await Assert.That(loaded.Diagnostics.Single().Id).IsEqualTo(GraphDiagnosticIds.MigrationUnavailable);
            await Assert.That(node.TypeVersion).IsEqualTo(1);
            await Assert.That(node.Payload).IsTypeOf<UnknownPayload>();
            await Assert.That(GraphJson.Serialize(loaded.Document)).IsEqualTo(json);
        }
    }

    [Test]
    public async Task NodesFromNewerVersionsArePreserved()
    {
        var registry = CreateRegistry(without: Constant);
        registry.NodeTypes.Register(ConstantV2());
        var editor = new GraphEditor(new(registry));
        editor.AddNode(registry.NodeTypes.Resolve(Constant.Id), new ConstantPayloadV2(7));
        var json = GraphJson.Serialize(editor.Document);

        var loaded = GraphJson.Deserialize(json, CreateRegistry());

        await Assert.That(loaded.Diagnostics.Single().Id).IsEqualTo(GraphDiagnosticIds.PayloadUnreadable);
        await Assert.That(GraphJson.Serialize(loaded.Document!)).IsEqualTo(json);
    }

    [Test]
    public async Task KnownTypesReconcilePortsByKey()
    {
        var json = GraphJson.Serialize(BuildSample(CreateRegistry()));
        var cKey = Id(150);
        var reshaped = new NodeType(Add.Id, "Add",
        [
            PortDeclaration.Input(AKey, "left", NumberId),
            PortDeclaration.Input(cKey, "c", NumberId),
            PortDeclaration.Output(SumKey, "sum", NumberId),
        ]);
        var registry = CreateRegistry(without: Add);
        registry.NodeTypes.Register(reshaped);

        var loaded = GraphJson.Deserialize(json, registry);

        var add = loaded.Document!.Nodes.Single(n => n.TypeId == Add.Id);
        var names = add.Ports.Select(p => p.Declaration.Name).ToArray();
        await Assert.That(names).IsEquivalentTo(["left", "c", "sum", "b"]);
        await Assert.That(loaded.Diagnostics.Single().Id).IsEqualTo(GraphDiagnosticIds.PortNotFound);
        await Assert.That(loaded.Document.GetEdges(add.GetPort(BKey).Id).Count).IsEqualTo(1);
        await Assert.That(loaded.Document.GetEdges(add.GetPort(AKey).Id).Count).IsEqualTo(1);
    }

    [Test]
    public async Task LayoutCanBeOmittedOrStoredSeparately()
    {
        var document = BuildSample(CreateRegistry());

        var asset = GraphJson.Serialize(document, new() { IncludeLayout = false });
        var layout = GraphJson.SerializeLayout(document);
        var loaded = GraphJson.Deserialize(asset, CreateRegistry()).Document!;

        await Assert.That(asset).DoesNotContain("\"layout\"");
        await Assert.That(loaded.Positions.Count).IsEqualTo(0);
        await Assert.That(GraphJson.DeserializeLayout(loaded, layout).Count).IsEqualTo(0);
        await Assert.That(loaded.Positions.Count).IsEqualTo(document.Positions.Count);
        await Assert.That(GraphJson.Serialize(loaded)).IsEqualTo(GraphJson.Serialize(document));
        await Assert.That(GraphJson.Serialize(document, new() { Indented = false })).DoesNotContain("\n");
    }

    [Test]
    public async Task LayoutReportsMalformedInputAndMissingNodes()
    {
        var document = new GraphDocument(CreateRegistry());
        var missing = $$"""{"positions":[{"node":"{{ElementId.New()}}","x":1,"y":2}]}""";

        var malformed = GraphJson.DeserializeLayout(document, "{").Single();
        var dangling = GraphJson.DeserializeLayout(document, missing).Single();

        await Assert.That(malformed.Id).IsEqualTo(GraphDiagnosticIds.MalformedDocument);
        await Assert.That(dangling.Id).IsEqualTo(GraphDiagnosticIds.NodeNotFound);
    }

    [Test]
    [Arguments("not json", GraphDiagnosticIds.MalformedDocument)]
    [Arguments("[]", GraphDiagnosticIds.UnsupportedSchema)]
    [Arguments("""{"format":"other","schemaVersion":1}""", GraphDiagnosticIds.UnsupportedSchema)]
    [Arguments("""{"format":"mass4.graph","schemaVersion":99}""", GraphDiagnosticIds.UnsupportedSchema)]
    [Arguments("""{"format":"mass4.graph","schemaVersion":1,"assetId":"x"}""", GraphDiagnosticIds.MalformedDocument)]
    public async Task DeserializeRejectsUnreadableDocuments(string json, string diagnostic)
    {
        var loaded = GraphJson.Deserialize(json, CreateRegistry());

        await Assert.That(loaded.Document).IsNull();
        await Assert.That(loaded.HasErrors).IsTrue();
        await Assert.That(loaded.Diagnostics.Single().Id).IsEqualTo(diagnostic);
    }

    [Test]
    public async Task DeserializeDropsMalformedElementsAndKeepsTheRest()
    {
        var saved = JsonNode.Parse(GraphJson.Serialize(BuildSample(CreateRegistry())))!;
        saved["nodes"]!.AsArray().Add(new JsonObject { ["type"] = Constant.Id.ToString() });
        saved["edges"]!.AsArray().Add(new JsonObject
        {
            ["id"] = ElementId.New().ToString(),
            ["source"] = ElementId.New().ToString(),
            ["target"] = ElementId.New().ToString(),
        });
        saved["variables"] = new JsonObject();

        var loaded = GraphJson.Deserialize(saved.ToJsonString(), CreateRegistry());

        await Assert.That(loaded.HasErrors).IsTrue();
        await Assert.That(loaded.Document!.Nodes.Count).IsEqualTo(5);
        await Assert.That(loaded.Document.Edges.Count).IsEqualTo(4);
        await Assert.That(loaded.Diagnostics.Select(d => d.Id).Order().ToArray()).IsEquivalentTo(
        [
            GraphDiagnosticIds.PortNotFound,
            GraphDiagnosticIds.MalformedDocument,
            GraphDiagnosticIds.MalformedDocument,
        ]);
    }

    [Test]
    public async Task DuplicateIdentitiesAreDroppedWithErrors()
    {
        var saved = JsonNode.Parse(GraphJson.Serialize(BuildSample(CreateRegistry())))!;
        saved["nodes"]!.AsArray().Add(saved["nodes"]![0]!.DeepClone());
        saved["edges"]!.AsArray().Add(saved["edges"]![0]!.DeepClone());
        saved["variables"]!.AsArray().Add(saved["variables"]![0]!.DeepClone());

        var loaded = GraphJson.Deserialize(saved.ToJsonString(), CreateRegistry());

        await Assert.That(loaded.Document!.Nodes.Count).IsEqualTo(5);
        await Assert.That(loaded.Document.Edges.Count).IsEqualTo(4);
        await Assert.That(loaded.Document.Variables.Count).IsEqualTo(1);
        await Assert.That(loaded.Diagnostics.Count(d => d.Id == GraphDiagnosticIds.DuplicateElement)).IsEqualTo(3);
    }

    [Test]
    public async Task UnreadablePayloadIsPreservedWithWarning()
    {
        var saved = JsonNode.Parse(GraphJson.Serialize(BuildSample(CreateRegistry())))!;
        var constant = saved["nodes"]!.AsArray().First(n => n!["type"]!.GetValue<string>() == Constant.Id.ToString())!;
        constant["payload"] = "oops";

        var loaded = GraphJson.Deserialize(saved.ToJsonString(), CreateRegistry());

        await Assert.That(loaded.Diagnostics.Single().Id).IsEqualTo(GraphDiagnosticIds.PayloadUnreadable);
        await Assert.That(GraphJson.Serialize(loaded.Document!)).Contains("\"payload\": \"oops\"");
    }

    [Test]
    public async Task SerializeThrowsForPayloadWithoutSerializer()
    {
        var editor = CreateEditor();
        editor.AddNode(Add, new object());

        await Assert.That(() => GraphJson.Serialize(editor.Document)).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task VariablesOfUnknownTypesKeepTheirDefaults()
    {
        var editor = CreateEditor();
        editor.Execute(new SetVariableCommand(new(ElementId.New(), "target", TriggerId, VariableScope.External)
        {
            DefaultValue = new UnknownPayload(JsonDocument.Parse("""{"path":"/a"}""").RootElement),
        }));
        var json = GraphJson.Serialize(editor.Document);

        var loaded = GraphJson.Deserialize(json, CreateRegistry());

        await Assert.That(loaded.Document!.Variables.Single().Scope).IsEqualTo(VariableScope.External);
        await Assert.That(GraphJson.Serialize(loaded.Document)).IsEqualTo(json);
    }

    [Test]
    public async Task AllVariableScopesRoundTrip()
    {
        var editor = CreateEditor();
        foreach (var scope in Enum.GetValues<VariableScope>())
        {
            editor.Execute(new SetVariableCommand(new(ElementId.New(), scope.ToString(), NumberId, scope)));
        }
        var json = GraphJson.Serialize(editor.Document);
        var broken = json.Replace("\"external\"", "\"global\"", StringComparison.Ordinal);

        var loaded = GraphJson.Deserialize(json, CreateRegistry()).Document!;
        var degraded = GraphJson.Deserialize(broken, CreateRegistry());

        await Assert.That(loaded.Variables.Select(v => v.Scope).Distinct().Count()).IsEqualTo(4);
        await Assert.That(new GraphSource(loaded).Registry).IsSameReferenceAs(loaded.Registry);
        await Assert.That(degraded.Document!.Variables.Count).IsEqualTo(3);
        await Assert.That(degraded.Diagnostics.Single().Message).Contains("global");
    }

    static NodeType ConstantV2() => new(Constant.Id, "Constant", Constant.Ports)
    {
        Version = 2,
        PayloadSerializer = new JsonTypeInfoSerializer<ConstantPayloadV2>(TestJsonContext.Default.ConstantPayloadV2),
    };

    static GraphTypeRegistry CreateRegistry(NodeType? without = null)
    {
        var registry = new GraphTypeRegistry();
        foreach (var type in TestTypes.CreateRegistry().PortTypes.Entries)
        {
            registry.PortTypes.Register(type);
        }
        foreach (var type in TestTypes.CreateRegistry().NodeTypes.Entries.Where(type => type != without))
        {
            registry.NodeTypes.Register(type);
        }
        return registry;
    }

    static GraphDocument BuildSample(GraphTypeRegistry registry)
    {
        var editor = new GraphEditor(new(registry, Id(900)));
        var constant = editor.AddNode(Constant, new ConstantPayload(2));
        var add = editor.AddNode(Add);
        var idle = editor.AddNode(State);
        var walk = editor.AddNode(State);
        editor.AddNode(Print);
        editor.Connect(constant.PortId(ValueKey), add.PortId(AKey));
        editor.Connect(constant.PortId(ValueKey), add.PortId(BKey));
        editor.Connect(idle.PortId(ExitKey), walk.PortId(EnterKey));
        editor.Connect(walk.PortId(ExitKey), idle.PortId(EnterKey));
        var gain = new GraphVariable(ElementId.New(), "gain", NumberId, VariableScope.Input) { DefaultValue = 1.5 };
        editor.Execute(new SetVariableCommand(gain));
        editor.Execute(new MoveNodeCommand(constant.NodeId, new(-10.25, 4)));
        editor.Execute(new MoveNodeCommand(add.NodeId, new(120, 4)));
        return editor.Document;
    }
}
