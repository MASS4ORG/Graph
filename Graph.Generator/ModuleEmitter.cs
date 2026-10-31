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
        var dataflow = BindsDataflow(definitions, semantics);
        var control = SemanticsNodes(definitions, "Control", 1);
        var state = SemanticsNodes(definitions, "State", 2);
        text.AppendLine($"public sealed class {name[(dot + 1)..]} : {Interfaces(dataflow, control, state)}\n{{");
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
        if (dataflow)
        {
            Dataflow(text, definitions);
        }
        Semantics(text, control, "Control");
        Semantics(text, state, "State");
        return text.AppendLine("}").ToString();
    }

    // Value declarations bind storage only when the consumer references dataflow; a core-only assembly registers types.
    static bool BindsDataflow(Definition[] definitions, SemanticsReferences semantics) => semantics.Dataflow &&
        definitions.Any(static d => d.Kind == "GraphValueAttribute" || BehaviorKind(d) == 0);

    static string Interfaces(bool dataflow, Definition[] control, Definition[] state) => "IGraphTypeModule"
        + (dataflow ? ", global::MASS4.Graph.Dataflow.IDataflowModule" : "")
        + (control.Length > 0 ? ", global::MASS4.Graph.Control.IControlModule" : "")
        + (state.Length > 0 ? ", global::MASS4.Graph.State.IStateModule" : "");

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
        text.AppendLine("public global::MASS4.Graph.Dataflow.DataflowSemantics CreateDataflow() { var semantics = new global::MASS4.Graph.Dataflow.DataflowSemantics(); ConfigureDataflow(semantics); return semantics; }");
        text.AppendLine("public void ConfigureDataflow(global::MASS4.Graph.Dataflow.DataflowSemantics semantics) {");
        foreach (var definition in definitions.Where(static d => d.Kind == "GraphValueAttribute"))
        {
            BindValue(text, definition);
        }
        foreach (var definition in definitions.Where(static d => BehaviorKind(d) == 0))
        {
            BindDataflow(text, definition);
        }
        text.AppendLine("}");
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

    static Definition[] SemanticsNodes(Definition[] definitions, string kind, int behaviorKind) =>
        [.. definitions.Where(d => BehaviorKind(d) == behaviorKind ||
            (kind == "State" && (BehaviorKind(d) == 3 || d.IsGuarded)))];

    static void Semantics(StringBuilder text, Definition[] nodes, string kind)
    {
        if (nodes.Length == 0)
        {
            return;
        }
        var type = $"global::MASS4.Graph.{kind}.{kind}Semantics";
        text.AppendLine($"public {type} Create{kind}() {{ var semantics = new {type}(); Configure{kind}(semantics); return semantics; }}");
        text.AppendLine($"public void Configure{kind}({type} semantics) {{");
        foreach (var definition in nodes)
        {
            var behavior = definition.One("Behavior");
            var method = kind == "Control" ? "AddNode" : BehaviorKind(definition) == 2 ? "AddState" : "AddTransition";
            var value = behavior is null ? "global::MASS4.Graph.State.GuardedTransition.Instance"
                : $"new {Definition.TypeName(behavior.ConstructorArguments[1])}()";
            text.AppendLine($"semantics.{method}({definition.FullName}.Type.Id, {value});");
        }
        text.AppendLine("}");
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
