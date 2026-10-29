namespace MASS4.Graph;

/// <summary>
/// Adds a group, optionally inside another group, and moves nodes into it. Nodes already in a group leave it, so
/// grouping part of a group's nodes nests them in a new inner group.
/// </summary>
/// <param name="title">Display title.</param>
/// <param name="parent">The enclosing group, or null for a top-level group.</param>
/// <param name="nodes">Nodes to move into the group.</param>
/// <param name="groupId">Identity to use; a new one is generated when empty.</param>
public sealed class AddGroupCommand(
    string title,
    ElementId? parent = null,
    IReadOnlyList<ElementId>? nodes = null,
    ElementId groupId = default) : IGraphCommand
{
    ElementId?[] previous = [];

    /// <inheritdoc />
    public string Description => "Add group";

    /// <summary>The group this command adds.</summary>
    public ElementId GroupId { get; } = groupId.IsEmpty ? ElementId.New() : groupId;

    /// <summary>Display title.</summary>
    public string Title { get; } = title;

    /// <summary>The enclosing group, or null.</summary>
    public ElementId? Parent { get; } = parent;

    /// <summary>Nodes moved into the group.</summary>
    public IReadOnlyList<ElementId> Nodes { get; } = nodes ?? [];

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        if (graph.ContainsElement(GroupId))
        {
            diagnostics.Error(GraphDiagnosticIds.DuplicateElement, GroupId, "The identity belongs to another element.");
            return false;
        }
        if (!GroupRules.ParentExists(graph, Parent, GroupId, diagnostics) ||
            !GroupRules.NodesExist(graph, Nodes, diagnostics))
        {
            return false;
        }
        graph.AddGroupCore(new(GroupId, Title, Parent));
        previous = GroupRules.Move(graph, Nodes, GroupId);
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph)
    {
        GroupRules.Restore(graph, Nodes, previous);
        if (graph.TryGetGroup(GroupId, out var group))
        {
            graph.RemoveGroupCore(group);
        }
    }
}

/// <summary>
/// Removes a group without removing what it holds: its nodes and child groups move to its parent, or to the top level.
/// Undo restores the group, its membership and its collapsed state.
/// </summary>
/// <param name="groupId">The group to remove.</param>
public sealed class RemoveGroupCommand(ElementId groupId) : IGraphCommand
{
    GraphGroup? removed;
    ElementId[] members = [];
    GraphGroup[] children = [];
    bool wasCollapsed;

    /// <inheritdoc />
    public string Description => "Ungroup";

    /// <summary>The group to remove.</summary>
    public ElementId GroupId { get; } = groupId;

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        if (!graph.TryGetGroup(GroupId, out var group))
        {
            diagnostics.Error(GraphDiagnosticIds.GroupNotFound, GroupId, "The group does not exist.");
            return false;
        }
        removed = group;
        members = [.. group.Nodes];
        children = [.. graph.ChildGroups(GroupId)];
        wasCollapsed = graph.CollapsedGroups.Contains(GroupId);
        GroupRules.Move(graph, members, group.Parent);
        foreach (var child in children)
        {
            graph.SetGroupParentCore(child, group.Parent);
        }
        graph.RemoveGroupCore(group);
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph)
    {
        if (removed is null)
        {
            return;
        }
        graph.AddGroupCore(removed);
        foreach (var child in children)
        {
            graph.SetGroupParentCore(child, GroupId);
        }
        GroupRules.Move(graph, members, GroupId);
        graph.SetCollapsedCore(GroupId, wasCollapsed);
    }
}

/// <summary>Retitles a group.</summary>
/// <param name="groupId">The group.</param>
/// <param name="title">The new title.</param>
public sealed class RenameGroupCommand(ElementId groupId, string title) : IGraphCommand
{
    string? previous;

    /// <inheritdoc />
    public string Description => "Rename group";

    /// <summary>The group.</summary>
    public ElementId GroupId { get; } = groupId;

    /// <summary>The new title.</summary>
    public string Title { get; } = title;

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        if (!graph.TryGetGroup(GroupId, out var group))
        {
            diagnostics.Error(GraphDiagnosticIds.GroupNotFound, GroupId, "The group does not exist.");
            return false;
        }
        previous = group.Title;
        graph.SetGroupTitleCore(group, Title);
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph)
    {
        if (previous is not null && graph.TryGetGroup(GroupId, out var group))
        {
            graph.SetGroupTitleCore(group, previous);
        }
    }
}

/// <summary>Moves a group, with everything it holds, into another group or to the top level.</summary>
/// <param name="groupId">The group to move.</param>
/// <param name="parent">The new enclosing group, or null for the top level.</param>
public sealed class SetGroupParentCommand(ElementId groupId, ElementId? parent) : IGraphCommand
{
    ElementId? previous;

    /// <inheritdoc />
    public string Description => "Move group";

    /// <summary>The group to move.</summary>
    public ElementId GroupId { get; } = groupId;

