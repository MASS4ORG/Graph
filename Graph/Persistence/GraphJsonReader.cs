namespace MASS4.Graph;

/// <summary>Outcome of loading a graph document.</summary>
/// <param name="Document">The loaded document, or null when the input is not a readable graph document.</param>
/// <param name="Diagnostics">Findings; unknown or unmigratable content is preserved and reported as warnings.</param>
public sealed record GraphLoadResult(GraphDocument? Document, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Whether loading failed or dropped malformed elements.</summary>
    public bool HasErrors => Document is null || Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error);
}

public static partial class GraphJson
{
    static readonly HashSet<string> RootProperties =
        ["format", "schemaVersion", "assetId", "variables", "nodes", "edges", "layout"];

    static readonly HashSet<string> NodeProperties = ["id", "type", "version", "ports", "payload"];

    /// <summary>Loads a document, resolving renamed types and migrating older nodes through the registry.</summary>
    public static GraphLoadResult Deserialize(string json, GraphTypeRegistry registry) =>
        Deserialize(System.Text.Encoding.UTF8.GetBytes(json), registry);

    /// <summary>Loads a document from UTF-8 JSON.</summary>
    public static GraphLoadResult Deserialize(ReadOnlyMemory<byte> utf8Json, GraphTypeRegistry registry)
    {
        var diagnostics = new DiagnosticBag();
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(utf8Json);
        }
        catch (JsonException exception)
        {
            diagnostics.Error(GraphDiagnosticIds.MalformedDocument, null,
                $"The document is not valid JSON: {exception.Message}");
            return new(null, [.. diagnostics]);
        }
        using (parsed)
        {
            var document = ReadRoot(parsed.RootElement, registry, diagnostics);
            return new(document, [.. diagnostics]);
        }
    }

    /// <summary>Applies layout written by <see cref="SerializeLayout"/> to a loaded document.</summary>
    public static IReadOnlyList<Diagnostic> DeserializeLayout(GraphDocument document, string json)
    {
        var diagnostics = new DiagnosticBag();
        try
        {
            using var parsed = JsonDocument.Parse(json);
            ReadLayout(document, parsed.RootElement, diagnostics);
        }
        catch (JsonException exception)
        {
            diagnostics.Error(GraphDiagnosticIds.MalformedDocument, null,
                $"The layout is not valid JSON: {exception.Message}");
        }
        return [.. diagnostics];
    }

    static GraphDocument? ReadRoot(JsonElement root, GraphTypeRegistry registry, DiagnosticBag diagnostics)
    {
        if (!TryReadHeader(root, diagnostics, out var assetId))
        {
            return null;
        }
        var document = new GraphDocument(registry, assetId);
        ReadEach(root, "variables", diagnostics, element => ReadVariable(document, element, diagnostics));
        ReadEach(root, "nodes", diagnostics, element => ReadNode(document, element, diagnostics));
        ReadEach(root, "edges", diagnostics, element => ReadEdge(document, element, diagnostics));
        if (root.TryGetProperty("layout", out var layout))
        {
            ReadLayout(document, layout, diagnostics);
        }
        document.Extensions = ReadExtensions(root, RootProperties);
        return document;
    }

    static bool TryReadHeader(JsonElement root, DiagnosticBag diagnostics, out StableId assetId)
    {
        assetId = default;
        if (root.ValueKind != JsonValueKind.Object || OptionalString(root, "format") != FormatName)
        {
            diagnostics.Error(GraphDiagnosticIds.UnsupportedSchema, null,
                $"The document is not a '{FormatName}' document.");
            return false;
        }
        if (!root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) ||
            version is < 1 or > SchemaVersion)
        {
            diagnostics.Error(GraphDiagnosticIds.UnsupportedSchema, null,
                $"Only schema versions 1 to {SchemaVersion} are supported.");
            return false;
        }
        if (!StableId.TryParse(OptionalString(root, "assetId"), out assetId))
        {
            diagnostics.Error(GraphDiagnosticIds.MalformedDocument, null, "The document has no valid 'assetId'.");
            return false;
        }
        return true;
    }

    static void ReadVariable(GraphDocument document, JsonElement element, DiagnosticBag diagnostics)
    {
        var id = RequireElementId(element, "id");
        var typeId = document.Registry.PortTypes.Canonicalize(RequireStableId(element, "type"));
        var scope = ParseScope(RequireString(element, "scope"));
        var serializer = document.Registry.PortTypes.TryResolve(typeId, out var type) ? type.ValueSerializer : null;
        var value = element.TryGetProperty("default", out var saved)
            ? ReadValue(saved, serializer, id, diagnostics)
            : null;
        if (document.ContainsElement(id))
        {
            diagnostics.Error(GraphDiagnosticIds.DuplicateElement, id, "The variable identity is used twice; dropped.");
            return;
        }
        document.SetVariableCore(new(id, RequireString(element, "name"), typeId, scope) { DefaultValue = value });
    }

    static VariableScope ParseScope(string scope) => scope switch
    {
        "local" => VariableScope.Local,
        "input" => VariableScope.Input,
        "output" => VariableScope.Output,
        "external" => VariableScope.External,
        _ => throw new GraphFormatException($"Unknown variable scope '{scope}'."),
    };

    static void ReadNode(GraphDocument document, JsonElement element, DiagnosticBag diagnostics)
    {
        var registry = document.Registry;
        var id = RequireElementId(element, "id");
        var typeId = registry.NodeTypes.Canonicalize(RequireStableId(element, "type"));
        var version = element.TryGetProperty("version", out var saved) && saved.TryGetInt32(out var number)
            ? number
            : 1;
        JsonDocument? migrated = null;
        try
        {
            var current = ResolveCurrentType(registry, typeId, id, ref element, ref version, diagnostics, out migrated);
            var ports = ReadPorts(element, id, current, registry, diagnostics);
            var payload = element.TryGetProperty("payload", out var value)
                ? ReadValue(value, current?.PayloadSerializer, id, diagnostics)
                : null;
            if (document.ContainsElement(id) || ports.Any(port => document.ContainsElement(port.Id)))
            {
                diagnostics.Error(GraphDiagnosticIds.DuplicateElement, id,
                    "The node or a port identity is used twice; dropped.");
                return;
            }
            document.AddNodeCore(new(id, typeId, version, ports, payload)
            {
                Extensions = ReadExtensions(element, NodeProperties),
            });
        }
        finally
        {
            migrated?.Dispose();
        }
    }

    /// <summary>
    /// Returns the registered type when the node can be read at its current version, migrating it when needed; null
    /// keeps the saved node as is.
    /// </summary>
    static NodeType? ResolveCurrentType(
        GraphTypeRegistry registry,
        StableId typeId,
        ElementId id,
        ref JsonElement element,
        ref int version,
        DiagnosticBag diagnostics,
        out JsonDocument? migrated)
    {
        migrated = null;
        if (!registry.NodeTypes.TryResolve(typeId, out var type))
        {
            diagnostics.Warning(GraphDiagnosticIds.UnknownNodeType, id,
                $"Node type {typeId} is not registered; the node is preserved unchanged.");
            return null;
        }
        if (version > type.Version)
        {
            diagnostics.Warning(GraphDiagnosticIds.PayloadUnreadable, id,
                $"'{type.DisplayName}' was saved at version {version}, newer than {type.Version}; preserved.");
            return null;
        }
        if (version < type.Version)
        {
            if (!TryMigrate(registry, type, element, version, id, diagnostics, out migrated))
            {
                return null;
            }
            element = migrated.RootElement;
            version = type.Version;
        }
        return type;
    }

    static bool TryMigrate(
        GraphTypeRegistry registry,
        NodeType type,
        JsonElement element,
        int version,
        ElementId id,
        DiagnosticBag diagnostics,
        [NotNullWhen(true)] out JsonDocument? migrated)
    {
        migrated = null;
        var node = JsonObject.Create(element)!;
        for (var step = version; step < type.Version; step++)
        {
            if (!registry.TryGetMigration(type.Id, step, out var migration))
            {
                diagnostics.Warning(GraphDiagnosticIds.MigrationUnavailable, id,
                    $"'{type.DisplayName}' has no migration from version {step}; the node is preserved unchanged.");
                return false;
            }
            try
            {
                node = migration.Migrate(node);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException
                                                  or KeyNotFoundException or FormatException)
            {
                diagnostics.Warning(GraphDiagnosticIds.MigrationUnavailable, id,
                    $"Migrating '{type.DisplayName}' from version {step} failed ({exception.Message}); preserved.");
                return false;
            }
        }
        node["type"] = type.Id.ToString();
        node["version"] = type.Version;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            node.WriteTo(writer);
        }
        migrated = JsonDocument.Parse(buffer.WrittenMemory);
        return true;
    }

    static GraphPort[] ReadPorts(
        JsonElement element,
        ElementId nodeId,
        NodeType? current,
        GraphTypeRegistry registry,
        DiagnosticBag diagnostics)
    {
        List<GraphPort> saved = [];
        if (element.TryGetProperty("ports", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var port in array.EnumerateArray())
            {
                saved.Add(new(RequireElementId(port, "id"), nodeId, ReadDeclaration(port, registry)));
            }
        }
        return current is null ? [.. saved] : Reconcile(saved, current, nodeId, diagnostics);
    }

    /// <summary>
    /// Takes declarations of a known type from the registry while keeping saved port identities by key, so edges and
    /// ports the type no longer declares survive type changes.
    /// </summary>
    static GraphPort[] Reconcile(List<GraphPort> saved, NodeType current, ElementId nodeId, DiagnosticBag diagnostics)
    {
        Dictionary<StableId, GraphPort> byKey = [];
        foreach (var port in saved)
        {
            if (!byKey.TryAdd(port.Declaration.Key, port))
            {
                throw new GraphFormatException($"Port key {port.Declaration.Key} appears twice.");
            }
        }
        List<GraphPort> ports = [];
        foreach (var declaration in current.Ports)
        {
            var portId = byKey.Remove(declaration.Key, out var match) ? match.Id : ElementId.New();
            ports.Add(new(portId, nodeId, declaration));
        }
        foreach (var extra in saved.Where(port => byKey.ContainsKey(port.Declaration.Key)))
        {
            diagnostics.Warning(GraphDiagnosticIds.PortNotFound, extra.Id,
                $"'{current.DisplayName}' no longer declares port '{extra.Declaration.Name}'; it is preserved.");
            ports.Add(extra);
        }
        return [.. ports];
    }

    static PortDeclaration ReadDeclaration(JsonElement port, GraphTypeRegistry registry)
    {
        var direction = RequireString(port, "direction") switch
        {
            "input" => PortDirection.Input,
            "output" => PortDirection.Output,
            var other => throw new GraphFormatException($"Unknown port direction '{other}'."),
        };
        var multiplicity = RequireString(port, "multiplicity") switch
        {
            "single" => PortMultiplicity.Single,
            "multiple" => PortMultiplicity.Multiple,
            var other => throw new GraphFormatException($"Unknown port multiplicity '{other}'."),
        };
        var typeId = registry.PortTypes.Canonicalize(RequireStableId(port, "type"));
        return new(RequireStableId(port, "key"), RequireString(port, "name"), direction, typeId, multiplicity);
    }

    static void ReadEdge(GraphDocument document, JsonElement element, DiagnosticBag diagnostics)
    {
        var edge = new GraphEdge(
            RequireElementId(element, "id"),
            RequireElementId(element, "source"),
            RequireElementId(element, "target"));
        if (!document.TryGetPort(edge.Source, out _) || !document.TryGetPort(edge.Target, out _))
        {
            diagnostics.Error(GraphDiagnosticIds.PortNotFound, edge.Id, "The edge references a missing port; dropped.");
            return;
        }
        if (document.ContainsElement(edge.Id))
        {
            diagnostics.Error(GraphDiagnosticIds.DuplicateElement, edge.Id,
                "The edge identity is used twice; dropped.");
            return;
        }
        document.AddEdgeCore(edge);
    }

    static void ReadLayout(GraphDocument document, JsonElement layout, DiagnosticBag diagnostics) =>
        ReadEach(layout, "positions", diagnostics, element =>
        {
            var node = RequireElementId(element, "node");
            if (!document.TryGetNode(node, out _))
            {
                diagnostics.Warning(GraphDiagnosticIds.NodeNotFound, node,
                    "A position refers to a missing node; dropped.");
                return;
            }
            document.SetPositionCore(node, new(RequireDouble(element, "x"), RequireDouble(element, "y")));
        });

    static object? ReadValue(
        JsonElement value,
        IValueSerializer? serializer,
        ElementId owner,
        DiagnosticBag diagnostics)
    {
        if (serializer is null)
        {
            return new UnknownPayload(value);
        }
        try
        {
            return serializer.Read(value);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException
                                              or NotSupportedException or ArgumentException)
        {
            diagnostics.Warning(GraphDiagnosticIds.PayloadUnreadable, owner,
                $"The saved value could not be read ({exception.Message}); it is preserved unchanged.");
            return new UnknownPayload(value);
        }
    }

    static void ReadEach(JsonElement parent, string name, DiagnosticBag diagnostics, Action<JsonElement> read)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var array))
        {
            return;
        }
        if (array.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Error(GraphDiagnosticIds.MalformedDocument, null, $"'{name}' must be an array.");
            return;
        }
        foreach (var element in array.EnumerateArray())
        {
            try
            {
                read(element);
            }
            catch (GraphFormatException exception)
            {
                ElementId? id = ElementId.TryParse(OptionalString(element, "id"), out var parsed) ? parsed : null;
                diagnostics.Error(GraphDiagnosticIds.MalformedDocument, id,
                    $"A '{name}' entry was dropped: {exception.Message}");
            }
        }
    }

    static IReadOnlyDictionary<string, JsonElement> ReadExtensions(JsonElement element, HashSet<string> known)
    {
        Dictionary<string, JsonElement>? extensions = null;
        foreach (var property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                (extensions ??= [])[property.Name] = property.Value.Clone();
            }
        }
        return extensions ?? GraphNode.EmptyExtensions;
    }

    static string? OptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static string RequireString(JsonElement element, string name) =>
        OptionalString(element, name) ?? throw new GraphFormatException($"Missing string property '{name}'.");

    static ElementId RequireElementId(JsonElement element, string name) =>
        ElementId.TryParse(RequireString(element, name), out var id)
            ? id
            : throw new GraphFormatException($"'{name}' is not a valid identity.");

    static StableId RequireStableId(JsonElement element, string name) =>
        StableId.TryParse(RequireString(element, name), out var id)
            ? id
            : throw new GraphFormatException($"'{name}' is not a valid identity.");

    static double RequireDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
            ? number
            : throw new GraphFormatException($"Missing number property '{name}'.");

    sealed class GraphFormatException(string message) : Exception(message);
}
