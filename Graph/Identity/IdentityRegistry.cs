namespace MASS4.Graph;

/// <summary>An entry addressable by a stable identity, such as a node type, port type or host function.</summary>
public interface ITypeIdentity
{
    /// <summary>The persistent identity written into saved graphs.</summary>
    StableId Id { get; }

    /// <summary>Human-readable name for tools and diagnostics; never used for resolution.</summary>
    string DisplayName { get; }
}

/// <summary>
/// Resolves stable identities to registered entries, following rename entries so that identities found in older
/// documents still resolve after a consumer renames or replaces a type.
/// </summary>
/// <typeparam name="T">The kind of entry.</typeparam>
public sealed class IdentityRegistry<T> where T : class, ITypeIdentity
{
    readonly Dictionary<StableId, T> entries = [];
    readonly Dictionary<StableId, StableId> renames = [];

    /// <summary>All registered entries, in no particular order.</summary>
    public IEnumerable<T> Entries => entries.Values;

    /// <summary>Number of registered entries.</summary>
    public int Count => entries.Count;

    /// <summary>Rename entries as previous → next identity, in no particular order; chains are not collapsed.</summary>
    public IEnumerable<KeyValuePair<StableId, StableId>> Renames => renames.AsReadOnly();

    /// <summary>Registers an entry under its own identity.</summary>
    /// <exception cref="ArgumentException">The identity is empty, registered, or recorded as renamed.</exception>
    public void Register(T entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Id.IsEmpty)
        {
            throw new ArgumentException($"'{entry.DisplayName}' has an empty identity.", nameof(entry));
        }
        if (renames.ContainsKey(entry.Id))
        {
            throw new ArgumentException($"{entry.Id} is recorded as renamed and cannot be registered.", nameof(entry));
        }
        if (!entries.TryAdd(entry.Id, entry))
        {
            throw new ArgumentException($"{entry.Id} is already registered.", nameof(entry));
        }
    }

    /// <summary>Records that <paramref name="previous"/> now resolves to <paramref name="current"/>.</summary>
    /// <exception cref="ArgumentException">The rename is empty, conflicts, or forms a cycle.</exception>
    public void AddRename(StableId previous, StableId current)
    {
        if (previous.IsEmpty || current.IsEmpty)
        {
            throw new ArgumentException("Renames cannot involve the empty identity.");
        }
        if (entries.ContainsKey(previous))
        {
            throw new ArgumentException($"{previous} is still registered and cannot be renamed.", nameof(previous));
        }
        if (Canonicalize(current) == previous)
        {
            throw new ArgumentException($"Renaming {previous} to {current} would form a cycle.", nameof(current));
        }
        if (!renames.TryAdd(previous, current))
        {
            throw new ArgumentException($"{previous} already has a rename entry.", nameof(previous));
        }
    }

    /// <summary>Follows rename entries until reaching an identity that is not renamed.</summary>
    public StableId Canonicalize(StableId id)
    {
        // Cycles are rejected by AddRename, so the chain always terminates.
        while (renames.TryGetValue(id, out var next))
        {
            id = next;
        }
        return id;
    }

    /// <summary>Resolves an identity, following renames, to its registered entry.</summary>
    public bool TryResolve(StableId id, [NotNullWhen(true)] out T? entry) =>
        entries.TryGetValue(Canonicalize(id), out entry);

    /// <summary>Resolves an identity or throws when it is not registered.</summary>
    /// <exception cref="KeyNotFoundException">The identity does not resolve.</exception>
    public T Resolve(StableId id) =>
        TryResolve(id, out var entry) ? entry : throw new KeyNotFoundException($"{id} is not registered.");
}
