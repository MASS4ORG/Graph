namespace MASS4.Graph.Dataflow;

/// <summary>
/// Pull-based dataflow: node outputs are computed on demand from their inputs, cached per instance, and data edges
/// must not form cycles. Only ports whose types are registered here (or wildcards) carry data, so other semantics in
/// the same document keep their own edges, including cyclic ones.
/// </summary>
public sealed class DataflowSemantics
{
    readonly Dictionary<StableId, DataflowValueType> valueTypes = [];
    readonly Dictionary<(StableId Source, StableId Target), Coercion> coercions = [];
    readonly Dictionary<StableId, IDataflowNode> behaviors = [];

    /// <summary>Creates dataflow semantics with only the built-in variable-read node bound.</summary>
    public DataflowSemantics()
    {
        Rules =
        [
            new DataflowAcyclicRule(this), new DataflowInputRule(this), new DataflowInferenceRule(this),
            new DataflowVariableRule(this),
        ];
        AddNode(DataflowNodes.GetVariable.Id, VariableNode.Instance);
    }

    /// <summary>Compatibility strategy: exact types, wildcards, registered coercions and conversion nodes.</summary>
    public ConversionTable Conversions { get; } = new();

    /// <summary>Rules editors run on every change: acyclic edges, single inputs, inference, variable reads.</summary>
    public IReadOnlyList<IGraphRule> Rules { get; }

    /// <summary>Registers a port type whose values are stored inline as <typeparamref name="T"/>.</summary>
    /// <exception cref="ArgumentException">The port type is already registered.</exception>
    public DataflowSemantics AddValueType<T>(StableId portTypeId) where T : unmanaged =>
        AddType(new UnmanagedValueType<T>(portTypeId));

    /// <summary>Registers a port type whose values are object references of type <typeparamref name="T"/>.</summary>
    /// <exception cref="ArgumentException">The port type is already registered.</exception>
    public DataflowSemantics AddReferenceType<T>(StableId portTypeId) where T : class =>
        AddType(new ReferenceValueType<T>(portTypeId));

    /// <summary>
    /// Registers an implicit coercion: edges from <paramref name="source"/> to <paramref name="target"/> ports become
    /// valid and convert values at evaluation time.
    /// </summary>
    /// <exception cref="ArgumentException">A type is not registered with the matching CLR type.</exception>
    public DataflowSemantics AddCoercion<TFrom, TTo>(StableId source, StableId target, Func<TFrom, TTo> convert)
        where TFrom : unmanaged where TTo : unmanaged
    {
        RequireType<TFrom>(source);
        RequireType<TTo>(target);
        coercions[(source, target)] = new Coercion<TFrom, TTo>(convert);
        Conversions.AddImplicit(source, target);
        return this;
    }

    /// <summary>Binds a behavior to a node type.</summary>
    /// <exception cref="ArgumentException">The node type already has a behavior.</exception>
    public DataflowSemantics AddNode(StableId nodeTypeId, IDataflowNode behavior)
    {
        if (!behaviors.TryAdd(nodeTypeId, behavior))
        {
            throw new ArgumentException($"Node type {nodeTypeId} already has a dataflow behavior.", nameof(nodeTypeId));
        }
        return this;
    }

    /// <summary>Binds a conversion node type that editors insert between incompatible ports.</summary>
    /// <exception cref="ArgumentException">The type is not a valid conversion or already has a behavior.</exception>
    public DataflowSemantics AddConversionNode(NodeType conversion, IDataflowNode behavior)
    {
        Conversions.AddConversion(conversion);
        return AddNode(conversion.Id, behavior);
    }

    /// <summary>Finds the stored type of a port type.</summary>
    public bool TryGetValueType(StableId portTypeId, [NotNullWhen(true)] out DataflowValueType? type) =>
        valueTypes.TryGetValue(portTypeId, out type);

    /// <summary>Finds the behavior bound to a node type.</summary>
    public bool TryGetBehavior(StableId nodeTypeId, [NotNullWhen(true)] out IDataflowNode? behavior) =>
        behaviors.TryGetValue(nodeTypeId, out behavior);

    /// <summary>Whether a port carries data: its type is registered here or is a wildcard.</summary>
    public bool IsDataPort(GraphDocument document, GraphPort port) =>
        valueTypes.ContainsKey(port.TypeId) ||
        (document.Registry.PortTypes.TryResolve(port.TypeId, out var type) && type.IsWildcard);

    /// <summary>
    /// Validates a document and builds its per-instance program, reserving slots in <paramref name="layout"/> so
    /// other semantics can share the same instance records.
    /// </summary>
    /// <returns>The program, or null when the document has errors.</returns>
    public DataflowProgram? Compile(GraphDocument document, InstanceLayoutBuilder layout, DiagnosticBag diagnostics) =>
        DataflowCompiler.Compile(this, document, layout, diagnostics);

    internal bool TryGetCoercion(StableId source, StableId target, [NotNullWhen(true)] out Coercion? coercion) =>
        coercions.TryGetValue((source, target), out coercion);

    internal bool IsDataEdge(
        GraphDocument document,
        GraphEdge edge,
        [NotNullWhen(true)] out GraphPort? source,
        [NotNullWhen(true)] out GraphPort? target)
    {
        target = null;
        return document.TryGetPort(edge.Source, out source) && document.TryGetPort(edge.Target, out target) &&
               IsDataPort(document, source) && IsDataPort(document, target);
    }

    DataflowSemantics AddType(DataflowValueType type)
    {
        if (!valueTypes.TryAdd(type.PortTypeId, type))
        {
            throw new ArgumentException($"Port type {type.PortTypeId} is already registered.", nameof(type));
        }
        return this;
    }

    void RequireType<T>(StableId portTypeId)
    {
        if (!valueTypes.TryGetValue(portTypeId, out var type) || type.ClrType != typeof(T))
        {
            throw new ArgumentException($"Port type {portTypeId} is not registered as {typeof(T).Name}.");
        }
    }
}

/// <summary>Diagnostic codes reported by dataflow semantics.</summary>
public static class DataflowDiagnosticIds
{
    /// <summary>Data edges form a cycle.</summary>
    public const string DataCycle = "MG1001";

    /// <summary>A data input receives more than one edge.</summary>
    public const string MultipleDataEdges = "MG1002";

    /// <summary>Connected wildcard ports resolve to conflicting types.</summary>
    public const string TypeConflict = "MG1003";

    /// <summary>A connected wildcard port has no concrete type to infer.</summary>
    public const string TypeUnresolved = "MG1004";

    /// <summary>A data edge joins types with different storage and no registered coercion.</summary>
    public const string CoercionMissing = "MG1005";

    /// <summary>A variable-read node references a variable that does not exist.</summary>
    public const string VariableNotFound = "MG1006";

    /// <summary>A variable-read node references a variable whose type dataflow cannot carry.</summary>
    public const string VariableNotData = "MG1007";
}
