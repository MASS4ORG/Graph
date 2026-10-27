namespace MASS4.Graph;

/// <summary>An input with a compile-time domain type.</summary>
/// <typeparam name="T">Carried type.</typeparam>
/// <param name="Id">Document port identity.</param>
public readonly record struct GraphInput<T>(ElementId Id);

/// <summary>An output with a compile-time domain type.</summary>
/// <typeparam name="T">Carried type.</typeparam>
/// <param name="Id">Document port identity.</param>
public readonly record struct GraphOutput<T>(ElementId Id);

/// <summary>Marker for state-machine flow handles.</summary>
public readonly struct StateFlow;

/// <summary>Marker for control-flow handles.</summary>
public readonly struct ControlFlow;

/// <summary>Command helpers used by generated builders.</summary>
public static class TypedGraphBuilder
{
    /// <summary>Connects matching domain types through normal editor validation.</summary>
    public static void Connect<T>(this GraphEditor editor, GraphOutput<T> output, GraphInput<T> input) =>
        Require(editor, new ConnectCommand(output.Id, input.Id));

    /// <summary>Executes an edit and throws with diagnostics when rejected.</summary>
    public static void Require(GraphEditor editor, IGraphCommand command)
    {
        var result = editor.Execute(command);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Diagnostics.Select(static d => d.Message)));
        }
    }

    /// <summary>Validates a host input against its asset, persistent type and writable scope.</summary>
    public static void ValidateInput(GraphDocument document, ICompiledGraph graph, GraphVariable variable, StableId type)
    {
        if (document.AssetId != graph.AssetId || variable.TypeId != type ||
            variable.Scope is not (VariableScope.External or VariableScope.Input) || !document.Variables.Contains(variable))
        {
            throw new ArgumentException("The variable is not an input of this asset and type.", nameof(variable));
        }
    }
}

/// <summary>A typed external input resolved once against a compiled asset.</summary>
/// <typeparam name="T">Unmanaged variable type.</typeparam>
public readonly struct GraphVariableHandle<T> where T : unmanaged
{
    readonly ICompiledGraph graph;
    readonly ElementId id;

    /// <summary>Checks the variable's asset, scope and persistent type before retaining its identity.</summary>
    public GraphVariableHandle(GraphDocument document, ICompiledGraph graph, GraphVariable variable, StableId type)
    {
        TypedGraphBuilder.ValidateInput(document, graph, variable, type);
        this.graph = graph;
        id = variable.Id;
    }

    /// <summary>Writes an instance input without boxing or repeated variable lookup.</summary>
    public void Set(InstanceBuffer instances, InstanceHandle instance, T value)
    {
        if (!graph.SetVariable(instances, instance, id, value))
        {
            throw new InvalidOperationException("The compiled graph rejected its resolved variable input.");
        }
    }
}

/// <summary>A typed reference input resolved once against a compiled asset.</summary>
/// <typeparam name="T">Reference variable type.</typeparam>
public readonly struct GraphReferenceVariableHandle<T> where T : class
{
    readonly ICompiledGraph graph;
    readonly ElementId id;

    /// <summary>Checks the variable's asset, scope and persistent type before retaining its identity.</summary>
    public GraphReferenceVariableHandle(GraphDocument document, ICompiledGraph graph, GraphVariable variable, StableId type)
    {
        TypedGraphBuilder.ValidateInput(document, graph, variable, type);
        this.graph = graph;
        id = variable.Id;
    }

    /// <summary>Writes a typed reference without repeated variable lookup.</summary>
    public void Set(InstanceBuffer instances, InstanceHandle instance, T? value)
    {
        if (!graph.SetVariable(instances, instance, id, value))
        {
            throw new InvalidOperationException("The compiled graph rejected its resolved reference input.");
        }
    }
}