    /// <summary>The new enclosing group, or null.</summary>
    public ElementId? Parent { get; } = parent;

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        if (!graph.TryGetGroup(GroupId, out var group))
        {
            diagnostics.Error(GraphDiagnosticIds.GroupNotFound, GroupId, "The group does not exist.");
            return false;
        }
        if (!GroupRules.ParentExists(graph, Parent, GroupId, diagnostics))
        {
            return false;
        }
        previous = group.Parent;
        graph.SetGroupParentCore(group, Parent);
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph)
    {
        if (graph.TryGetGroup(GroupId, out var group))
        {
            graph.SetGroupParentCore(group, previous);
        }
    }
}

/// <summary>Moves nodes into a group, or out of every group when <paramref name="groupId"/> is null.</summary>
/// <param name="nodes">The nodes.</param>
/// <param name="groupId">The group, or null.</param>
public sealed class SetNodeGroupCommand(IReadOnlyList<ElementId> nodes, ElementId? groupId) : IGraphCommand
{
    ElementId?[] previous = [];

    /// <inheritdoc />
    public string Description => GroupId is null ? "Remove from group" : "Move to group";

    /// <summary>The nodes.</summary>
    public IReadOnlyList<ElementId> Nodes { get; } = nodes;

    /// <summary>The group, or null.</summary>
    public ElementId? GroupId { get; } = groupId;

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        if (GroupId is { } id && !graph.TryGetGroup(id, out _))
        {
            diagnostics.Error(GraphDiagnosticIds.GroupNotFound, id, "The group does not exist.");
            return false;
        }
        if (!GroupRules.NodesExist(graph, Nodes, diagnostics))
        {
            return false;
        }
        previous = GroupRules.Move(graph, Nodes, GroupId);
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph) => GroupRules.Restore(graph, Nodes, previous);
}

/// <summary>
/// Collapses or expands a group in the default view. This is view state: it is saved with the layout and never
/// changes the group, its members or any result.
/// </summary>
/// <param name="groupId">The group.</param>
/// <param name="collapsed">Whether the group is drawn collapsed.</param>
public sealed class SetGroupCollapsedCommand(ElementId groupId, bool collapsed) : IGraphCommand
{
    bool previous;

    /// <inheritdoc />
    public string Description => Collapsed ? "Collapse group" : "Expand group";

    /// <summary>The group.</summary>
    public ElementId GroupId { get; } = groupId;

    /// <summary>Whether the group is drawn collapsed.</summary>
    public bool Collapsed { get; } = collapsed;

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        if (!graph.TryGetGroup(GroupId, out _))
        {
            diagnostics.Error(GraphDiagnosticIds.GroupNotFound, GroupId, "The group does not exist.");
            return false;
        }
        previous = graph.CollapsedGroups.Contains(GroupId);
        graph.SetCollapsedCore(GroupId, Collapsed);
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph) => graph.SetCollapsedCore(GroupId, previous);
}

/// <summary>Shared checks and membership moves for group commands.</summary>
static class GroupRules
{
    public static bool ParentExists(GraphDocument graph, ElementId? parent, ElementId group, DiagnosticBag diagnostics)
    {
        if (parent is not { } id)
        {
            return true;
        }
        if (!graph.TryGetGroup(id, out _))
        {
            diagnostics.Error(GraphDiagnosticIds.GroupNotFound, id, "The enclosing group does not exist.");
            return false;
        }
        if (graph.IsWithin(id, group))
        {
            diagnostics.Error(GraphDiagnosticIds.GroupCycle, group, "A group cannot be placed inside itself.");
            return false;
        }
        return true;
    }

    public static bool NodesExist(GraphDocument graph, IReadOnlyList<ElementId> nodes, DiagnosticBag diagnostics)
    {
        foreach (var node in nodes)
        {
            if (!graph.TryGetNode(node, out _))
            {
                diagnostics.Error(GraphDiagnosticIds.NodeNotFound, node, "The node does not exist.");
                return false;
            }
        }
        if (nodes.Distinct().Count() != nodes.Count)
        {
            diagnostics.Error(GraphDiagnosticIds.DuplicateElement, null, "A node is listed more than once.");
            return false;
        }
        return true;
    }

    /// <summary>Moves nodes and returns the groups they were in, for <see cref="Restore"/>.</summary>
    public static ElementId?[] Move(GraphDocument graph, IReadOnlyList<ElementId> nodes, ElementId? group)
    {
        var previous = new ElementId?[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            previous[i] = graph.GroupOf(nodes[i]);
            graph.SetNodeGroupCore(nodes[i], group);
        }
        return previous;
    }

    public static void Restore(GraphDocument graph, IReadOnlyList<ElementId> nodes, ElementId?[] previous)
    {
        for (var i = previous.Length - 1; i >= 0; i--)
        {
            graph.SetNodeGroupCore(nodes[i], previous[i]);
        }
    }
}
