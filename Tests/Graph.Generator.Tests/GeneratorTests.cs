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

    static async Task HasError(Result result, string id)
    {
        await Assert.That(result.Diagnostics.Any(d => d.Id == id && d.Severity == RoslynSeverity.Error)).IsTrue();
        await Assert.That(result.Generated.Length).IsEqualTo(0);
    }

    static string Errors(Result result) => string.Join("\n", result.Diagnostics.Where(static d => d.Severity == RoslynSeverity.Error));

    static Result Generate(string source)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("GeneratorFixture", [CSharpSyntaxTree.ParseText(source, parse)],
            References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new RegistrationGenerator().AsSourceGenerator()], parseOptions: parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);
        var result = driver.GetRunResult();
        return new([.. diagnostics, .. updated.GetDiagnostics()],
            [.. result.GeneratedTrees.Where(static tree => !tree.FilePath.EndsWith("GraphDeclarations.g.cs", StringComparison.Ordinal))
                .Select(static tree => tree.ToString())]);
    }

    sealed record Result(ImmutableArray<RoslynDiagnostic> Diagnostics, string[] Generated);
}
