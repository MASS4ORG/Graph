using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MASS4.Graph.Generator;

/// <summary>Emits stable registrations, semantics bindings and typed editing helpers from explicit declarations.</summary>
[Generator]
public sealed class RegistrationGenerator : IIncrementalGenerator
{
    internal const string Prefix = "MASS4.Graph.Graph";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static output =>
            output.AddSource("GraphDeclarations.g.cs", AttributeDefinitions.Source));
        var declarations = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
            static (syntax, cancellation) => syntax.SemanticModel.GetDeclaredSymbol(
                (ClassDeclarationSyntax)syntax.Node, cancellation))
            .Where(static symbol => symbol is not null && symbol.GetAttributes().Any(static a =>
                a.AttributeClass?.ToDisplayString() is Prefix + "NodeAttribute" or
                    Prefix + "ValueAttribute" or Prefix + "SignalAttribute"))
            .Collect();
        context.RegisterSourceOutput(declarations.Combine(context.CompilationProvider),
            static (output, input) => Generate(output, input.Left, input.Right));
    }

    static void Generate(SourceProductionContext output, ImmutableArray<INamedTypeSymbol?> symbols, Compilation compilation)
    {
        var definitions = symbols.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>()
            .Select(static symbol => new Definition(symbol)).OrderBy(static d => d.FullName, StringComparer.Ordinal)
            .ToArray();
        if (definitions.Length == 0)
        {
            return;
        }
        var module = compilation.Assembly.GetAttributes().FirstOrDefault(static a =>
            a.AttributeClass?.ToDisplayString() == Prefix + "ModuleAttribute");
        if (!DeclarationValidation.Check(output, definitions, module))
        {
            return;
        }
        foreach (var definition in definitions)
        {
            output.AddSource(definition.FullName.Replace("global::", "", StringComparison.Ordinal) + ".g.cs",
                DeclarationEmitter.Emit(definition));
        }
        output.AddSource("GraphTypeModule.g.cs", ModuleEmitter.Emit(module!, definitions));
    }
}

sealed class Definition(INamedTypeSymbol symbol)
{
    public INamedTypeSymbol Symbol { get; } = symbol;
    public string FullName => Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    public AttributeData Attribute { get; } = symbol.GetAttributes().First(static a =>
        a.AttributeClass?.ToDisplayString() is RegistrationGenerator.Prefix + "NodeAttribute" or
            RegistrationGenerator.Prefix + "ValueAttribute" or RegistrationGenerator.Prefix + "SignalAttribute");
    public string Kind => Attribute.AttributeClass!.Name;
    public string Id => Text(0);
    public string Name => Text(1);
    public string Text(int index) => index < Attribute.ConstructorArguments.Length
        ? Attribute.ConstructorArguments[index].Value as string ?? "" : "";
    public IEnumerable<AttributeData> Attributes(string name) => Symbol.GetAttributes().Where(a =>
        a.AttributeClass?.ToDisplayString() == RegistrationGenerator.Prefix + name + "Attribute");
    public AttributeData? One(string name) => Attributes(name).FirstOrDefault();
    public AttributeData[] Ports => [.. Attributes("Port")];

    internal static T? Named<T>(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(p => p.Key == name).Value.Value is T value ? value : default;

    internal static string TypeName(TypedConstant constant) =>
        ((ITypeSymbol)constant.Value!).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    internal static string? NamedType(AttributeData attribute, string name) =>
        Named<ITypeSymbol>(attribute, name)?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    internal static string Quote(string value) => SymbolDisplay.FormatLiteral(value, true);
    internal static string Identity(string value) => $"new global::MASS4.Graph.StableId(new global::System.Guid({Quote(value)}))";
}
