using System.Text.Json;
using System.Text.Json.Nodes;
using MASS4.Graph.Samples.Compositor;
using MASS4.Graph.Samples.Fsm;
using MASS4.Graph.Samples.Shader;
using MASS4.Graph.State;
using C = MASS4.Graph.Samples.Compositor.Generated;
using F = MASS4.Graph.Samples.Fsm.Generated;
using S = MASS4.Graph.Samples.Shader.Generated;

namespace MASS4.Graph.Samples.Tests;

public class GeneratedRegistrationTests
{
    [Test]
    public async Task ShaderRegistrationAndExecutionMatchTheHandWrittenOracle()
    {
        var hand = new GraphTypeRegistry();
        ShaderLibrary.Register(hand);
        var generated = new GraphTypeRegistry().Add(new S.ShaderModule());
        await EqualRegistries(hand, generated);
        await EqualBindings(hand, new(ShaderLibrary.CreateDataflow()), new(new S.ShaderModule().CreateDataflow()));
        await Assert.That(Write(hand.PortTypes.Resolve(S.Scalar.Type.Id).ValueSerializer!, 1.25f))
            .IsEqualTo(Write(S.Scalar.Type.ValueSerializer!, 1.25f));
        await Assert.That(Write(hand.PortTypes.Resolve(S.Colour.Type.Id).ValueSerializer!, new Color(.1f, .2f, .3f, 1)))
            .IsEqualTo(Write(S.Colour.Type.ValueSerializer!, new Color(.1f, .2f, .3f, 1)));
        var example = MaterialExample.Build(ShaderLibrary.CreateDataflow(), hand);
        var scenario = new Scenario("generated shader", 2,
        [
            new SetVariableStep(example.AlbedoMap.Id, Texture.Checker(new(1, 0, 0, 1), new(0, 1, 0, 1))),
            new EvaluateStep(), new ConfigureStep(static c => c.Get<Fragment>().Uv = new(.8f, .8f)),
            new SetVariableStep(example.Intensity.Id, 3f, Instance: 1), new EvaluateStep(),
        ])
        { CreateContext = static () => new EvaluationContext().Set(new Fragment()) };
        await Compare(example.Document, new(ShaderLibrary.CreateDataflow()),
            new(new S.ShaderModule().CreateDataflow()), generated, scenario);
    }

    [Test]
    public async Task GuardRegistrationIncludesTheSameSignalRenameAndMigration()
    {
        var hand = new GraphTypeRegistry();
        GuardLibrary.Register(hand, F.SeePlayer.Type.Id);
        var generated = new GraphTypeRegistry().Add(new F.GuardModule());
        await EqualRegistries(hand, generated);
        await EqualBindings(hand, new(GuardLibrary.CreateDataflow(), GuardLibrary.CreateState()),
            new(new F.GuardModule().CreateDataflow(), new F.GuardModule().CreateState()));
        await Assert.That(hand.Signals.Resolve(GuardLibrary.SeePlayer.Id).Id)
            .IsEqualTo(generated.Signals.Resolve(GuardLibrary.SeePlayer.Id).Id);
        await Assert.That(generated.Signals.Resolve(GuardLibrary.SeePlayer.Id).Id).IsEqualTo(F.SeePlayer.Type.Id);
        var example = GuardExample.Build(GuardLibrary.CreateDataflow(), GuardLibrary.CreateState(), hand);
        var scenario = new Scenario("generated guard", 4,
        [
            new EvaluateStep(), new RaiseStep(F.SeePlayer.Type.Id), new EvaluateStep(),
            new SetVariableStep(example.Distance.Id, 1.0), new EvaluateStep(),
            new RaiseStep(F.Hit.Type.Id, Instance: 2), new EvaluateStep(),
            new SetVariableStep(example.Distance.Id, 5.0), new EvaluateStep(),
            new RaiseStep(F.LosePlayer.Type.Id), new EvaluateStep(), new EvaluateStep(4),
        ]);
        await Compare(example.Document, new(GuardLibrary.CreateDataflow(), state: GuardLibrary.CreateState()),
            new(new F.GuardModule().CreateDataflow(), state: new F.GuardModule().CreateState()), generated, scenario);
    }

