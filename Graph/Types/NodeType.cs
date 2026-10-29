namespace MASS4.Graph;

/// <summary>
/// A registered kind of node: its port declarations, payload serialization and schema version. Application data
/// attaches as a payload object, so domains never inherit framework node classes.
/// </summary>
public sealed class NodeType : ITypeIdentity
{
    readonly Dictionary<StableId, PortDeclaration> portsByKey;

    /// <summary>Creates a node type.</summary>
    /// <param name="id">Persistent identity.</param>
    /// <param name="displayName">Human-readable name.</param>
    /// <param name="ports">Port declarations in display order.</param>
    /// <exception cref="ArgumentException">Two ports share a key.</exception>
    public NodeType(StableId id, string displayName, IReadOnlyList<PortDeclaration> ports)
    {
        Id = id;
        DisplayName = displayName;
        Ports = ports;
        portsByKey = new(ports.Count);
        foreach (var port in ports)
        {
            if (!portsByKey.TryAdd(port.Key, port))
            {
                throw new ArgumentException(
                    $"Node type '{displayName}' declares port {port.Key} twice.", nameof(ports));
            }
        }
    }

    /// <inheritdoc />
    public StableId Id { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <summary>Port declarations in display order.</summary>
    public IReadOnlyList<PortDeclaration> Ports { get; }

    /// <summary>Current payload schema version; older saved nodes are upgraded through registered migrations.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Serializer for the node payload; null keeps saved payloads opaque.</summary>
    public IValueSerializer? PayloadSerializer { get; init; }

    /// <summary>
    /// Creates the payload of newly added nodes when the command supplies none, as palettes and generated builders
    /// do; null leaves the payload empty. Loading never applies it.
    /// </summary>
    public Func<object?>? DefaultPayload { get; init; }

    /// <summary>Finds a declared port by key.</summary>
    public bool TryGetPort(StableId key, [NotNullWhen(true)] out PortDeclaration? port) =>
        portsByKey.TryGetValue(key, out port);
}

/// <summary>A typed function the host exposes to graphs, such as an automation or scripting library entry.</summary>
/// <param name="id">Persistent identity.</param>
/// <param name="displayName">Human-readable name.</param>
/// <param name="parameterTypes">Port type identities of the parameters, in order.</param>
/// <param name="resultType">Port type identity of the result, or null when the function returns nothing.</param>
public sealed class HostFunction(
    StableId id,
    string displayName,
    IReadOnlyList<StableId> parameterTypes,
    StableId? resultType) : ITypeIdentity
{
    /// <inheritdoc />
    public StableId Id { get; } = id;

    /// <inheritdoc />
    public string DisplayName { get; } = displayName;

    /// <summary>Port type identities of the parameters, in order.</summary>
    public IReadOnlyList<StableId> ParameterTypes { get; } = parameterTypes;

    /// <summary>Port type identity of the result, or null when the function returns nothing.</summary>
    public StableId? ResultType { get; } = resultType;
}

/// <summary>
/// A typed event hosts raise and graphs react to, such as a state-machine trigger. Signals are identities like node
/// and port types, so graphs never match events by string and renames are registry entries.
/// </summary>
/// <param name="id">Persistent identity.</param>
/// <param name="displayName">Human-readable name.</param>
public sealed class SignalType(StableId id, string displayName) : ITypeIdentity
{
    /// <inheritdoc />
    public StableId Id { get; } = id;

    /// <inheritdoc />
    public string DisplayName { get; } = displayName;
}

/// <summary>The value kinds an annotation can hold; a closed, format-neutral set.</summary>
public enum AnnotationKind
{
    /// <summary>A registered signal identity (<see cref="StableId"/>), resolved through renames on load.</summary>
    Signal,

    /// <summary>A 32-bit integer.</summary>
    Integer,

    /// <summary>A double-precision number.</summary>
    Number,

    /// <summary>A boolean.</summary>
    Boolean,

