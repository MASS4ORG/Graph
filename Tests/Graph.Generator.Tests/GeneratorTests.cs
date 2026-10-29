using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace MASS4.Graph.Generator.Tests;

[NotInParallel]
public class GeneratorTests
{
    const string Prefix = """
        using MASS4.Graph;
        [assembly: GraphModule("Fixture.Module", typeof(MASS4.Graph.Dataflow.DataflowNodes))]
        namespace Fixture;

        """;
    const string Id = "10000000-0000-4000-8000-000000000001";
    const string Other = "10000000-0000-4000-8000-000000000002";

    static readonly ImmutableArray<MetadataReference> References = [.. ((string)AppContext.GetData(
            "TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Concat([typeof(GraphTypeRegistry).Assembly.Location, typeof(Dataflow.DataflowNode).Assembly.Location,
            typeof(Control.ControlSemantics).Assembly.Location, typeof(State.StateNodes).Assembly.Location])
        .Distinct(StringComparer.Ordinal).Select(static path => MetadataReference.CreateFromFile(path))];

    /// <summary>The framework and the core only, as a user assembly referencing no semantics package sees them.</summary>
    static readonly ImmutableArray<MetadataReference> CoreReferences = [.. ((string)AppContext.GetData(
            "TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Where(static path => !Path.GetFileName(path).StartsWith("MASS4.", StringComparison.Ordinal))
        .Append(typeof(GraphTypeRegistry).Assembly.Location)
        .Distinct(StringComparer.Ordinal).Select(static path => MetadataReference.CreateFromFile(path))];

    [Test]
    [Arguments("")]
    [Arguments("not-an-id")]
    [Arguments("00000000-0000-0000-0000-000000000000")]
    public async Task MissingAndInvalidIdentitiesAreBuildErrors(string id)
    {
        var result = Generate(Prefix + $"[GraphNode(\"{id}\", \"Node\")] public static partial class Node;");
        await HasError(result, "MG5001");
    }

    [Test]
    public async Task DuplicateAndRenamedRegisteredIdentitiesAreBuildErrors()
    {
        await HasError(Generate(Prefix + $$"""
            [GraphNode("{{Id}}", "A")] public static partial class A;
            [GraphSignal("{{Id}}", "B")] public static partial class B;
            """), "MG5002");
        await HasError(Generate(Prefix + $$"""
            [GraphSignal("{{Id}}", "A")] public static partial class A;
            [GraphSignal("{{Other}}", "B"), GraphRenamedFrom("{{Id}}")]
            public static partial class B;
            """), "MG5002");
    }

    [Test]
    public async Task DuplicatePortKeysAndMembersAndBadKeysAreBuildErrors()
    {
        var source = Prefix + $$"""
            [GraphNode("{{Id}}", "A")]
            [GraphPort("A", "{{Other}}", "a", PortDirection.Input, "{{Id}}", typeof(float))]
            [GraphPort("A", "{{Other}}", "b", PortDirection.Output, "{{Id}}", typeof(float))]
            public static partial class A;
            """;
        await HasError(Generate(source), "MG5003");
        await HasError(Generate(source.Replace(Other, "bad", StringComparison.Ordinal)), "MG5001");
    }

    [Test]
    public async Task InvalidConversionAndPayloadDeclarationsFailBeforeEmission()
    {
        await HasError(Generate(Prefix + $$"""
            [GraphNode("{{Id}}", "A", Conversion = true)] public static partial class A;
            """), "MG5004");
        await HasError(Generate(Prefix + $$"""
            [GraphNode("{{Id}}", "A"), GraphPayload(typeof(string))] public static partial class A;
            """), "MG5005");
        await HasError(Generate(Prefix + $$"""
            [GraphNode("{{Id}}", "A", GuardType = "bad")] public static partial class A;
            """), "MG5005");
    }

    [Test]
    public async Task UnsupportedShapesAndMissingModuleFailLoudly()
    {
        await HasError(Generate(Prefix + $"[GraphNode(\"{Id}\", \"A\")] public class A;"), "MG5005");
        await HasError(Generate($"using MASS4.Graph; [GraphSignal(\"{Id}\", \"A\")] public static partial class A;"), "MG5005");
        var empty = Generate("public class Unrelated;");
        await Assert.That(empty.Generated.Length).IsEqualTo(0);
        await Assert.That(empty.Diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidRenamesAreBuildErrors()
    {
        await HasError(Generate(Prefix + $$"""
            [GraphSignal("{{Id}}", "A"), GraphRenamedFrom("bad")] public static partial class A;
            """), "MG5001");
        await HasError(Generate(Prefix + $$"""
            [GraphSignal("{{Id}}", "A"), GraphRenamedFrom("{{Other}}"), GraphRenamedFrom("{{Other}}")]
            public static partial class A;
            """), "MG5002");
    }

    [Test]
    public async Task TypedBuildersRejectIncompatibleConnectionsAtCompileTime()
    {
        var declaration = Prefix + $$"""
            [GraphNode("{{Id}}", "A")]
            [GraphPort("In", "{{Id}}", "in", PortDirection.Input, "{{Id}}", typeof(float))]
            [GraphPort("Out", "{{Other}}", "out", PortDirection.Output, "{{Other}}", typeof(string))]
            public static partial class A;
            static class Usage {
                static void Wire(GraphEditor editor) {
                    var node = A.Add(editor);
                    editor.Connect(node.Out, node.In);
                }
            }
            """;
        var invalid = Generate(declaration);
        await Assert.That(invalid.Diagnostics.Count(static d => d.Severity == RoslynSeverity.Error)).IsGreaterThan(0);
        var valid = Generate(declaration.Replace("typeof(string)", "typeof(float)", StringComparison.Ordinal));
        await Assert.That(Errors(valid)).IsEqualTo("");
    }

    [Test]
    public async Task ModuleAndHelpersCompileForControlStateMigrationsAndRenames()
    {
        var result = Generate(Prefix + $$"""
            [GraphNode("{{Id}}", "Control", Version = 2)]
            [GraphBehavior(GraphBehaviorKind.Control, typeof(Behavior))]
            [GraphMigration(1, typeof(Behavior), nameof(Behavior.Migrate))]
            [GraphRenamedFrom("10000000-0000-4000-8000-000000000003")]
            public static partial class Node;
            public sealed class Behavior : MASS4.Graph.Control.IControlNode {
                public void Execute(ref MASS4.Graph.Control.ControlContext context) { }
                public static System.Text.Json.Nodes.JsonObject Migrate(System.Text.Json.Nodes.JsonObject node) => node;
            }
            [GraphNode("{{Other}}", "Guarded", GuardType = "10000000-0000-4000-8000-000000000004")]
            public static partial class Guarded;
            """);
        await Assert.That(Errors(result)).IsEqualTo("");
        var combined = string.Join("\n", result.Generated);
        await Assert.That(combined).Contains("AddTransitionMigration");
        await Assert.That(combined).Contains("AddMigration");
        await Assert.That(combined).Contains("AddRename");
    }

    [Test]
    public async Task ValuePayloadConversionAndFeatureBindingsCompileTogether()
    {
        var result = Generate(Prefix + """
            [GraphValue("10000000-0000-4000-8000-000000000001", "number", typeof(float), JsonContext = typeof(Json))]
            public static partial class Number;
            [GraphValue("10000000-0000-4000-8000-000000000002", "text", typeof(string), Serializer = typeof(TextSerializer))]
            public static partial class Text;
            [GraphValue("10000000-0000-4000-8000-000000000003", "boolean", typeof(bool))]
            public static partial class Boolean;
            public sealed class TextSerializer : IValueSerializer {
                public void Write(System.Text.Json.Utf8JsonWriter writer, object? value) => writer.WriteStringValue((string?)value);
                public object? Read(System.Text.Json.JsonElement element) => element.GetString();
            }
            [System.Text.Json.Serialization.JsonSerializable(typeof(float))]
            [System.Text.Json.Serialization.JsonSerializable(typeof(string))]
            public static class Json {
                public static System.Text.Json.Serialization.JsonSerializerContext Default => throw new System.NotSupportedException();
            }
            public sealed record NumberPayload(float Amount);
            [GraphNode("10000000-0000-4000-8000-000000000004", "Constant")]
            [GraphPort("Out", "10000000-0000-4000-8000-000000000004", "out", PortDirection.Output, "10000000-0000-4000-8000-000000000001", typeof(float))]
            [GraphPayload(typeof(NumberPayload), Value = typeof(Number), Member = nameof(NumberPayload.Amount), DefaultFactory = nameof(Default))]
            [GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Constant), Method = nameof(Evaluate))]
            public static partial class Constant {
                static NumberPayload Default() => new(2);
                internal static void Evaluate(MASS4.Graph.Dataflow.DataflowContext context) { }
            }
            [GraphNode("10000000-0000-4000-8000-000000000005", "Conversion", Conversion = true)]
            [GraphPort("In", "10000000-0000-4000-8000-000000000005", "in", PortDirection.Input, "10000000-0000-4000-8000-000000000001", typeof(float))]
            [GraphPort("Out", "10000000-0000-4000-8000-000000000006", "out", PortDirection.Output, "10000000-0000-4000-8000-000000000002", typeof(string), Multiplicity = PortMultiplicity.Multiple)]
            [GraphPayload(typeof(string), JsonContext = typeof(Json))]
            [GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Convert), Method = nameof(Evaluate), Volatile = true, Features = new[] { typeof(object) })]
            public static partial class Convert {
                internal static void Evaluate(MASS4.Graph.Dataflow.DataflowContext context) { }
            }
            """);
        await Assert.That(Errors(result)).IsEqualTo("");
    }

    [Test]
    public async Task MalformedAttributesAndStorageMismatchesFailWithoutGeneratorCrashes()
    {
        await HasError(Generate(Prefix + "[GraphNode] public static partial class Node;"), "MG5001");
        await HasError(Generate(Prefix + $$"""
            [GraphNode("{{Id}}", "Node"), GraphPort] public static partial class Node;
            """), "MG5005");
        await HasError(Generate(Prefix + $$"""
            [GraphNode("{{Id}}", "Node")]
            [GraphPort("In", "{{Id}}", "in", (PortDirection)8, "{{Other}}", typeof(float))]
            public static partial class Node;
            """), "MG5005");
        await HasError(Generate(Prefix + $$"""
            [GraphNode("{{Id}}", "Node"), GraphPayload(typeof(string), Value = typeof(string))]
            public static partial class Node;
            """), "MG5005");
        await HasError(Generate(Prefix + $$"""
            [GraphValue("{{Other}}", "number", typeof(float))] public static partial class Number;
            [GraphNode("{{Id}}", "Node")]
            [GraphPort("In", "{{Id}}", "in", PortDirection.Input, "{{Other}}", typeof(string))]
            public static partial class Node;
            """), "MG5005");
        await HasError(Generate(Prefix + $$"""
            [GraphNode("{{Id}}", "Node", Version = 2), GraphMigration(2, typeof(Node), "Migrate")]
            public static partial class Node;
            """), "MG5005");
    }

    [Test]
    public async Task GlobalNamespaceModulesCompile()
    {
        var result = Generate($$"""
            using MASS4.Graph;
            [assembly: GraphModule("Module")]
            [GraphSignal("{{Id}}", "Signal")] public static partial class Signal;
            """);
        await Assert.That(Errors(result)).IsEqualTo("");
    }

    [Test]
    public async Task TypedPortsAndGuardsTakeIdentityAndStorageFromTheirValueDeclarations()
    {
        const string values = """
            [GraphValue("10000000-0000-4000-8000-000000000011", "number", typeof(float))]
            public static partial class Number;
            [GraphValue("10000000-0000-4000-8000-000000000012", "flag", typeof(bool))]
            public static partial class Flag;

            """;
        var typed = Load(Prefix + values + $$"""
            [GraphNode("{{Other}}", "Node")]
            [GraphPort("In", "{{Id}}", "in", PortDirection.Input, typeof(Number))]
            [GraphPort("Out", "{{Other}}", "out", PortDirection.Output, typeof(Flag), Multiplicity = PortMultiplicity.Single)]
            public static partial class Node;
            [GraphNode("10000000-0000-4000-8000-000000000003", "When", Guard = typeof(Flag))]
            public static partial class When;
            """);
        var spelled = Load(Prefix + values + $$"""
            [GraphNode("{{Other}}", "Node")]
            [GraphPort("In", "{{Id}}", "in", PortDirection.Input, "10000000-0000-4000-8000-000000000011", typeof(float))]
            [GraphPort("Out", "{{Other}}", "out", PortDirection.Output, "10000000-0000-4000-8000-000000000012", typeof(bool), Multiplicity = PortMultiplicity.Single)]
            public static partial class Node;
            [GraphNode("10000000-0000-4000-8000-000000000003", "When", GuardType = "10000000-0000-4000-8000-000000000012")]
            public static partial class When;
            """);
        await Assert.That(Snapshot(typed)).IsEqualTo(Snapshot(spelled));
        await Assert.That(Snapshot(typed)).Contains("in:Input:10000000-0000-4000-8000-000000000011");
    }

    [Test]
    public async Task TypedPortsAndGuardsRejectTypesThatAreNotMatchingValueDeclarations()
    {
        await HasError(Generate(Prefix + $$"""
            [GraphNode("{{Id}}", "Node")]
            [GraphPort("In", "{{Id}}", "in", PortDirection.Input, typeof(float))]
            public static partial class Node;
            """), "MG5005");
        const string number = """
            [GraphValue("10000000-0000-4000-8000-000000000009", "number", typeof(float))]
            public static partial class Number;

            """;
        await HasError(Generate(Prefix + number + $$"""
            [GraphNode("{{Id}}", "When", Guard = typeof(Number))] public static partial class When;
            """), "MG5005");
        await HasError(Generate(Prefix + number + $$"""
            [GraphNode("{{Id}}", "When", Guard = typeof(string))] public static partial class When;
            """), "MG5005");
        await HasError(Generate(Prefix + """
            [GraphValue("10000000-0000-4000-8000-000000000009", "flag", typeof(bool))]
            public static partial class Flag;
            [GraphNode("10000000-0000-4000-8000-000000000001", "When", Guard = typeof(Flag), GuardType = "10000000-0000-4000-8000-000000000009")]
            public static partial class When;
            """), "MG5005");
    }

    [Test]
    public async Task TriggersAreSignalDeclarationsSoBareIdentitiesAndRemovedSignalsDoNotCompile()
    {
        var declaration = Prefix + $$"""
            [GraphValue("10000000-0000-4000-8000-000000000009", "flag", typeof(bool))]
            public static partial class Flag;
            [GraphNode("{{Id}}", "When", Guard = typeof(Flag))] public static partial class When;
            [GraphSignal("{{Other}}", "Go")] public static partial class Go;
            static class Usage {
                static void Wire(GraphEditor editor, GraphOutput<StateFlow> from, GraphInput<StateFlow> to,
                    ICompiledGraph graph, InstanceBuffer instances, InstanceHandle instance) {
                    When.Transition(editor, from, to, trigger: TRIGGER);
                    MASS4.Graph.State.StateGraphBuilder.AddTransition(editor, from, to, TRIGGER);
                    Go.Raise(graph, instances, instance);
                    graph.Raise(instances, instance, Go.Type);
                }
            }
            """;
        await Assert.That(Errors(Generate(declaration.Replace("TRIGGER", "Go.Type", StringComparison.Ordinal)))).IsEqualTo("");
        var bare = Generate(declaration.Replace("TRIGGER", "Go.Type.Id", StringComparison.Ordinal));
        await Assert.That(bare.Diagnostics.Count(static d => d.Id == "CS1503")).IsEqualTo(2);
        var removed = Generate(declaration.Replace("TRIGGER", "Went.Type", StringComparison.Ordinal));
        await Assert.That(removed.Diagnostics.Any(static d => d.Id == "CS0103")).IsTrue();
    }

    [Test]
    public async Task SourceRenamesOfDeclarationsAndMembersLeaveTheRegistrationUnchanged()
    {
        const string source = """
            [GraphValue("10000000-0000-4000-8000-000000000001", "number", typeof(float))]
            public static partial class NUMBER;
            [GraphNode("10000000-0000-4000-8000-000000000002", "Node")]
            [GraphPort("MEMBER", "10000000-0000-4000-8000-000000000003", "out", PortDirection.Output, typeof(NUMBER))]
            public static partial class NODE;
            [GraphSignal("10000000-0000-4000-8000-000000000004", "Go"), GraphRenamedFrom("10000000-0000-4000-8000-000000000005")]
            public static partial class SIGNAL;
            """;
        var before = Load(Prefix + source.Replace("NUMBER", "Number").Replace("NODE", "Node").Replace("MEMBER", "Out")
            .Replace("SIGNAL", "Go"));
        var after = Load(Prefix + source.Replace("NUMBER", "Scalar").Replace("NODE", "Emitter").Replace("MEMBER", "Result")
            .Replace("SIGNAL", "Started"));
        await Assert.That(Snapshot(after)).IsEqualTo(Snapshot(before));
        await Assert.That(after.Signals.Resolve(StableId.Parse("10000000-0000-4000-8000-000000000005")).Id)
            .IsEqualTo(StableId.Parse("10000000-0000-4000-8000-000000000004"));
    }

    [Test]
    public async Task AssetDeclarationsReportMissingMalformedAndUnsupportedAssets()
    {
        const string declaration = """
            [GraphValue("10000000-0000-4000-8000-000000000001", "number", typeof(double))]
            public static partial class Number;
            [GraphNode("10000000-0000-4000-8000-000000000002", "Constant")]
            [GraphPort("Out", "10000000-0000-4000-8000-000000000003", "out", PortDirection.Output, typeof(Number))]
            [GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Constant), Method = nameof(Evaluate))]
            public static partial class Constant
            {
                internal static void Evaluate(MASS4.Graph.Dataflow.DataflowContext context) => context.Write(Indices.Out, 2.0);
            }

            """;
        var asset = Prefix + declaration + """[GraphAsset("assets/one.graph.json")] public sealed partial class One;""";
        var valid = Generate(asset, ("/project/assets/one.graph.json", Asset("10000000-0000-4000-8000-000000000002")));
        await Assert.That(Errors(valid)).IsEqualTo("");
        await Assert.That(valid.Generated.Any(static source => source.Contains("partial class @One : global::MASS4.Graph.ICompiledGraph")))
            .IsTrue();

        await HasAssetError(Generate(asset), "MG6001");
        await HasAssetError(Generate(asset, ("/project/assets/one.graph.json", "{ not json")), "MG6002");
        await HasAssetError(Generate(asset, ("/project/assets/one.graph.json", Asset("10000000-0000-4000-8000-0000000000ff"))), "MG6002");
        await HasAssetError(Generate(Prefix + declaration + """[GraphAsset("one.graph.json")] public static partial class One;""",
            ("one.graph.json", Asset("10000000-0000-4000-8000-000000000002"))), "MG6003");
    }

    static string Asset(string type) => $$"""
        {
          "format": "mass4.graph", "schemaVersion": 2, "assetId": "20000000-0000-4000-8000-000000000001",
          "variables": [],
          "nodes": [{ "id": "20000000-0000-4000-8000-000000000002", "type": "{{type}}", "version": 1,
            "ports": [{ "id": "20000000-0000-4000-8000-000000000003", "key": "10000000-0000-4000-8000-000000000003",
              "name": "out", "direction": "output", "type": "10000000-0000-4000-8000-000000000001", "multiplicity": "multiple" }] }],
          "edges": []
        }
        """;

    static async Task HasAssetError(Result result, string id) =>
        await Assert.That(result.Diagnostics.Any(d => d.Id == id && d.Severity == RoslynSeverity.Error)).IsTrue();

    [Test]
    public async Task SerializersAndJsonContextsAreCheckedAtBuildTime()
    {
        await HasError(Generate(Prefix + """
            public sealed class NotASerializer;
            [GraphValue("10000000-0000-4000-8000-000000000001", "text", typeof(string), Serializer = typeof(NotASerializer))]
            public static partial class Text;
            """), "MG5006");
        await HasError(Generate(Prefix + """
            [System.Text.Json.Serialization.JsonSerializable(typeof(int))]
            public static class Json;
            [GraphValue("10000000-0000-4000-8000-000000000001", "number", typeof(double), JsonContext = typeof(Json))]
            public static partial class Number;
            """), "MG5006");
        await HasError(Generate(Prefix + """
            [System.Text.Json.Serialization.JsonSerializable(typeof(int))]
            public static class Json;
            public sealed record Payload(int Value);
            [GraphNode("10000000-0000-4000-8000-000000000001", "Node"), GraphPayload(typeof(Payload), JsonContext = typeof(Json))]
            public static partial class Node;
            """), "MG5006");
    }

    [Test]
    public async Task CoreOnlyAssembliesRegisterValuesAndReportMissingSemanticsActionably()
    {
        const string module = """
            using MASS4.Graph;
            [assembly: GraphModule("Fixture.Module")]
            namespace Fixture;

            """;
        var coreOnly = Generate(module + """
            public sealed class Note;
            [GraphValue("10000000-0000-4000-8000-000000000001", "note", typeof(Note))]
            public static partial class Notes;
            [GraphNode("10000000-0000-4000-8000-000000000002", "Sticky")]
            [GraphPort("Out", "10000000-0000-4000-8000-000000000003", "out", PortDirection.Output, typeof(Notes))]
            public static partial class Sticky;
            """, coreOnly: true);
        await Assert.That(Errors(coreOnly)).IsEqualTo("");
        await Assert.That(string.Join("\n", coreOnly.Generated)).DoesNotContain("Dataflow");

        var missing = Generate(module + """
            [GraphNode("10000000-0000-4000-8000-000000000002", "Node")]
            [GraphBehavior(GraphBehaviorKind.Dataflow, typeof(Node), Method = "Evaluate")]
            public static partial class Node;
            """, coreOnly: true);
        await HasError(missing, "MG5007");
        await Assert.That(missing.Diagnostics.First(static d => d.Id == "MG5007").GetMessage())
            .Contains("add a reference to the MASS4.Graph.Dataflow package");
    }

    static async Task HasError(Result result, string id)
    {
        await Assert.That(result.Diagnostics.Any(d => d.Id == id && d.Severity == RoslynSeverity.Error)).IsTrue();
        await Assert.That(result.Generated.Length).IsEqualTo(0);
    }

    static string Errors(Result result) => string.Join("\n", result.Diagnostics.Where(static d => d.Severity == RoslynSeverity.Error));

    static Result Generate(string source, params (string Path, string Text)[] files) => Generate(source, false, files);

    static Result Generate(string source, bool coreOnly, params (string Path, string Text)[] files)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("GeneratorFixture", [CSharpSyntaxTree.ParseText(source, parse)],
            coreOnly ? CoreReferences : References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new RegistrationGenerator().AsSourceGenerator()],
            [.. files.Select(static file => (AdditionalText)new FixtureFile(file.Path, file.Text))], parseOptions: parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);
        var result = driver.GetRunResult();
        return new([.. diagnostics, .. updated.GetDiagnostics()],
            [.. result.GeneratedTrees.Where(static tree => !tree.FilePath.EndsWith("GraphDeclarations.g.cs", StringComparison.Ordinal))
                .Select(static tree => tree.ToString())]);
    }

    sealed record Result(ImmutableArray<RoslynDiagnostic> Diagnostics, string[] Generated);

    sealed class FixtureFile(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override Microsoft.CodeAnalysis.Text.SourceText GetText(CancellationToken cancellationToken = default) =>
            Microsoft.CodeAnalysis.Text.SourceText.From(text);
    }

    /// <summary>Compiles and loads a fixture, then registers its generated module into a fresh registry.</summary>
    static GraphTypeRegistry Load(string source)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("Fixture" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, parse)], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        CSharpGeneratorDriver.Create([new RegistrationGenerator().AsSourceGenerator()], parseOptions: parse)
            .RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var generatorDiagnostics);
        if (generatorDiagnostics.Any(static d => d.Severity == RoslynSeverity.Error))
        {
            throw new InvalidOperationException(string.Join("\n", generatorDiagnostics));
        }
        using var image = new MemoryStream();
        var emitted = updated.Emit(image);
        if (!emitted.Success)
        {
            throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
        }
        var assembly = System.Reflection.Assembly.Load(image.ToArray());
        var module = (IGraphTypeModule)Activator.CreateInstance(assembly.GetType("Fixture.Module")!)!;
        return new GraphTypeRegistry().Add(module);
    }

    static string Snapshot(GraphTypeRegistry registry) => string.Join("\n",
        registry.PortTypes.Entries.Select(static p => $"P:{p.Id}:{p.DisplayName}")
            .Concat(registry.NodeTypes.Entries.Select(static n => $"N:{n.Id}:{n.DisplayName}:{n.Version}:" +
                string.Join(";", n.Ports.Select(static p => $"{p.Key}:{p.Name}:{p.Direction}:{p.TypeId}:{p.Multiplicity}"))))
            .Concat(registry.Signals.Entries.Select(static s => $"S:{s.Id}:{s.DisplayName}"))
            .Concat(registry.Signals.Renames.Select(static r => $"R:{r.Key}>{r.Value}"))
            .Concat(registry.Migrations.Select(static m => $"M:{m.NodeTypeId}:{m.FromVersion}"))
            .Order(StringComparer.Ordinal));
}