    [Test]
    public async Task CompositorRegistrationAndFramesMatchTheHandWrittenOracle()
    {
        var hand = new GraphTypeRegistry();
        CompositorLibrary.Register(hand);
        var generated = new GraphTypeRegistry().Add(new C.CompositorModule());
        await EqualRegistries(hand, generated);
        await EqualBindings(hand, new(CompositorLibrary.CreateDataflow()), new(new C.CompositorModule().CreateDataflow()));
        var example = CompositionExample.Build(CompositorLibrary.CreateDataflow(), hand);
        var scenario = new Scenario("generated compositor", 2,
        [
            new EvaluateStep(), new ConfigureStep(static c => c.Get<Timeline>().Seconds = .25), new EvaluateStep(),
            new ConfigureStep(static c => c.Get<Timeline>().Seconds = 2), new EvaluateStep(),
        ])
        {
            CreateContext = static () => new EvaluationContext().Set(new Timeline()).Set(new RenderSettings { Width = 4, Height = 3 }),
        };
        var options = new DifferentialOptions
        {
            Comparers = new Dictionary<Type, System.Collections.IEqualityComparer>
            { [typeof(Frame)] = CompositorLibrary.FrameComparer },
        };
        await Compare(example.Document, new(CompositorLibrary.CreateDataflow()),
            new(new C.CompositorModule().CreateDataflow()), generated, scenario, options);
    }

    [Test]
    public async Task DomainGuardedTransitionsMigrateOldPayloadsAndRetainUnknownData()
    {
        var module = new F.GuardModule();
        var registry = new GraphTypeRegistry().Add(module);
        var example = GuardExample.Build(module.CreateDataflow(), module.CreateState(), registry);
        var root = JsonNode.Parse(GraphJson.Serialize(example.Document))!.AsObject();
        var saved = root["nodes"]!.AsArray().First(n => n!["type"]!.GetValue<string>() == F.Guarded.Type.Id.ToString())!;
        saved["version"] = 1;
        saved["payload"] = new JsonObject { ["trigger"] = GuardLibrary.SeePlayer.Id.ToString(), ["priority"] = 7 };
        saved.AsObject().Remove("annotations");
        var loaded = GraphJson.Deserialize(root.ToJsonString(), registry);
        await Assert.That(loaded.Diagnostics.Count).IsEqualTo(0);
        var node = loaded.Document!.Nodes.First(n => n.TypeId == F.Guarded.Type.Id);
        await Assert.That(node.TypeVersion).IsEqualTo(2);
        await Assert.That(node.Payload).IsNull();
        await Assert.That(node.Annotations[StateNodes.Trigger.Id]).IsEqualTo(F.SeePlayer.Type.Id);
        await Assert.That(node.Annotations[StateNodes.Priority.Id]).IsEqualTo(7);
        await Assert.That(registry.TryGetMigration(F.Guarded.Type.Id, 1, out _)).IsTrue();
        var bare = new GraphTypeRegistry();
        foreach (var type in registry.NodeTypes.Entries) { bare.NodeTypes.Register(type); }
        foreach (var type in registry.PortTypes.Entries) { bare.PortTypes.Register(type); }
        foreach (var type in registry.Signals.Entries) { bare.Signals.Register(type); }
        foreach (var type in registry.Annotations.Entries) { bare.Annotations.Register(type); }
        var unmigrated = GraphJson.Deserialize(root.ToJsonString(), bare);
        await Assert.That(unmigrated.Diagnostics.Select(static d => d.Id)).Contains(GraphDiagnosticIds.MigrationUnavailable);
    }

