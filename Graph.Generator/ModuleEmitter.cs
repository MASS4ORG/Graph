using System.Text;
using Microsoft.CodeAnalysis;

namespace MASS4.Graph.Generator;

static class ModuleEmitter
{
    public static string Emit(AttributeData module, Definition[] definitions, SemanticsReferences semantics)
    {
        var text = new StringBuilder(DeclarationEmitter.Header);
        var name = (string)module.ConstructorArguments[0].Value!;
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            text.AppendLine($"namespace {name[..dot]};");
        }
        text.AppendLine($"public sealed class {name[(dot + 1)..]} : IGraphTypeModule\n{{");
        text.AppendLine("public void Register(GraphTypeRegistry registry) {");
        foreach (var prerequisite in module.ConstructorArguments[1].Values)
        {
            text.AppendLine($"{Definition.TypeName(prerequisite)}.Register(registry);");
        }
        foreach (var definition in definitions)
        {
            Register(text, definition);
        }
        text.AppendLine("}");
        // Value declarations bind storage only when the consumer references dataflow; a core-only assembly registers types.
        if (semantics.Dataflow && definitions.Any(static d => d.Kind == "GraphValueAttribute" || BehaviorKind(d) == 0))
        {
            Dataflow(text, definitions);
        }
        Semantics(text, definitions, "Control", 1);
        Semantics(text, definitions, "State", 2);
        return text.AppendLine("}").ToString();
    }

    static void Register(StringBuilder text, Definition definition)
    {
        var registry = Registry(definition);
        text.AppendLine($"registry.{registry}.Register({definition.FullName}.Type);");
        foreach (var rename in definition.Attributes("RenamedFrom"))
        {
            text.AppendLine($"registry.{registry}.AddRename({Definition.Identity((string)rename.ConstructorArguments[0].Value!)}, {definition.FullName}.Type.Id);");
        }
        if (definition.IsGuarded)
        {
            text.AppendLine($"global::MASS4.Graph.State.StateNodes.AddTransitionMigration(registry, {definition.FullName}.Type.Id);");
        }
        foreach (var migration in definition.Attributes("Migration"))
        {
            var args = migration.ConstructorArguments;
            text.AppendLine($"registry.AddMigration(new({definition.FullName}.Type.Id, {args[0].Value}, {Definition.TypeName(args[1])}.@{args[2].Value}));");
        }
    }

    static void Dataflow(StringBuilder text, Definition[] definitions)
    {
        text.AppendLine("public global::MASS4.Graph.Dataflow.DataflowSemantics CreateDataflow() { var semantics = new global::MASS4.Graph.Dataflow.DataflowSemantics();");
        foreach (var definition in definitions.Where(static d => d.Kind == "GraphValueAttribute"))
        {
            BindValue(text, definition);
        }
        foreach (var definition in definitions.Where(static d => BehaviorKind(d) == 0))
        {
            BindDataflow(text, definition);
        }
        text.AppendLine("return semantics; }");
    }

    static void BindValue(StringBuilder text, Definition definition)
    {
        var type = definition.Attribute.ConstructorArguments[2];
        var storage = ((ITypeSymbol)type.Value!).IsReferenceType ? "Reference" : "Value";
        text.AppendLine($"semantics.Add{storage}Type<{Definition.TypeName(type)}>({definition.FullName}.Type.Id);");
    }

    static void BindDataflow(StringBuilder text, Definition definition)
    {
        var behavior = definition.One("Behavior")!;
        var owner = Definition.TypeName(behavior.ConstructorArguments[1]);
        var method = Definition.Named<string>(behavior, "Method");
        var volatileValue = Definition.Named<bool>(behavior, "Volatile") ? "true" : "false";
        var features = behavior.NamedArguments.FirstOrDefault(static p => p.Key == "Features");
        var featureArguments = features.Key is null ? "" : string.Concat(features.Value.Values.Select(static f =>
            $", typeof({Definition.TypeName(f)})"));
        var conversion = Definition.Named<bool>(definition.Attribute, "Conversion");
        text.AppendLine($"semantics.{(conversion ? "AddConversionNode" : "AddNode")}({definition.FullName}.Type{(conversion ? "" : ".Id")}, new global::MASS4.Graph.Dataflow.DataflowNode({owner}.@{method}, {volatileValue}{featureArguments}));");
    }

    static void Semantics(StringBuilder text, Definition[] definitions, string kind, int behaviorKind)
    {
        var nodes = definitions.Where(d => BehaviorKind(d) == behaviorKind ||
            (kind == "State" && (BehaviorKind(d) == 3 || d.IsGuarded)))
            .ToArray();
        if (nodes.Length == 0)
        {
            return;
        }
        text.AppendLine($"public global::MASS4.Graph.{kind}.{kind}Semantics Create{kind}() {{ var semantics = new global::MASS4.Graph.{kind}.{kind}Semantics();");
        foreach (var definition in nodes)
        {
            var behavior = definition.One("Behavior");
            var method = kind == "Control" ? "AddNode" : BehaviorKind(definition) == 2 ? "AddState" : "AddTransition";
            var value = behavior is null ? "global::MASS4.Graph.State.GuardedTransition.Instance"
                : $"new {Definition.TypeName(behavior.ConstructorArguments[1])}()";
            text.AppendLine($"semantics.{method}({definition.FullName}.Type.Id, {value});");
        }
        text.AppendLine("return semantics; }");
    }

    static int BehaviorKind(Definition definition) => definition.One("Behavior")?.ConstructorArguments[0].Value is int kind
        ? kind : -1;

    static string Registry(Definition definition) => definition.Kind switch
    {
        "GraphNodeAttribute" => "NodeTypes",
        "GraphValueAttribute" => "PortTypes",
        _ => "Signals",
    };
}
