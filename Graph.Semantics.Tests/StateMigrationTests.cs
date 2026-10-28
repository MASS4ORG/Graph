namespace MASS4.Graph.Semantics.Tests;

/// <summary>
/// The first live migration: schema 1 documents whose transitions kept trigger and priority in a version 1 payload
/// load as schema 2 documents with those values lifted into annotations.
/// </summary>
public class StateMigrationTests
{
    static readonly ElementId Fast = Element(4);
    static readonly ElementId Loop = Element(5);
    static readonly ElementId Halt = Element(6);
    static readonly ElementId Gate = Element(7);
    static readonly ElementId Idle = Element(2);
    static readonly ElementId Walking = Element(3);

    [Test]
    public async Task VersionOneTransitionsLiftTheirPayloadIntoAnnotations()
    {
        var loaded = Load();
        var document = loaded.Document!;

        await Assert.That(loaded.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(Annotations(document, Fast))
            .IsEqualTo($"{StateNodes.Trigger.Id}={Go.Id};{StateNodes.Priority.Id}=2");
        await Assert.That(Annotations(document, Loop)).IsEqualTo($"{StateNodes.Trigger.Id}={Go.Id}");
        await Assert.That(Annotations(document, Halt)).IsEqualTo($"{StateNodes.Trigger.Id}={Stop.Id}");
        await Assert.That(Annotations(document, Gate)).IsEqualTo($"{StateNodes.Priority.Id}=1");
        await Assert.That(document.Nodes.Where(node => node.Ports.Any(p => p.Declaration.Key == StateNodes.FromKey))
            .All(node => node.Payload is null && node.TypeVersion == 2)).IsTrue();
    }

    [Test]
    public async Task MigratedMachinesBehaveAsTheirVersionOneAnnotationsSay()
    {
        var document = Load().Document!;
        var data = CreateDataflow();
        var state = CreateState();
        var layout = new InstanceLayoutBuilder();
        var plane = CompileData(data, document, layout);
        var program = state.Compile(document, layout, plane, new())!;
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();
        var context = new EvaluationContext();

        program.Tick(context, instances, instance, 0.1);
        program.Raise(instances, instance, program.ResolveTrigger(Go.Id));
        program.Tick(context, instances, instance, 0.1);
        var afterGo = program.CurrentState(instances, instance).Id;
        program.Raise(instances, instance, program.ResolveTrigger(Stop.Id));
        program.Tick(context, instances, instance, 0.1);

        await Assert.That(afterGo).IsEqualTo(Walking);
        await Assert.That(program.CurrentState(instances, instance).Id).IsEqualTo(Idle);
    }

    [Test]
    public async Task MigratedDocumentsSaveAsSchemaTwoAndThenRoundTrip()
    {
        var saved = GraphJson.Serialize(Load().Document!);
        var reloaded = GraphJson.Deserialize(saved, CreateRegistry());

        await Assert.That(saved).Contains("\"schemaVersion\": 2");
        await Assert.That(saved).DoesNotContain("\"payload\"");
        await Assert.That(saved).Contains("\"annotations\"");
        await Assert.That(reloaded.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(GraphJson.Serialize(reloaded.Document!)).IsEqualTo(saved);
    }

    [Test]
    public async Task WithoutTheMigrationOldTransitionsArePreservedNotSilentlyStripped()
    {
        var registry = CreateRegistry();
        var bare = new GraphTypeRegistry();
        foreach (var type in registry.PortTypes.Entries)
        {
            bare.PortTypes.Register(type);
        }
        foreach (var type in registry.NodeTypes.Entries)
        {
            bare.NodeTypes.Register(type);
        }

        var loaded = GraphJson.Deserialize(File.ReadAllText(FixturePath), bare);

        var transition = loaded.Document!.GetNode(Fast);
        await Assert.That(loaded.Diagnostics.Select(d => d.Id)).Contains(GraphDiagnosticIds.MigrationUnavailable);
        await Assert.That(transition.TypeVersion).IsEqualTo(1);
        await Assert.That(transition.Payload).IsTypeOf<UnknownPayload>();
    }

    static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "state-machine-v1.json");

    static GraphLoadResult Load() => GraphJson.Deserialize(File.ReadAllText(FixturePath), CreateRegistry());

    static string Annotations(GraphDocument document, ElementId node) =>
        string.Join(";", document.GetNode(node).Annotations.OrderBy(static a => a.Key)
            .Select(static a => $"{a.Key}={a.Value}"));

    static ElementId Element(int value) => new(new Guid($"10000000-0000-4000-8000-{value:D12}"));
}
