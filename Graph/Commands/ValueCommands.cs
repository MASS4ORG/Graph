namespace MASS4.Graph;

/// <summary>Replaces a node payload.</summary>
/// <param name="nodeId">The node.</param>
/// <param name="payload">The new application data.</param>
public sealed class SetPayloadCommand(ElementId nodeId, object? payload) : IGraphCommand
{
    object? previous;

    /// <inheritdoc />
    public string Description => "Edit node";

    /// <summary>The node.</summary>
    public ElementId NodeId { get; } = nodeId;

    /// <summary>The new application data.</summary>
    public object? Payload { get; } = payload;

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        if (!graph.TryGetNode(NodeId, out var node))
        {
            diagnostics.Error(GraphDiagnosticIds.NodeNotFound, NodeId, "The node does not exist.");
            return false;
        }
        previous = node.Payload;
        graph.SetPayloadCore(node, Payload);
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph)
    {
        if (graph.TryGetNode(NodeId, out var node))
        {
            graph.SetPayloadCore(node, previous);
        }
    }
}

/// <summary>Sets a node annotation of a registered type, or removes it when the value is null.</summary>
/// <param name="nodeId">The node.</param>
/// <param name="annotationType">Identity of the registered <see cref="AnnotationType"/>.</param>
/// <param name="value">The value, of the type's kind; null removes the annotation.</param>
public sealed class SetAnnotationCommand(ElementId nodeId, StableId annotationType, object? value) : IGraphCommand
{
    StableId canonical;
    object? stored;
    object? previous;

    /// <inheritdoc />
    public string Description => "Edit annotation";

    /// <summary>The node.</summary>
    public ElementId NodeId { get; } = nodeId;

    /// <summary>Identity of the annotation type.</summary>
    public StableId AnnotationType { get; } = annotationType;

    /// <summary>The value; null removes the annotation.</summary>
    public object? Value { get; } = value;

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        if (!graph.TryGetNode(NodeId, out var node))
        {
            diagnostics.Error(GraphDiagnosticIds.NodeNotFound, NodeId, "The node does not exist.");
            return false;
        }
        if (AnnotationRules.Check(graph.Registry, NodeId, AnnotationType, Value, out canonical, out stored) is { } problem)
        {
            diagnostics.Add(problem);
            return false;
        }
        previous = node.Annotations.TryGetValue(canonical, out var current) ? current : null;
        graph.SetAnnotationCore(node, canonical, stored);
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph)
    {
        if (graph.TryGetNode(NodeId, out var node))
        {
            graph.SetAnnotationCore(node, canonical, previous);
        }
    }
}

/// <summary>Checks annotation writes against the registry: registered types only, values of their kind.</summary>
static class AnnotationRules
{
    public static Diagnostic? Check(
        GraphTypeRegistry registry,
        ElementId node,
        StableId type,
        object? value,
        out StableId canonical,
        out object? stored)
    {
        canonical = registry.Annotations.Canonicalize(type);
        stored = value;
        if (!registry.Annotations.TryResolve(type, out var annotation))
        {
            return new(GraphDiagnosticIds.AnnotationTypeNotRegistered, DiagnosticSeverity.Error, node,
                $"Annotation type {type} is not registered.");
        }
        if (value is not null && !annotation.Accepts(value))
        {
            return new(GraphDiagnosticIds.AnnotationValueMismatch, DiagnosticSeverity.Error, node,
                $"Annotation '{annotation.DisplayName}' holds {annotation.Kind} values.");
        }
        // Signal values follow renames exactly as they do on load, so edits never store an obsolete identity.
        if (annotation.Kind == AnnotationKind.Signal && value is StableId signal)
        {
            stored = registry.Signals.Canonicalize(signal);
        }
        return null;
    }
}

/// <summary>Adds a blackboard variable, or replaces the one with the same identity.</summary>
/// <param name="variable">The variable.</param>
public sealed class SetVariableCommand(GraphVariable variable) : IGraphCommand
{
    GraphVariable? previous;

    /// <inheritdoc />
    public string Description => $"Set variable {Variable.Name}";

    /// <summary>The variable.</summary>
    public GraphVariable Variable { get; } = variable;

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        if (!graph.TryGetVariable(Variable.Id, out previous) && graph.ContainsElement(Variable.Id))
        {
            diagnostics.Error(GraphDiagnosticIds.DuplicateElement, Variable.Id,
                "The identity belongs to another element.");
            return false;
        }
        graph.SetVariableCore(Variable);
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph)
    {
        if (previous is null)
        {
            graph.RemoveVariableCore(Variable.Id);
        }
        else
        {
            graph.SetVariableCore(previous);
        }
    }
}

/// <summary>Removes a blackboard variable.</summary>
/// <param name="variableId">The variable.</param>
public sealed class RemoveVariableCommand(ElementId variableId) : IGraphCommand
{
    GraphVariable? removed;

    /// <inheritdoc />
    public string Description => "Remove variable";

    /// <summary>The variable.</summary>
    public ElementId VariableId { get; } = variableId;

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        if (!graph.TryGetVariable(VariableId, out removed))
        {
            diagnostics.Error(GraphDiagnosticIds.VariableNotFound, VariableId, "The variable does not exist.");
            return false;
        }
        graph.RemoveVariableCore(VariableId);
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph)
    {
        if (removed is not null)
        {
            graph.SetVariableCore(removed);
        }
    }
}

/// <summary>Moves a node in the default view, or clears its position; layout never affects semantics.</summary>
/// <param name="nodeId">The node.</param>
/// <param name="position">The new position, or null to clear it.</param>
public sealed class MoveNodeCommand(ElementId nodeId, GraphPoint? position) : IGraphCommand
{
    GraphPoint? previous;

    /// <inheritdoc />
    public string Description => "Move node";

    /// <summary>The node.</summary>
    public ElementId NodeId { get; } = nodeId;

    /// <summary>The new position, or null to clear it.</summary>
    public GraphPoint? Position { get; } = position;

    /// <inheritdoc />
    public bool Apply(GraphDocument graph, DiagnosticBag diagnostics)
    {
        if (!graph.TryGetNode(NodeId, out _))
        {
            diagnostics.Error(GraphDiagnosticIds.NodeNotFound, NodeId, "The node does not exist.");
            return false;
        }
        previous = graph.Positions.TryGetValue(NodeId, out var current) ? current : null;
        graph.SetPositionCore(NodeId, Position);
        return true;
    }

    /// <inheritdoc />
    public void Revert(GraphDocument graph) => graph.SetPositionCore(NodeId, previous);
}
