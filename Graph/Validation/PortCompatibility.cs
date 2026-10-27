namespace MASS4.Graph;

/// <summary>How an output type relates to an input type under a compatibility strategy.</summary>
public enum PortCompatibilityKind
{
    /// <summary>The types match, or one side is a wildcard.</summary>
    Exact,

    /// <summary>The semantics coerce the value at evaluation time; a direct edge is allowed.</summary>
    Implicit,

    /// <summary>A conversion node must sit between the ports.</summary>
    RequiresConversion,

    /// <summary>The ports cannot be connected.</summary>
    Incompatible,
}

/// <summary>Result of a compatibility check.</summary>
/// <param name="Kind">The relation between the types.</param>
/// <param name="Conversion">The node type to insert when a conversion is required.</param>
public readonly record struct PortCompatibility(PortCompatibilityKind Kind, NodeType? Conversion = null)
{
    /// <summary>Whether an edge may join the ports without an intermediate node.</summary>
    public bool AllowsDirectEdge => Kind is PortCompatibilityKind.Exact or PortCompatibilityKind.Implicit;
}

/// <summary>Decides whether an output type may feed an input type; each semantics selects its own strategy.</summary>
public interface IPortCompatibility
{
    /// <summary>Checks an output type against an input type.</summary>
    PortCompatibility Check(PortType source, PortType target);
}

/// <summary>Allows only identical types, plus wildcards on either side.</summary>
public sealed class ExactPortCompatibility : IPortCompatibility
{
    /// <summary>Shared instance.</summary>
    public static ExactPortCompatibility Instance { get; } = new();

    /// <inheritdoc />
    public PortCompatibility Check(PortType source, PortType target) =>
        source.Id == target.Id || source.IsWildcard || target.IsWildcard
            ? new(PortCompatibilityKind.Exact)
            : new(PortCompatibilityKind.Incompatible);
}

/// <summary>
/// Exact matching extended with declared implicit coercions and conversion nodes that <see cref="GraphEditor.Connect"/>
/// inserts automatically.
/// </summary>
public sealed class ConversionTable : IPortCompatibility
{
    readonly Dictionary<(StableId Source, StableId Target), PortCompatibility> pairs = [];

    /// <summary>Declares that the semantics coerce source values to the target type.</summary>
    public ConversionTable AddImplicit(StableId source, StableId target)
    {
        pairs[(source, target)] = new(PortCompatibilityKind.Implicit);
        return this;
    }

    /// <summary>Declares a conversion node type, which must have exactly one input and one output port.</summary>
    /// <exception cref="ArgumentException">The node type does not have the required port shape.</exception>
    public ConversionTable AddConversion(NodeType conversion)
    {
        var inputs = conversion.Ports.Where(static port => port.Direction == PortDirection.Input).ToArray();
        var outputs = conversion.Ports.Where(static port => port.Direction == PortDirection.Output).ToArray();
        if (inputs.Length != 1 || outputs.Length != 1)
        {
            throw new ArgumentException(
                $"Conversion '{conversion.DisplayName}' needs exactly one input and one output port.",
                nameof(conversion));
        }
        pairs[(inputs[0].TypeId, outputs[0].TypeId)] = new(PortCompatibilityKind.RequiresConversion, conversion);
        return this;
    }

    /// <inheritdoc />
    public PortCompatibility Check(PortType source, PortType target)
    {
        var exact = ExactPortCompatibility.Instance.Check(source, target);
        if (exact.Kind == PortCompatibilityKind.Exact)
        {
            return exact;
        }
        return pairs.TryGetValue((source.Id, target.Id), out var declared) ? declared : exact;
    }
}
