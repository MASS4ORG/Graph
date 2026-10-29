namespace MASS4.Graph;

/// <summary>
/// A titled frame around nodes. Groups form a forest: each group has at most one parent group and each node is a
/// direct member of at most one group, so a node's enclosing groups are its group and that group's ancestors. Groups
/// are authored document content but never semantic: validation and evaluation ignore them. Whether a group is drawn
/// collapsed is view state (see <see cref="GraphDocument.CollapsedGroups"/>), not part of the model.
/// </summary>
public sealed class GraphGroup
{
    readonly HashSet<ElementId> nodes = [];

    internal GraphGroup(ElementId id, string title, ElementId? parent)
    {
        Id = id;
        Title = title;
        Parent = parent;
    }

    /// <summary>Identity of the group within the document; it shares the element identity space.</summary>
    public ElementId Id { get; }

    /// <summary>Display title.</summary>
    public string Title { get; internal set; }

    /// <summary>The enclosing group, or null for a top-level group.</summary>
    public ElementId? Parent { get; internal set; }

    /// <summary>Nodes directly in this group, in no particular order; nested groups' nodes are not included.</summary>
    public IReadOnlyCollection<ElementId> Nodes => nodes;

    /// <summary>Saved properties this version does not understand, written back verbatim.</summary>
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; internal init; } = GraphNode.EmptyExtensions;

    internal void AddNode(ElementId node) => nodes.Add(node);

    internal void RemoveNode(ElementId node) => nodes.Remove(node);
}
