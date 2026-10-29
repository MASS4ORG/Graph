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
        var assets = context.SyntaxProvider.ForAttributeWithMetadataName(Prefix + "AssetAttribute",
            static (node, _) => node is ClassDeclarationSyntax,
            static (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol).Collect();
        var files = context.AdditionalTextsProvider
            .Select(static (file, cancellation) => (file.Path, Text: file.GetText(cancellation)?.ToString())).Collect();
        context.RegisterSourceOutput(assets.Combine(files).Combine(declarations),
            static (output, input) => GenerateAssets(output, input.Left.Left, input.Left.Right, input.Right));
    }

    static readonly DiagnosticDescriptor AssetMissing = new("MG6001", "Graph asset not found",
        "{0}", "MASS4.Graph", DiagnosticSeverity.Error, true);

    static readonly DiagnosticDescriptor AssetInvalid = new("MG6002", "Graph asset cannot be compiled",
        "{0}", "MASS4.Graph", DiagnosticSeverity.Error, true);

    static readonly DiagnosticDescriptor AssetShape = new("MG6003", "Invalid graph asset declaration",
        "{0}", "MASS4.Graph", DiagnosticSeverity.Error, true);

    static void GenerateAssets(
        SourceProductionContext output,
        ImmutableArray<INamedTypeSymbol> assets,
        ImmutableArray<(string Path, string? Text)> files,
        ImmutableArray<INamedTypeSymbol?> symbols)
    {
        var definitions = symbols.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>()
            .Select(static symbol => new Definition(symbol)).ToArray();
        foreach (var asset in assets.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>())
        {
            var location = asset.Locations.FirstOrDefault();
            if (asset.IsStatic || asset.Arity != 0 || asset.ContainingType is not null ||
                !asset.DeclaringSyntaxReferences.All(static r => r.GetSyntax() is ClassDeclarationSyntax c &&
                    c.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                output.ReportDiagnostic(Diagnostic.Create(AssetShape, location,
                    "Graph assets compile into top-level, nongeneric, non-static partial classes."));
                continue;
            }
            var path = asset.GetAttributes().First(static a => a.AttributeClass?.ToDisplayString() == Prefix + "AssetAttribute")
                .ConstructorArguments.FirstOrDefault().Value as string ?? "";
            var normalized = path.Replace('\\', '/').TrimStart('/');
            var file = files.FirstOrDefault(f => f.Path.Replace('\\', '/') is var candidate &&
                (candidate == normalized || candidate.EndsWith("/" + normalized, StringComparison.Ordinal)));
            if (normalized.Length == 0 || file.Text is null)
            {
                output.ReportDiagnostic(Diagnostic.Create(AssetMissing, location,
                    $"No AdditionalFiles item ends with '{path}'; add the asset with <AdditionalFiles Include=\"...\"/>."));
                continue;
            }
            var plan = AssetPlan.Create(file.Text, definitions);
            if (plan.Errors.Count > 0)
            {
                foreach (var error in plan.Errors)
                {
                    output.ReportDiagnostic(Diagnostic.Create(AssetInvalid, location, $"{path}: {error}"));
                }
                continue;
            }
            output.AddSource(asset.ToDisplayString().Replace('<', '_').Replace('>', '_') + ".Asset.g.cs",
                AssetEmitter.Emit(asset, plan));
        }
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
        var semantics = new SemanticsReferences(
            compilation.GetTypeByMetadataName("MASS4.Graph.Dataflow.DataflowSemantics") is not null,
            compilation.GetTypeByMetadataName("MASS4.Graph.Control.ControlSemantics") is not null,
            compilation.GetTypeByMetadataName("MASS4.Graph.State.StateSemantics") is not null);
        if (!DeclarationValidation.Check(output, definitions, module, semantics))
        {
            return;
        }
        foreach (var definition in definitions)
        {
            output.AddSource(definition.FullName.Replace("global::", "", StringComparison.Ordinal) + ".g.cs",
                DeclarationEmitter.Emit(definition, semantics));
        }
        output.AddSource("GraphTypeModule.g.cs", ModuleEmitter.Emit(module!, definitions, semantics));
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

    /// <summary>The guard's boolean type identity, from a typed <c>Guard</c> declaration or a <c>GuardType</c> GUID.</summary>
    public string? GuardId => Named<INamedTypeSymbol>(Attribute, "Guard") is { } guard
        ? ValueDeclaration(guard)?.ConstructorArguments.FirstOrDefault().Value as string ?? ""
        : Named<string>(Attribute, "GuardType");

    public bool IsGuarded => GuardId is not null;

    /// <summary>The GraphValue attribute of a declaration, or null for any other type.</summary>
    internal static AttributeData? ValueDeclaration(ITypeSymbol? symbol) => symbol?.GetAttributes().FirstOrDefault(static a =>
        a.AttributeClass?.ToDisplayString() == RegistrationGenerator.Prefix + "ValueAttribute");

    /// <summary>
    /// Reads a port in either form: an explicit type GUID with a CLR type, or a GraphValue declaration supplying both.
    /// Null when the arguments do not have one of those shapes.
    /// </summary>
    internal static PortArguments? ReadPort(AttributeData port)
    {
        var args = port.ConstructorArguments;
        if (args.Length is not (5 or 6) || args[0].Value is not string member || args[1].Value is not string key ||
            args[2].Value is not string name || args[3].Value is not int direction ||
            args[args.Length - 1].Value is not ITypeSymbol type)
        {
            return null;
        }
        if (args.Length == 6)
        {
            return args[4].Value is string id ? new(member, key, name, direction, id, type, null) : null;
        }
        var value = ValueDeclaration(type);
        return value is { ConstructorArguments: [{ Value: string declared }, _, { Value: ITypeSymbol stored }] }
            ? new(member, key, name, direction, declared, stored, type)
            : new(member, key, name, direction, "", type, type);
    }

    internal static string Quote(string value) => SymbolDisplay.FormatLiteral(value, true);
    internal static string Identity(string value) => $"new global::MASS4.Graph.StableId(new global::System.Guid({Quote(value)}))";
}

/// <summary>Which semantics packages the compilation references; generated code binds only to those.</summary>
readonly record struct SemanticsReferences(bool Dataflow, bool Control, bool State);

/// <summary>A port's arguments; <paramref name="Declaration"/> is set for the typed GraphValue form.</summary>
sealed record PortArguments(string Member, string Key, string Name, int Direction, string TypeId, ITypeSymbol ClrType,
    ITypeSymbol? Declaration)
{
    /// <summary>Whether a typed port names a type without a GraphValue declaration.</summary>
    public bool UndeclaredValue => Declaration is not null && TypeId.Length == 0;
}
