namespace MASS4.Graph;

/// <summary>
/// Derives node types from subgraph assets. A subgraph's contract is its exposed variables: inputs become input ports
/// and outputs become output ports of the node that references it.
/// </summary>
public static class Subgraphs
{
    /// <summary>
    /// Creates the node type that references a subgraph. Its identity is the subgraph's asset identity and each
    /// port key is the identity of the variable it exposes, so references survive variable renames.
    /// </summary>
    public static NodeType CreateNodeType(GraphDocument subgraph, string displayName)
    {
        var ports = subgraph.Variables
            .Where(static variable => variable.Scope is VariableScope.Input or VariableScope.Output)
            .OrderBy(static variable => variable.Scope)
            .ThenBy(static variable => variable.Name, StringComparer.Ordinal)
            .ThenBy(static variable => variable.Id)
            .Select(static variable => variable.Scope == VariableScope.Input
                ? PortDeclaration.Input(new(variable.Id.Value), variable.Name, variable.TypeId)
                : PortDeclaration.Output(new(variable.Id.Value), variable.Name, variable.TypeId))
            .ToArray();
        return new(subgraph.AssetId, displayName, ports);
    }
}
