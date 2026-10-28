using System.Text.Json;

namespace MASS4.Graph.Dataflow;

/// <summary>Payload of a variable-read node: the blackboard variable it reads.</summary>
/// <param name="Variable">Identity of the variable in the same document.</param>
public sealed record VariableReference(ElementId Variable);

/// <summary>Built-in dataflow node types.</summary>
public static class DataflowNodes
{
    /// <summary>Identity of the wildcard type of variable reads; inference binds it to the variable's type.</summary>
    public static StableId VariableValueTypeId { get; } = Id(0x301);

    /// <summary>The wildcard type of variable-read outputs.</summary>
    public static PortType VariableValueType { get; } =
        new(VariableValueTypeId, "variable value") { IsWildcard = true };

    /// <summary>Key of the variable-read output.</summary>
    public static StableId ValueKey { get; } = Id(0x311);

    /// <summary>Reads a blackboard variable; its payload is a <see cref="VariableReference"/>.</summary>
    public static NodeType GetVariable { get; } = new(Id(0x302), "Get variable",
        [PortDeclaration.Output(ValueKey, "value", VariableValueTypeId)])
    {
        PayloadSerializer = VariableReferenceSerializer.Instance,
    };

    /// <summary>Registers the built-in port and node types.</summary>
    public static void Register(GraphTypeRegistry registry)
    {
        registry.PortTypes.Register(VariableValueType);
        registry.NodeTypes.Register(GetVariable);
    }

    internal static bool TryGetVariable(
        GraphDocument document,
        GraphNode node,
        [NotNullWhen(true)] out GraphVariable? variable)
    {
        variable = null;
        return node.TypeId == GetVariable.Id && node.Payload is VariableReference reference &&
               document.TryGetVariable(reference.Variable, out variable);
    }

    static StableId Id(int value) => new(new Guid($"4d415353-3447-5241-5048-{value:x12}"));
}

/// <summary>Variable values live in per-instance variable slots, so reading them computes nothing.</summary>
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

/// <summary>Rejects variable reads whose variable is missing or carries no dataflow type.</summary>
/// <param name="semantics">Decides which types carry data.</param>
sealed class DataflowVariableRule(DataflowSemantics semantics) : IGraphRule
{
    public void Validate(GraphValidationContext context)
    {
        var graph = context.Graph;
        foreach (var node in graph.Nodes.Where(static node => node.TypeId == DataflowNodes.GetVariable.Id))
        {
            if (!DataflowNodes.TryGetVariable(graph, node, out var variable))
            {
                context.Report(new(DataflowDiagnosticIds.VariableNotFound, DiagnosticSeverity.Error, node.Id,
                    "The node reads a variable that does not exist."));
            }
            else if (!semantics.TryGetValueType(variable.TypeId, out _))
            {
                context.Report(new(DataflowDiagnosticIds.VariableNotData, DiagnosticSeverity.Error, node.Id,
                    $"Variable '{variable.Name}' has a type dataflow cannot carry."));
            }
        }
    }
}
