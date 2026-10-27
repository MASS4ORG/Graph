using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Report = System.Action<Microsoft.CodeAnalysis.DiagnosticDescriptor, Microsoft.CodeAnalysis.Location?, string>;

namespace MASS4.Graph.Generator;

static class DeclarationValidation
{
    static readonly DiagnosticDescriptor InvalidIdentity = Descriptor("MG5001", "Missing or invalid identity");
    static readonly DiagnosticDescriptor DuplicateIdentity = Descriptor("MG5002", "Duplicate or renamed identity");
    static readonly DiagnosticDescriptor DuplicatePort = Descriptor("MG5003", "Duplicate port key or member");
    static readonly DiagnosticDescriptor ConversionShape = Descriptor("MG5004", "Invalid conversion shape");
    static readonly DiagnosticDescriptor InvalidDeclaration = Descriptor("MG5005", "Invalid graph declaration");

    public static bool Check(SourceProductionContext output, Definition[] definitions, AttributeData? module)
    {
        var valid = true;
        var identities = new HashSet<Guid>();
        var renames = new HashSet<Guid>();
        foreach (var definition in definitions)
        {
            CheckDefinition(definition, identities, ReportError);
            CheckRenames(definition, renames, ReportError);
            CheckPorts(definition, ReportError);
            CheckNodeOptions(definition, ReportError);
            CheckPayload(definition, ReportError);
            CheckMigrations(definition, ReportError);
        }
        CheckStorage(definitions, ReportError);
        foreach (var collision in identities.Intersect(renames))
        {
            ReportError(DuplicateIdentity, null, $"Renamed identity {collision} is also registered.");
        }
        if (!ValidModule(module))
        {
            ReportError(InvalidDeclaration, null, "Declare one assembly GraphModule with a valid qualified class name.");
        }
        return valid;

        void ReportError(DiagnosticDescriptor descriptor, Location? location, string message)
        {
            valid = false;
            output.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
        }
    }

    static bool ValidModule(AttributeData? module) => module?.ConstructorArguments.FirstOrDefault().Value is string name &&
        name.Split('.').All(static part => SyntaxFacts.IsValidIdentifier(part));

    static void CheckDefinition(Definition definition, HashSet<Guid> identities, Report report)
    {
        var location = definition.Symbol.Locations.FirstOrDefault();
        if (!TryGuid(definition.Id, out var id))
        {
            report(InvalidIdentity, location, $"{definition.FullName} requires an explicit nonempty GUID.");
        }
        else if (!identities.Add(id))
        {
            report(DuplicateIdentity, location, $"{id} is declared more than once.");
        }
        if (!IsDeclaration(definition.Symbol))
        {
            report(InvalidDeclaration, location, "Graph declarations must be top-level static partial, nongeneric classes.");
        }
    }