    /// <summary>A string.</summary>
    Text,
}

/// <summary>
/// A registered kind of node metadata that the semantics owning it consume when compiling, such as a transition's
/// trigger or priority. Annotations are never evaluation inputs and never a substitute for a payload or a port: a
/// domain that needs evaluation data declares it as such.
/// </summary>
/// <param name="id">Persistent identity.</param>
/// <param name="displayName">Human-readable name.</param>
/// <param name="kind">The value kind.</param>
public sealed class AnnotationType(StableId id, string displayName, AnnotationKind kind) : ITypeIdentity
{
    /// <inheritdoc />
    public StableId Id { get; } = id;

    /// <inheritdoc />
    public string DisplayName { get; } = displayName;

    /// <summary>The value kind.</summary>
    public AnnotationKind Kind { get; } = kind;

    /// <summary>Whether a value has this annotation's kind.</summary>
    public bool Accepts(object? value) => (Kind, value) switch
    {
        (AnnotationKind.Signal, StableId) => true,
        (AnnotationKind.Integer, int) => true,
        (AnnotationKind.Number, double) => true,
        (AnnotationKind.Boolean, bool) => true,
        (AnnotationKind.Text, string) => true,
        _ => false,
    };
}

/// <summary>Upgrades saved nodes of one type from one payload version to the next.</summary>
/// <param name="NodeTypeId">Current identity of the node type.</param>
/// <param name="FromVersion">The version this step reads; it produces <c>FromVersion + 1</c>.</param>
/// <param name="Migrate">Rewrites the saved node object, including its payload and ports.</param>
public sealed record NodeMigration(StableId NodeTypeId, int FromVersion, Func<JsonObject, JsonObject> Migrate);

/// <summary>A unit of type registrations, typically emitted by the discovery source generator.</summary>
public interface IGraphTypeModule
{
    /// <summary>Adds this module's types, renames and migrations to the registry.</summary>
    void Register(GraphTypeRegistry registry);
}

/// <summary>
/// All identities a graph document resolves against: node types, port types, signals and host functions, each with
/// its own rename entries.
/// </summary>
public sealed class GraphTypeRegistry
{
    readonly Dictionary<(StableId NodeType, int FromVersion), NodeMigration> migrations = [];

    /// <summary>Registered node types.</summary>
    public IdentityRegistry<NodeType> NodeTypes { get; } = new();

    /// <summary>Registered port and variable value types.</summary>
    public IdentityRegistry<PortType> PortTypes { get; } = new();

    /// <summary>Registered signals, such as state-machine triggers.</summary>
    public IdentityRegistry<SignalType> Signals { get; } = new();

    /// <summary>Registered node annotation types, such as a transition's trigger and priority.</summary>
    public IdentityRegistry<AnnotationType> Annotations { get; } = new();

    /// <summary>Host-provided functions graphs may call.</summary>
    public IdentityRegistry<HostFunction> HostFunctions { get; } = new();

    /// <summary>Registered payload migration steps, in no particular order.</summary>
    public IEnumerable<NodeMigration> Migrations => migrations.Values;

    /// <summary>Adds a module's registrations.</summary>
    public GraphTypeRegistry Add(IGraphTypeModule module)
    {
        module.Register(this);
        return this;
    }

    /// <summary>Registers a payload migration step.</summary>
    /// <exception cref="ArgumentException">A step for the same type and version already exists.</exception>
    public void AddMigration(NodeMigration migration)
    {
        if (!migrations.TryAdd((migration.NodeTypeId, migration.FromVersion), migration))
        {
            throw new ArgumentException(
                $"A migration from version {migration.FromVersion} of {migration.NodeTypeId} already exists.",
                nameof(migration));
        }
    }

    /// <summary>Finds the migration step reading <paramref name="fromVersion"/> of a node type.</summary>
    public bool TryGetMigration(
        StableId nodeTypeId,
        int fromVersion,
        [NotNullWhen(true)] out NodeMigration? migration) =>
        migrations.TryGetValue((nodeTypeId, fromVersion), out migration);
}
