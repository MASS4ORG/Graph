namespace MASS4.Graph;

/// <summary>Whether a port consumes or produces values.</summary>
public enum PortDirection
{
    /// <summary>The port receives connections from outputs.</summary>
    Input,

    /// <summary>The port connects to inputs.</summary>
    Output,
}

/// <summary>How many edges may attach to a port.</summary>
public enum PortMultiplicity
{
    /// <summary>At most one edge.</summary>
    Single,

    /// <summary>Any number of edges.</summary>
    Multiple,
}

/// <summary>
/// A registered value type that ports and variables carry. Consumers own their type sets; the core only defines the
/// identity and serialization contract.
/// </summary>
/// <param name="id">Persistent identity.</param>
/// <param name="displayName">Human-readable name.</param>
public sealed class PortType(StableId id, string displayName) : ITypeIdentity
{
    /// <inheritdoc />
    public StableId Id { get; } = id;

    /// <inheritdoc />
    public string DisplayName { get; } = displayName;

    /// <summary>Whether ports of this type accept any type, for generic nodes with inferred types.</summary>
    public bool IsWildcard { get; init; }

    /// <summary>Serializer for values of this type, used for variable defaults; null keeps values opaque.</summary>
    public IValueSerializer? ValueSerializer { get; init; }
}

/// <summary>A port as declared by a node type; instantiated once per node.</summary>
/// <param name="Key">Identity of the port within its node type; stable across renames of the port's name.</param>
/// <param name="Name">Display name.</param>
/// <param name="Direction">Input or output.</param>
/// <param name="TypeId">Identity of the carried <see cref="PortType"/>.</param>
/// <param name="Multiplicity">How many edges may attach.</param>
public sealed record PortDeclaration(
    StableId Key,
    string Name,
    PortDirection Direction,
    StableId TypeId,
    PortMultiplicity Multiplicity)
{
    /// <summary>Declares an input port, which accepts a single edge.</summary>
    public static PortDeclaration Input(StableId key, string name, StableId typeId) =>
        new(key, name, PortDirection.Input, typeId, PortMultiplicity.Single);

    /// <summary>Declares an output port, which fans out to any number of edges.</summary>
    public static PortDeclaration Output(StableId key, string name, StableId typeId) =>
        new(key, name, PortDirection.Output, typeId, PortMultiplicity.Multiple);
}

/// <summary>
/// Reads and writes values of one type as JSON. Implementations must not rely on reflection so that persistence stays
/// trimming- and AOT-safe; <see cref="JsonTypeInfoSerializer{T}"/> adapts source-generated metadata.
/// </summary>
public interface IValueSerializer
{
    /// <summary>Writes a value as a single JSON value.</summary>
    void Write(Utf8JsonWriter writer, object? value);

    /// <summary>Reads a value written by <see cref="Write"/> at the current version.</summary>
    object? Read(JsonElement element);
}

/// <summary>Serializes values through source-generated <see cref="System.Text.Json"/> metadata.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <param name="typeInfo">Metadata from a <c>JsonSerializerContext</c>.</param>
public sealed class JsonTypeInfoSerializer<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    : IValueSerializer
{
    /// <inheritdoc />
    public void Write(Utf8JsonWriter writer, object? value) => JsonSerializer.Serialize(writer, (T)value!, typeInfo);

    /// <inheritdoc />
    public object? Read(JsonElement element) => element.Deserialize(typeInfo);
}
