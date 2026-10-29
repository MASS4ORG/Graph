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
    static readonly DiagnosticDescriptor InvalidSerializer = Descriptor("MG5006", "Invalid serializer declaration");
    static readonly DiagnosticDescriptor MissingSemantics = Descriptor("MG5007", "Semantics package not referenced");

    public static bool Check(
        SourceProductionContext output,
        Definition[] definitions,
        AttributeData? module,
        SemanticsReferences semantics)
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
            CheckSerializers(definition, ReportError);
            CheckSemantics(definition, semantics, ReportError);
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
        foreach (var attribute in definition.Ports)
        {
            var location = definition.Symbol.Locations[0];
            if (Definition.ReadPort(attribute) is not { } port)
            {
                report(InvalidDeclaration, location, "Ports require a member, key, name, direction and either a GraphValue declaration or a type GUID with a CLR type.");
                continue;
            }
            if (port.UndeclaredValue)
            {
                report(InvalidDeclaration, location, $"Port '{port.Member}' names {port.ClrType.ToDisplayString()}, which is not a GraphValue declaration.");
                continue;
            }
            CheckPortIdentity(attribute, port, location, keys, report);
            if (!SyntaxFacts.IsValidIdentifier(port.Member) || port.Member == "NodeId" || !members.Add(port.Member))
            {
                report(DuplicatePort, location, $"Port member '{port.Member}' is invalid or duplicated.");
            }
        }
    }

    static void CheckPortIdentity(AttributeData attribute, PortArguments port, Location location, HashSet<Guid> keys, Report report)
    {
        if (!TryGuid(port.Key, out var key) || !TryGuid(port.TypeId, out _))
        {
            report(InvalidIdentity, location, "Ports require explicit nonempty key and type GUIDs.");
        }
        else if (!keys.Add(key))
        {
            report(DuplicatePort, location, $"Port key {key} occurs twice in this node.");
        }
        CheckPortEnums(attribute, location, report);
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
        if (definition.GuardId is { } guard &&
            (!TryGuid(guard, out _) || definition.Ports.Length != 0 || definition.One("Payload") is not null))
        {
            report(InvalidDeclaration, location, "Guarded helpers require a boolean type GUID and own their ports and legacy payload migration.");
        }
        if (Definition.Named<INamedTypeSymbol>(definition.Attribute, "Guard") is { } typed &&
            (Definition.Named<string>(definition.Attribute, "GuardType") is not null ||
             Definition.ValueDeclaration(typed)?.ConstructorArguments.LastOrDefault().Value is not ITypeSymbol
             {
                 SpecialType: SpecialType.System_Boolean,
             }))
        {
            report(InvalidDeclaration, location, "Guard names a GraphValue declaration storing bool, and replaces GuardType.");
        }
    }

    static bool ValidConversion(Definition definition) => ValidConversionPorts(definition.Ports) &&
        definition.One("Behavior")?.ConstructorArguments.FirstOrDefault().Value is 0;

    static bool ValidConversionPorts(AttributeData[] ports) => ports.Length == 2 &&
        Definition.ReadPort(ports[0]) is { } first && Definition.ReadPort(ports[1]) is { } second &&
        first.Direction != second.Direction;

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
        var guarded = definition.IsGuarded;
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
            foreach (var port in definition.Ports.Select(Definition.ReadPort))
            {
                CheckPortStorage(port, definition.Symbol.Locations[0], values, report);
            }
        }
    }

    static void CheckPortStorage(PortArguments? port, Location location, Dictionary<Guid, ITypeSymbol> values, Report report)
    {
        // Typed ports take their CLR type from the declaration, so only GUID-form ports can disagree with it.
        if (port is { Declaration: null } && TryGuid(port.TypeId, out var type) && values.TryGetValue(type, out var expected) &&
            !SymbolEqualityComparer.Default.Equals(expected, port.ClrType))
        {
            report(InvalidDeclaration, location, "The port's CLR type differs from its GraphValue storage declaration.");
        }
    }

    /// <summary>Serializer types must be constructible serializers; JSON contexts must declare the serialized type.</summary>
    static void CheckSerializers(Definition definition, Report report)
    {
        var location = definition.Symbol.Locations[0];
        if (definition.Kind == "GraphValueAttribute" && definition.Attribute.ConstructorArguments.LastOrDefault().Value is ITypeSymbol stored)
        {
            if (Definition.Named<INamedTypeSymbol>(definition.Attribute, "Serializer") is { } serializer && !IsSerializer(serializer))
            {
                report(InvalidSerializer, location, $"{serializer.ToDisplayString()} must implement MASS4.Graph.IValueSerializer and have a public parameterless constructor.");
            }
            CheckJsonContext(Definition.Named<INamedTypeSymbol>(definition.Attribute, "JsonContext"), stored, location, report);
        }
        if (definition.One("Payload") is { ConstructorArguments: [{ Value: ITypeSymbol payload }, ..] } attribute)
        {
            CheckJsonContext(Definition.Named<INamedTypeSymbol>(attribute, "JsonContext"), payload, location, report);
        }
    }

    static bool IsSerializer(INamedTypeSymbol type) => !type.IsAbstract &&
        type.AllInterfaces.Any(static i => i.ToDisplayString() == "MASS4.Graph.IValueSerializer") &&
        type.InstanceConstructors.Any(static c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);

    static void CheckJsonContext(INamedTypeSymbol? context, ITypeSymbol type, Location location, Report report)
    {
        if (context is null)
        {
            return;
        }
        var declared = context.GetAttributes()
            .Where(static a => a.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializableAttribute")
            .Select(static a => a.ConstructorArguments.FirstOrDefault().Value as ITypeSymbol);
        if (!declared.Any(t => SymbolEqualityComparer.Default.Equals(t, type)))
        {
            report(InvalidSerializer, location, $"{context.ToDisplayString()} declares no [JsonSerializable(typeof({type.ToDisplayString()}))]; registration would fail at run time.");
        }
    }

    /// <summary>Behaviors and guarded transitions bind to semantics packages the assembly must reference.</summary>
    static void CheckSemantics(Definition definition, SemanticsReferences semantics, Report report)
    {
        var (package, referenced) = definition.One("Behavior")?.ConstructorArguments.FirstOrDefault().Value switch
        {
            0 => ("MASS4.Graph.Dataflow", semantics.Dataflow),
            1 => ("MASS4.Graph.Control", semantics.Control),
            2 or 3 => ("MASS4.Graph.State", semantics.State),
            _ => definition.IsGuarded ? ("MASS4.Graph.State", semantics.State) : ("", true),
        };
        if (!referenced)
        {
            report(MissingSemantics, definition.Symbol.Locations[0], $"{definition.FullName} binds {package} semantics; add a reference to the {package} package.");
        }
    }

    static bool TryGuid(string? value, out Guid id) => Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;

    static DiagnosticDescriptor Descriptor(string id, string title) =>
        new(id, title, "{0}", "MASS4.Graph", DiagnosticSeverity.Error, true);
}
