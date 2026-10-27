using System.Text.Encodings.Web;

namespace MASS4.Graph;

/// <summary>Options for writing a graph document.</summary>
public sealed record GraphWriteOptions
{
    /// <summary>Whether the default view layout is embedded; omit it for source-controlled assets.</summary>
    public bool IncludeLayout { get; init; } = true;

    /// <summary>Whether output is indented, one property per line, for merge-friendly diffs.</summary>
    public bool Indented { get; init; } = true;
}

/// <summary>
/// Reads and writes graph documents as versioned JSON. Output is deterministic: elements are ordered by identity, so
/// identical documents produce identical bytes and concurrent edits merge as independent lines.
/// </summary>
public static partial class GraphJson
{
    /// <summary>Value of the <c>format</c> property identifying graph documents.</summary>
    public const string FormatName = "mass4.graph";

    /// <summary>Document schema version this library writes and the newest it reads.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Writes a document to a string.</summary>
    /// <exception cref="InvalidOperationException">A non-null value has no serializer to write it.</exception>
    public static string Serialize(GraphDocument document, GraphWriteOptions? options = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Serialize(buffer, document, options);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Writes a document as UTF-8 JSON.</summary>
    /// <exception cref="InvalidOperationException">A non-null value has no serializer to write it.</exception>
    public static void Serialize(IBufferWriter<byte> output, GraphDocument document, GraphWriteOptions? options = null)
    {
        options ??= new();
        using var writer = CreateWriter(output, options.Indented);
        writer.WriteStartObject();
        writer.WriteString("format", FormatName);
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("assetId", document.AssetId.ToString());
        WriteElements(writer, document);
        if (options.IncludeLayout && document.Positions.Count > 0)
        {
            writer.WritePropertyName("layout");
            WriteLayoutObject(writer, document);
        }
        WriteExtensions(writer, document.Extensions);
        writer.WriteEndObject();
    }

    /// <summary>Writes only the default view layout, for storing view state beside the asset.</summary>
    public static string SerializeLayout(GraphDocument document, GraphWriteOptions? options = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = CreateWriter(buffer, (options ?? new()).Indented))
        {
            WriteLayoutObject(writer, document);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static void WriteElements(Utf8JsonWriter writer, GraphDocument document)
    {
        writer.WriteStartArray("variables");
        foreach (var variable in document.Variables.OrderBy(static variable => variable.Id))
        {
            WriteVariable(writer, document, variable);
        }
        writer.WriteEndArray();
        writer.WriteStartArray("nodes");
        foreach (var node in document.Nodes.OrderBy(static node => node.Id))
        {
            WriteNode(writer, document, node);
        }
        writer.WriteEndArray();
        writer.WriteStartArray("edges");
        foreach (var edge in document.Edges.OrderBy(static edge => edge.Id))
        {
            writer.WriteStartObject();
            writer.WriteString("id", edge.Id.ToString());
            writer.WriteString("source", edge.Source.ToString());
            writer.WriteString("target", edge.Target.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    static Utf8JsonWriter CreateWriter(IBufferWriter<byte> output, bool indented) =>
        new(output, new()
        {
            Indented = indented,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    static void WriteVariable(Utf8JsonWriter writer, GraphDocument document, GraphVariable variable)
    {
        writer.WriteStartObject();
        writer.WriteString("id", variable.Id.ToString());
        writer.WriteString("name", variable.Name);
        writer.WriteString("type", variable.TypeId.ToString());
        writer.WriteString("scope", ScopeName(variable.Scope));
        if (variable.DefaultValue is not null)
        {
            writer.WritePropertyName("default");
            var serializer = document.Registry.PortTypes.TryResolve(variable.TypeId, out var type)
                ? type.ValueSerializer
                : null;
            WriteValue(writer, variable.DefaultValue, serializer, variable.Name);
        }
        writer.WriteEndObject();
    }

    static void WriteNode(Utf8JsonWriter writer, GraphDocument document, GraphNode node)
    {
        writer.WriteStartObject();
        writer.WriteString("id", node.Id.ToString());
        writer.WriteString("type", node.TypeId.ToString());
        writer.WriteNumber("version", node.TypeVersion);
        writer.WriteStartArray("ports");
        foreach (var port in node.Ports)
        {
            var declaration = port.Declaration;
            writer.WriteStartObject();
            writer.WriteString("id", port.Id.ToString());
            writer.WriteString("key", declaration.Key.ToString());
            writer.WriteString("name", declaration.Name);
            writer.WriteString("direction", declaration.Direction == PortDirection.Input ? "input" : "output");
            writer.WriteString("type", declaration.TypeId.ToString());
            var single = declaration.Multiplicity == PortMultiplicity.Single;
            writer.WriteString("multiplicity", single ? "single" : "multiple");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (node.Payload is not null)
        {
            writer.WritePropertyName("payload");
            var serializer = document.TryGetNodeType(node, out var type) ? type.PayloadSerializer : null;
            WriteValue(writer, node.Payload, serializer, $"node {node.Id}");
        }
        WriteExtensions(writer, node.Extensions);
        writer.WriteEndObject();
    }

    static void WriteValue(Utf8JsonWriter writer, object value, IValueSerializer? serializer, string owner)
    {
        if (value is UnknownPayload unknown)
        {
            unknown.Json.WriteTo(writer);
        }
        else if (serializer is not null)
        {
            serializer.Write(writer, value);
        }
        else
        {
            throw new InvalidOperationException($"The value of {owner} has no serializer and cannot be saved.");
        }
    }

    static void WriteLayoutObject(Utf8JsonWriter writer, GraphDocument document)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("positions");
        foreach (var (node, point) in document.Positions.OrderBy(static pair => pair.Key))
        {
            writer.WriteStartObject();
            writer.WriteString("node", node.ToString());
            writer.WriteNumber("x", point.X);
            writer.WriteNumber("y", point.Y);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    static void WriteExtensions(Utf8JsonWriter writer, IReadOnlyDictionary<string, JsonElement> extensions)
    {
        foreach (var (name, value) in extensions.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }
    }

    static string ScopeName(VariableScope scope) => scope switch
    {
        VariableScope.Input => "input",
        VariableScope.Output => "output",
        VariableScope.External => "external",
        _ => "local",
    };
}
