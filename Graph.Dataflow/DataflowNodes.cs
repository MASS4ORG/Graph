using System.Text.Json;

namespace MASS4.Graph.Dataflow;

/// <summary>Payload of a variable node: the blackboard variable it reads or, for outputs, writes.</summary>
/// <param name="Variable">Identity of the variable in the same document.</param>
public sealed record VariableReference(ElementId Variable);

/// <summary>Built-in dataflow node types.</summary>
public static class DataflowNodes
{
    /// <summary>Identity of the wildcard type of variable nodes; inference binds it to the variable's type.</summary>
    public static StableId VariableValueTypeId { get; } = Id(0x301);

    /// <summary>The wildcard type of variable-node ports.</summary>
    public static PortType VariableValueType { get; } =
        new(VariableValueTypeId, "variable value") { IsWildcard = true };

    /// <summary>Key of the variable-read output.</summary>
    public static StableId ValueKey { get; } = Id(0x311);

    /// <summary>Key of the graph-output input.</summary>
    public static StableId ResultKey { get; } = Id(0x312);

    /// <summary>Reads a blackboard variable; its payload is a <see cref="VariableReference"/>.</summary>
    public static NodeType GetVariable { get; } = new(Id(0x302), "Get variable",
        [PortDeclaration.Output(ValueKey, "value", VariableValueTypeId)])
    {
        PayloadSerializer = VariableReferenceSerializer.Instance,
    };

    /// <summary>
    /// Defines the value of an output-scope variable, the graph's declared result that hosts read; its payload is a
    /// <see cref="VariableReference"/>. Each output has at most one such node.
    /// </summary>
    public static NodeType Output { get; } = new(Id(0x303), "Output",
        [PortDeclaration.Input(ResultKey, "value", VariableValueTypeId)])
    {
        PayloadSerializer = VariableReferenceSerializer.Instance,
    };

    /// <summary>Registers the built-in port and node types.</summary>
    public static void Register(GraphTypeRegistry registry)
    {
        registry.PortTypes.Register(VariableValueType);
        registry.NodeTypes.Register(GetVariable);
        registry.NodeTypes.Register(Output);
    }

    /// <summary>Finds the variable a variable-read or output node references.</summary>
    internal static bool TryGetVariable(
        GraphDocument document,
        GraphNode node,
        [NotNullWhen(true)] out GraphVariable? variable)
    {
        variable = null;
        return IsVariableNode(node) && node.Payload is VariableReference reference &&
               document.TryGetVariable(reference.Variable, out variable);
    }

    internal static bool IsVariableNode(GraphNode node) => node.TypeId == GetVariable.Id || node.TypeId == Output.Id;

    static StableId Id(int value) => new(new Guid($"4d415353-3447-5241-5048-{value:x12}"));
}

/// <summary>Variable values live in per-instance variable slots, so variable nodes compute nothing.</summary>
sealed class VariableNode : IDataflowNode
{
    public static VariableNode Instance { get; } = new();

    public void Evaluate(DataflowContext context)
    {
    }
}

/// <summary>Reads and writes <see cref="VariableReference"/> without reflection.</summary>
sealed class VariableReferenceSerializer : IValueSerializer
{
    public static VariableReferenceSerializer Instance { get; } = new();

    public void Write(Utf8JsonWriter writer, object? value)
    {
        writer.WriteStartObject();
        writer.WriteString("variable", ((VariableReference)value!).Variable.ToString());
        writer.WriteEndObject();
    }

    public object? Read(JsonElement element) =>
        new VariableReference(ElementId.Parse(element.GetProperty("variable").GetString()!));
}

/// <summary>
/// Checks variable nodes: the variable exists and carries data; reads target non-output variables; output nodes
/// target output-scope variables, at most one per variable. Full validation also warns about outputs nothing defines.
/// </summary>
/// <param name="semantics">Decides which types carry data.</param>
sealed class DataflowVariableRule(DataflowSemantics semantics) : IGraphRule
{
    public void Validate(GraphValidationContext context)
    {
        var graph = context.Graph;
        HashSet<ElementId> written = [];
        foreach (var node in graph.Nodes.Where(DataflowNodes.IsVariableNode).OrderBy(static node => node.Id))
        {
            if (Check(graph, node, written) is { } diagnostic)
            {
                context.Report(diagnostic);
            }
        }
        if (context.Changes is null)
        {
            ReportUndefinedOutputs(context);
        }
    }

    static void ReportUndefinedOutputs(GraphValidationContext context)
    {
        var graph = context.Graph;
        var defined = graph.Nodes
            .Where(node => node.TypeId == DataflowNodes.Output.Id && graph.GetEdges(node.Ports[0].Id).Count > 0)
            .Select(static node => node.Payload)
            .OfType<VariableReference>()
            .Select(static reference => reference.Variable)
            .ToHashSet();
        foreach (var output in graph.Variables.Where(v => v.Scope == VariableScope.Output && !defined.Contains(v.Id)))
        {
            context.Report(new(DataflowDiagnosticIds.OutputUndefined, DiagnosticSeverity.Warning, output.Id,
                $"Output '{output.Name}' has no connected output node and always reads its default."));
        }
    }

    Diagnostic? Check(GraphDocument graph, GraphNode node, HashSet<ElementId> written)
    {
        if (!DataflowNodes.TryGetVariable(graph, node, out var variable))
        {
            return Error(DataflowDiagnosticIds.VariableNotFound, node,
                "The node references a variable that does not exist.");
        }
        if (!semantics.TryGetValueType(variable.TypeId, out _))
        {
            return Error(DataflowDiagnosticIds.VariableNotData, node,
                $"Variable '{variable.Name}' has a type dataflow cannot carry.");
        }
        var isOutput = variable.Scope == VariableScope.Output;
        if (node.TypeId == DataflowNodes.GetVariable.Id)
        {
            return isOutput
                ? Error(DataflowDiagnosticIds.OutputRead, node,
                    $"Output '{variable.Name}' is a result for hosts and cannot be read inside the graph.")
                : null;
        }
        if (!isOutput)
        {
            return Error(DataflowDiagnosticIds.OutputScope, node,
                $"Variable '{variable.Name}' is not an output; only output-scope variables have output nodes.");
        }
        return written.Add(variable.Id)
            ? null
            : Error(DataflowDiagnosticIds.OutputRedefined, node,
                $"Output '{variable.Name}' already has an output node.");
    }

    static Diagnostic Error(string id, GraphNode node, string message) =>
        new(id, DiagnosticSeverity.Error, node.Id, message);
}
