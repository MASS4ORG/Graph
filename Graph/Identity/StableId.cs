namespace MASS4.Graph;

/// <summary>
/// Opaque persistent identity of a registered type or symbol. It is never derived from CLR type names, so renaming
/// code does not invalidate saved graphs.
/// </summary>
/// <param name="Value">The underlying GUID.</param>
public readonly record struct StableId(Guid Value) : IComparable<StableId>
{
    /// <summary>The empty identity, which no registry accepts.</summary>
    public static StableId Empty => default;

    /// <summary>Whether this identity is empty.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a new random identity.</summary>
    public static StableId New() => new(Guid.NewGuid());

    /// <summary>Parses the canonical hyphenated form produced by <see cref="ToString"/>.</summary>
    public static StableId Parse(string text) => new(Guid.ParseExact(text, "D"));

    /// <summary>Parses the canonical hyphenated form, returning false for anything else.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out StableId id)
    {
        var parsed = Guid.TryParseExact(text, "D", out var value);
        id = new(value);
        return parsed;
    }

    /// <inheritdoc />
    public int CompareTo(StableId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the canonical lowercase hyphenated form.</summary>
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Identity of an element inside one graph document (node, port, edge or variable). Element identities are persisted,
/// so saved documents never depend on array positions.
/// </summary>
/// <param name="Value">The underlying GUID.</param>
public readonly record struct ElementId(Guid Value) : IComparable<ElementId>
{
    /// <summary>Whether this identity is empty.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a new random element identity.</summary>
    public static ElementId New() => new(Guid.NewGuid());

    /// <summary>Parses the canonical hyphenated form produced by <see cref="ToString"/>.</summary>
    public static ElementId Parse(string text) => new(Guid.ParseExact(text, "D"));

    /// <summary>Parses the canonical hyphenated form, returning false for anything else.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out ElementId id)
    {
        var parsed = Guid.TryParseExact(text, "D", out var value);
        id = new(value);
        return parsed;
    }

    /// <inheritdoc />
    public int CompareTo(ElementId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the canonical lowercase hyphenated form.</summary>
    public override string ToString() => Value.ToString("D");
}