    static bool IsDeclaration(INamedTypeSymbol symbol) => symbol.IsStatic && symbol.Arity == 0 &&
        symbol.ContainingType is null && symbol.DeclaringSyntaxReferences.All(static syntax =>
            syntax.GetSyntax() is ClassDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword));

    static void CheckRenames(Definition definition, HashSet<Guid> renames, Report report)
    {
        foreach (var rename in definition.Attributes("RenamedFrom"))
        {
            if (!TryGuid(rename.ConstructorArguments.FirstOrDefault().Value as string, out var previous))
            {
                report(InvalidIdentity, definition.Symbol.Locations[0], "Renames require explicit nonempty GUIDs.");
            }
            else if (!renames.Add(previous))
            {
                report(DuplicateIdentity, definition.Symbol.Locations[0], $"{previous} is renamed more than once.");
            }
        }
    }

    static void CheckPorts(Definition definition, Report report)
    {
        var keys = new HashSet<Guid>();
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var port in definition.Ports)
        {
            var location = definition.Symbol.Locations[0];
            if (port.ConstructorArguments.Length != 6)
            {
                report(InvalidDeclaration, location, "Ports require a member, key, name, direction, type GUID and CLR type.");
                continue;
            }
            CheckPortIdentity(port, location, keys, report);
            var member = port.ConstructorArguments[0].Value as string ?? "";
            if (!SyntaxFacts.IsValidIdentifier(member) || member == "NodeId" || !members.Add(member))
            {
                report(DuplicatePort, location, $"Port member '{member}' is invalid or duplicated.");
            }
        }
    }

    static void CheckPortIdentity(AttributeData port, Location location, HashSet<Guid> keys, Report report)
    {
        var args = port.ConstructorArguments;
        if (!TryGuid(args[1].Value as string, out var key) || !TryGuid(args[4].Value as string, out _))
        {
            report(InvalidIdentity, location, "Ports require explicit nonempty key and type GUIDs.");
        }
        else if (!keys.Add(key))
        {
            report(DuplicatePort, location, $"Port key {key} occurs twice in this node.");
        }
        CheckPortEnums(port, location, report);
    }

    static void CheckPortEnums(AttributeData port, Location location, Report report)
    {
        if (port.ConstructorArguments[3].Value is not (0 or 1) ||
            port.NamedArguments.Any(static p => p.Key == "Multiplicity" && p.Value.Value is not (0 or 1)))
        {
            report(InvalidDeclaration, location, "Port direction and multiplicity must be declared enum values.");
        }
    }

    static void CheckNodeOptions(Definition definition, Report report)
    {
        var location = definition.Symbol.Locations[0];
        if (Definition.Named<bool>(definition.Attribute, "Conversion") && !ValidConversion(definition))
        {
            report(ConversionShape, location, "Conversions require one input, one output and a dataflow behavior.");
        }
        if (Definition.Named<string>(definition.Attribute, "GuardType") is { } guard &&
            (!TryGuid(guard, out _) || definition.Ports.Length != 0 || definition.One("Payload") is not null))
        {
            report(InvalidDeclaration, location, "Guarded helpers require a boolean type GUID and own their ports and legacy payload migration.");
        }
    }

    static bool ValidConversion(Definition definition) => ValidConversionPorts(definition.Ports) &&
        definition.One("Behavior")?.ConstructorArguments.FirstOrDefault().Value is 0;

    static bool ValidConversionPorts(AttributeData[] ports) => ports.Length == 2 &&
        ports.All(static p => p.ConstructorArguments.Length == 6) &&
        ports[0].ConstructorArguments[3].Value is int first &&
        ports[1].ConstructorArguments[3].Value is int second && first != second;

    static void CheckPayload(Definition definition, Report report)
    {
        if (definition.One("Payload") is not { } payload)
        {
            return;
        }
        var context = Definition.NamedType(payload, "JsonContext");
        var value = Definition.Named<INamedTypeSymbol>(payload, "Value");
        if ((context is null) == (value is null))
        {
            report(InvalidDeclaration, definition.Symbol.Locations[0], "Payloads require exactly one source-generated JSON context or value declaration.");
        }
        else if (value is not null && !value.GetAttributes().Any(static a =>
                     a.AttributeClass?.ToDisplayString() == RegistrationGenerator.Prefix + "ValueAttribute"))
        {
            report(InvalidDeclaration, definition.Symbol.Locations[0], "Single-value payloads require a GraphValue declaration.");
        }
    }

    static void CheckMigrations(Definition definition, Report report)
    {
        var version = Definition.Named<int>(definition.Attribute, "Version");
        var guarded = Definition.Named<string>(definition.Attribute, "GuardType") is not null;
        var current = guarded ? 2 : version == 0 ? 1 : version;
        HashSet<int> versions = guarded ? [1] : [];
        foreach (var migration in definition.Attributes("Migration"))
        {
            var from = migration.ConstructorArguments.FirstOrDefault().Value;
            if (!ValidMigrationStep(from, current, versions))
            {
                report(InvalidDeclaration, definition.Symbol.Locations[0], "Migration versions must be unique positive steps below the node's current version.");
            }
        }
    }

    static bool ValidMigrationStep(object? from, int current, HashSet<int> versions) =>
        from is int input && input > 0 && input < current && versions.Add(input);

    static void CheckStorage(Definition[] definitions, Report report)
    {
        var values = new Dictionary<Guid, ITypeSymbol>();
        foreach (var definition in definitions.Where(static d => d.Kind == "GraphValueAttribute"))
        {
            if (definition.Attribute.ConstructorArguments.Length == 3 && TryGuid(definition.Id, out var id))
            {
                values.TryAdd(id, (ITypeSymbol)definition.Attribute.ConstructorArguments[2].Value!);
            }
        }
        foreach (var definition in definitions)
        {
            foreach (var port in definition.Ports.Where(static p => p.ConstructorArguments.Length == 6))
            {
                CheckPortStorage(port, definition.Symbol.Locations[0], values, report);
            }
        }
    }

    static void CheckPortStorage(AttributeData port, Location location, Dictionary<Guid, ITypeSymbol> values, Report report)
    {
        var args = port.ConstructorArguments;
        if (TryGuid(args[4].Value as string, out var type) && values.TryGetValue(type, out var expected) &&
            !SymbolEqualityComparer.Default.Equals(expected, args[5].Value as ITypeSymbol))
        {
            report(InvalidDeclaration, location, "The port's CLR type differs from its GraphValue storage declaration.");
        }
    }

    static bool TryGuid(string? value, out Guid id) => Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;

    static DiagnosticDescriptor Descriptor(string id, string title) =>
        new(id, title, "{0}", "MASS4.Graph", DiagnosticSeverity.Error, true);
}
