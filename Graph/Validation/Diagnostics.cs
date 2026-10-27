namespace MASS4.Graph;

/// <summary>Severity of a diagnostic; only errors block a mutation.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational.</summary>
    Info,

    /// <summary>Suspicious but allowed.</summary>
    Warning,

    /// <summary>Invalid; a mutation producing it is rolled back.</summary>
    Error,
}

/// <summary>
/// A validation, mutation or persistence finding in a shape shared by editors, command-line tools and CI.
/// </summary>
/// <param name="Id">Stable diagnostic code, such as <c>MG0204</c>; see <see cref="GraphDiagnosticIds"/>.</param>
/// <param name="Severity">How serious the finding is.</param>
/// <param name="ElementId">The affected element, or null for document-level findings.</param>
/// <param name="Message">User-facing reason.</param>
public sealed record Diagnostic(string Id, DiagnosticSeverity Severity, ElementId? ElementId, string Message)
{
    /// <summary>Formats the diagnostic like a compiler message.</summary>
    public override string ToString() =>
        ElementId is { } element
            ? $"{Severity.ToString().ToLowerInvariant()} {Id} [{element}]: {Message}"
            : $"{Severity.ToString().ToLowerInvariant()} {Id}: {Message}";
}

/// <summary>Collects diagnostics while a command, rule set or loader runs.</summary>
public sealed class DiagnosticBag : IReadOnlyList<Diagnostic>
{
    readonly List<Diagnostic> items = [];

    /// <summary>Whether any collected diagnostic is an error.</summary>
    public bool HasErrors { get; private set; }

    /// <inheritdoc />
    public int Count => items.Count;

    /// <inheritdoc />
    public Diagnostic this[int index] => items[index];

    /// <summary>Adds a diagnostic.</summary>
    public void Add(Diagnostic diagnostic)
    {
        items.Add(diagnostic);
        HasErrors |= diagnostic.Severity == DiagnosticSeverity.Error;
    }

    /// <summary>Adds an error.</summary>
    public void Error(string id, ElementId? elementId, string message) =>
        Add(new(id, DiagnosticSeverity.Error, elementId, message));

    /// <summary>Adds a warning.</summary>
    public void Warning(string id, ElementId? elementId, string message) =>
        Add(new(id, DiagnosticSeverity.Warning, elementId, message));

    /// <inheritdoc />
    public IEnumerator<Diagnostic> GetEnumerator() => items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Diagnostic codes reported by the graph core.</summary>
public static class GraphDiagnosticIds
{
    /// <summary>A referenced node does not exist.</summary>
    public const string NodeNotFound = "MG0001";

    /// <summary>A referenced port does not exist.</summary>
    public const string PortNotFound = "MG0002";

    /// <summary>A referenced edge does not exist.</summary>
    public const string EdgeNotFound = "MG0003";

    /// <summary>A referenced variable does not exist.</summary>
    public const string VariableNotFound = "MG0004";

    /// <summary>An element identity is already used.</summary>
    public const string DuplicateElement = "MG0005";

    /// <summary>A node cannot be created because its type is not registered.</summary>
    public const string NodeTypeNotRegistered = "MG0006";

    /// <summary>A node's type is not registered; the node is preserved unchanged.</summary>
    public const string UnknownNodeType = "MG0101";

    /// <summary>A port or variable type is not registered, so compatibility cannot be checked.</summary>
    public const string UnknownPortType = "MG0102";

    /// <summary>An edge does not run from an output to an input.</summary>
    public const string DirectionMismatch = "MG0201";

    /// <summary>A single-edge port would receive a second edge.</summary>
    public const string MultiplicityExceeded = "MG0202";

    /// <summary>The same two ports are already connected.</summary>
    public const string DuplicateEdge = "MG0203";

    /// <summary>The connected port types are incompatible.</summary>
    public const string IncompatiblePortTypes = "MG0204";

    /// <summary>The connected port types need an explicit conversion node.</summary>
    public const string ConversionRequired = "MG0205";

    /// <summary>The document uses an unsupported format or schema version.</summary>
    public const string UnsupportedSchema = "MG0301";

    /// <summary>The document is not well-formed.</summary>
    public const string MalformedDocument = "MG0302";

    /// <summary>A saved node could not be migrated to the current type version; it is preserved unchanged.</summary>
    public const string MigrationUnavailable = "MG0303";

    /// <summary>A saved value could not be read; it is preserved unchanged.</summary>
    public const string PayloadUnreadable = "MG0304";
}