    [Test]
    public async Task GeneratedBuildersValidateTypesDefaultsAndAtomicTransitions()
    {
        var module = new S.ShaderModule();
        var registry = new GraphTypeRegistry().Add(module);
        var data = module.CreateDataflow();
        var editor = new GraphEditor(new GraphDocument(registry), data.Conversions, data.Rules);
        var constant = S.Constant.Add(editor);
        var convert = S.ScalarToColor.Add(editor);
        var multiply = S.Multiply.Add(editor);
        editor.Connect(constant.Out, convert.Scalar);
        editor.Connect(convert.Out, multiply.A);
        var colour = S.ColourConstant.Add(editor);
        editor.Connect(colour.Out, multiply.B);
        await Assert.That(editor.Document.GetNode(constant.NodeId).Payload).IsEqualTo(new ScalarPayload(0));
        await Assert.That(editor.Document.Edges.Count).IsEqualTo(3);
        var compiled = (InterpretedGraph)new GraphInterpreter(data).Compile(editor.Document).Graph!;
        var buffers = new InstanceBuffer(compiled.InstanceLayout);
        await Assert.That(compiled.Data.Read<Color>(new(), buffers, buffers.Allocate(), multiply.Out.Id))
            .IsEqualTo(new Color(0, 0, 0, 0));
        await Assert.That(() => S.Constant.Add(new GraphEditor(new GraphDocument(new()))))
            .ThrowsExactly<InvalidOperationException>();
        var guard = new F.GuardModule();
        var stateEditor = new GraphEditor(new GraphDocument(new GraphTypeRegistry().Add(guard)));
        var from = F.Attack.Add(stateEditor);
        var to = F.Attack.Add(stateEditor);
        var transition = F.After.Transition(stateEditor, from.Exit, to.Enter);
        await Assert.That(stateEditor.Document.GetNode(transition.NodeId).Payload).IsEqualTo(new AfterPayload(1));
        var count = stateEditor.Document.Nodes.Count;
        await Assert.That(() => F.After.Transition(stateEditor, new(ElementId.New()), to.Enter))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(stateEditor.Document.Nodes.Count).IsEqualTo(count);
        stateEditor.Undo();
        await Assert.That(stateEditor.Document.Edges.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TypedVariableHandlesRejectWrongAssetsTypesScopesAndUnknownVariables()
    {
        var module = new F.GuardModule();
        var registry = new GraphTypeRegistry().Add(module);
        var example = GuardExample.Build(module.CreateDataflow(), module.CreateState(), registry);
        var graph = new GraphInterpreter(module.CreateDataflow(), state: module.CreateState()).Compile(example.Document).Graph!;
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var handle = F.Number.Bind(example.Document, graph, example.Distance);
        var instance = instances.Allocate();
        handle.Set(instances, instance, 1.0);
        await Assert.That(() => F.Boolean.Bind(example.Document, graph, example.Distance)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => F.Number.Bind(new GraphDocument(registry), graph, example.Distance))
            .ThrowsExactly<ArgumentException>();
        var unknown = new GraphVariable(ElementId.New(), "unknown", F.Number.Type.Id, VariableScope.External);
        await Assert.That(() => F.Number.Bind(example.Document, graph, unknown)).ThrowsExactly<ArgumentException>();
        var local = new GraphVariable(ElementId.New(), "local", F.Number.Type.Id, VariableScope.Local);
        await Assert.That(() => F.Number.Bind(example.Document, graph, local)).ThrowsExactly<ArgumentException>();
        var unsupported = new GraphDocument(registry);
        var external = new GraphVariable(ElementId.New(), "external", F.Number.Type.Id, VariableScope.External);
        TypedGraphBuilder.Require(new GraphEditor(unsupported), new SetVariableCommand(external));
        var withoutStorage = new GraphInterpreter(new()).Compile(unsupported).Graph!;
        var unsupportedInput = F.Number.Bind(unsupported, withoutStorage, external);
        var unsupportedBuffer = new InstanceBuffer(withoutStorage.InstanceLayout);
        await Assert.That(() => unsupportedInput.Set(unsupportedBuffer, unsupportedBuffer.Allocate(), 1.0))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task IncompleteSemanticsAreRejectedByEverySampleBuilder()
    {
        var shader = new GraphTypeRegistry().Add(new S.ShaderModule());
        var fsm = new GraphTypeRegistry().Add(new F.GuardModule());
        var compositor = new GraphTypeRegistry().Add(new C.CompositorModule());
        await Assert.That(() => MaterialExample.Build(new(), shader)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => GuardExample.Build(new(), new F.GuardModule().CreateState(), fsm))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => CompositionExample.Build(new(), compositor)).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task TypedReferenceAndParameterHandlesDriveGeneratedMaterials()
    {
        var module = new S.ShaderModule();
        var registry = new GraphTypeRegistry().Add(module);
        var example = MaterialExample.Build(module.CreateDataflow(), registry);
        var graph = new GraphInterpreter(module.CreateDataflow()).Compile(example.Document).Graph!;
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var instance = instances.Allocate();
        var texture = S.Textures.Bind(example.Document, graph, example.AlbedoMap);
        texture.Set(instances, instance, Texture.Checker(new(1, 0, 0, 1), new(0, 1, 0, 1)));
        var gain = S.Scalar.Bind(example.Document, graph, example.Intensity);
        gain.Set(instances, instance, 3f);
        var pixels = await MaterialRenderer.RenderAsync(graph, instances, instance, example.Albedo.Id, 1, 1);
        await Assert.That(pixels.Single()).IsEqualTo(MaterialExample.Expected(new(1, 0, 0, 1), MaterialExample.DefaultTint, 3));
        await Assert.That(() => S.Textures.Bind(example.Document, graph, example.Intensity)).ThrowsExactly<ArgumentException>();
        var unsupported = new GraphDocument(registry);
        var external = new GraphVariable(ElementId.New(), "texture", S.Textures.Type.Id, VariableScope.External);
        TypedGraphBuilder.Require(new GraphEditor(unsupported), new SetVariableCommand(external));
        var withoutStorage = new GraphInterpreter(new()).Compile(unsupported).Graph!;
        var input = S.Textures.Bind(unsupported, withoutStorage, external);
        var buffer = new InstanceBuffer(withoutStorage.InstanceLayout);
        await Assert.That(() => input.Set(buffer, buffer.Allocate(), null)).ThrowsExactly<InvalidOperationException>();
    }

    static async Task EqualRegistries(GraphTypeRegistry hand, GraphTypeRegistry generated)
    {
        await Assert.That(Snapshot(hand)).IsEqualTo(Snapshot(generated));
        foreach (var expected in hand.Migrations)
        {
            await Assert.That(generated.TryGetMigration(expected.NodeTypeId, expected.FromVersion, out var actual)).IsTrue();
            await Assert.That(actual!.Migrate).IsEqualTo(expected.Migrate);
        }
        foreach (var expected in hand.NodeTypes.Entries)
        {
            var actual = generated.NodeTypes.Resolve(expected.Id);
            var payload = expected.DefaultPayload?.Invoke();
            await Assert.That(actual.DefaultPayload?.Invoke()).IsEqualTo(payload);
            if (expected.PayloadSerializer is { } serializer && payload is not null)
            {
                var serialized = Write(serializer, payload);
                await Assert.That(Write(actual.PayloadSerializer!, payload)).IsEqualTo(serialized);
                using var json = JsonDocument.Parse(serialized);
                await Assert.That(actual.PayloadSerializer!.Read(json.RootElement)).IsEqualTo(serializer.Read(json.RootElement));
            }
        }
    }

    static string Snapshot(GraphTypeRegistry registry) => string.Join("\n",
        registry.PortTypes.Entries.Select(static p => $"P:{p.Id}:{p.DisplayName}:{p.IsWildcard}:{p.ValueSerializer is not null}")
            .Concat(registry.NodeTypes.Entries.Select(static n =>
                $"N:{n.Id}:{n.DisplayName}:{n.Version}:{n.PayloadSerializer is not null}:" + string.Join(";", n.Ports)))
            .Concat(registry.Signals.Entries.Select(static s => $"S:{s.Id}:{s.DisplayName}"))
            .Concat(registry.Annotations.Entries.Select(static a => $"A:{a.Id}:{a.DisplayName}:{a.Kind}"))
            .Concat(registry.HostFunctions.Entries.Select(static f => $"H:{f.Id}:{f.DisplayName}"))
            .Concat(Renames("P", registry.PortTypes)).Concat(Renames("N", registry.NodeTypes))
            .Concat(Renames("S", registry.Signals)).Concat(Renames("A", registry.Annotations))
            .Concat(Renames("H", registry.HostFunctions))
            .Concat(registry.Migrations.Select(static m => $"M:{m.NodeTypeId}:{m.FromVersion}"))
            .Order(StringComparer.Ordinal));

    static IEnumerable<string> Renames<T>(string kind, IdentityRegistry<T> registry) where T : class, ITypeIdentity =>
        registry.Renames.Select(r => $"R{kind}:{r.Key}>{r.Value}");

    /// <summary>The semantics bindings a registration produces, compared per registered identity.</summary>
    sealed record Bindings(DataflowSemantics Data, StateSemantics? State = null);

    static async Task EqualBindings(GraphTypeRegistry registry, Bindings hand, Bindings generated) =>
        await Assert.That(BindingSnapshot(registry, generated)).IsEqualTo(BindingSnapshot(registry, hand));

    static string BindingSnapshot(GraphTypeRegistry registry, Bindings bindings)
    {
        var ports = registry.PortTypes.Entries.ToArray();
        var data = bindings.Data;
        return string.Join("\n", ports.Select(p => data.TryGetValueType(p.Id, out var v)
                ? $"V:{p.Id}:{v.ClrType.FullName}:{v.Storage}" : $"V:{p.Id}:none")
            .Concat(ports.SelectMany(_ => ports, (a, b) => (a, b)).Where(static pair => pair.a.Id != pair.b.Id)
                .Select(pair => (pair, check: data.Conversions.Check(pair.a, pair.b)))
                .Where(static c => c.check.Kind != PortCompatibilityKind.Incompatible)
                .Select(static c => $"C:{c.pair.a.Id}>{c.pair.b.Id}:{c.check.Kind}:{c.check.Conversion?.Id}"))
            .Concat(registry.NodeTypes.Entries.Select(n => $"D:{n.Id}:" + (data.TryGetBehavior(n.Id, out var b)
                ? $"{b.IsVolatile}:{Features(b.RequiredFeatures)}" : "none")))
            .Concat(registry.NodeTypes.Entries.Select(n => $"S:{n.Id}:" + StateRole(bindings.State, n.Id)))
            .Order(StringComparer.Ordinal));
    }

    static string StateRole(StateSemantics? state, StableId id) =>
        state is null ? "none"
        : state.IsEntry(id) ? "entry"
        : state.TryGetState(id, out var s) ? $"state:{s.MemorySize}:{Features(s.RequiredFeatures)}"
        : state.TryGetTransition(id, out var t) ? $"transition:{Features(t.RequiredFeatures)}"
        : "none";

    static string Features(IReadOnlyList<Type> features) =>
        string.Join(",", features.Select(static f => f.FullName).Order(StringComparer.Ordinal));

    static string Write(IValueSerializer serializer, object? value)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) { serializer.Write(writer, value); }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    static async Task Compare(GraphDocument document, GraphInterpreter hand, GraphInterpreter generated,
        GraphTypeRegistry registry, Scenario scenario, DifferentialOptions? options = null)
    {
        var saved = GraphJson.Serialize(document);
        var expected = GraphJson.Deserialize(saved, document.Registry);
        var loaded = GraphJson.Deserialize(saved, registry);
        await Assert.That(loaded.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(expected.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(GraphJson.Serialize(loaded.Document!)).IsEqualTo(GraphJson.Serialize(expected.Document!));
        var report = await DifferentialHarness.CompareAsync(new(expected.Document!), hand,
            new ReloadedBackend(generated, registry), scenario, options);
        await Assert.That(report.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(report.Equivalent).IsTrue();
    }

    sealed class ReloadedBackend(GraphInterpreter interpreter, GraphTypeRegistry registry) : IGraphBackend
    {
        public string Name => "generated registration";
        public ValueTask<CompileResult> CompileAsync(GraphSource source, IProgress<EvaluationProgress>? progress = null,
            CancellationToken cancellation = default)
        {
            var loaded = GraphJson.Deserialize(GraphJson.Serialize(source.Document), registry);
            if (loaded.Diagnostics.Count != 0) { throw new InvalidOperationException(string.Join("; ", loaded.Diagnostics)); }
            return interpreter.CompileAsync(new(loaded.Document!), progress, cancellation);
        }
    }
}
