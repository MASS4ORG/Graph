using System.Diagnostics.CodeAnalysis;

namespace MASS4.Graph.Control;

/// <summary>
/// Execution-driven semantics: exec edges decide which node runs next, nodes choose which exec outputs continue, and
/// nodes may keep per-instance memory. Data inputs and outputs of control nodes go through an <see cref="IDataPlane"/>,
/// so control graphs compose with dataflow in one document.
/// </summary>
public sealed class ControlSemantics
{
    readonly Dictionary<StableId, IControlNode> behaviors = [];

    /// <summary>Creates control semantics with no registered nodes.</summary>
    public ControlSemantics() => Rules = [new ControlBehaviorRule(this)];

    /// <summary>Identity of the exec port type.</summary>
    public static StableId ExecTypeId { get; } = StableId.Parse("4d415353-3447-5241-5048-000000000101");

    /// <summary>The exec port type, owned by these semantics.</summary>
    public static PortType ExecType { get; } = new(ExecTypeId, "exec");

    /// <summary>Rules editors run on every change.</summary>
    public IReadOnlyList<IGraphRule> Rules { get; }

    /// <summary>Maximum node executions in one run before it reports a step-limit failure.</summary>
    public int MaxStepsPerRun { get; init; } = 100_000;

    /// <summary>Maximum pending exec targets in one run before it reports a stack overflow.</summary>
    public int StackCapacity { get; init; } = 1024;

    /// <summary>Maximum exec outputs one node execution may continue.</summary>
    public int MaxContinuations { get; init; } = 16;

    /// <summary>Registers the exec port type.</summary>
    public static void RegisterTypes(GraphTypeRegistry registry) => registry.PortTypes.Register(ExecType);

    /// <summary>Declares an exec input, which any number of exec outputs may enter.</summary>
    public static PortDeclaration ExecInput(StableId key, string name = "in") =>
        new(key, name, PortDirection.Input, ExecTypeId, PortMultiplicity.Multiple);

    /// <summary>
    /// Declares an exec output. Several edges may leave it; targets run in the order of their node identities.
    /// </summary>
    public static PortDeclaration ExecOutput(StableId key, string name = "out") =>
        new(key, name, PortDirection.Output, ExecTypeId, PortMultiplicity.Multiple);

    /// <summary>Binds a behavior to a node type.</summary>
    /// <exception cref="ArgumentException">The node type already has a behavior.</exception>
    public ControlSemantics AddNode(StableId nodeTypeId, IControlNode behavior)
    {
        if (!behaviors.TryAdd(nodeTypeId, behavior))
        {
            throw new ArgumentException($"Node type {nodeTypeId} already has a control behavior.", nameof(nodeTypeId));
        }
        return this;
    }

    /// <summary>Whether any node of the document has a control behavior.</summary>
    public bool Participates(GraphDocument document) =>
        document.Nodes.Any(node => behaviors.ContainsKey(node.TypeId));

    /// <summary>Finds the behavior bound to a node type.</summary>
    public bool TryGetBehavior(StableId nodeTypeId, [NotNullWhen(true)] out IControlNode? behavior) =>
        behaviors.TryGetValue(nodeTypeId, out behavior);

    /// <summary>
    /// Builds the per-instance program, reserving node memory in <paramref name="layout"/>. Data ports resolve through
    /// <paramref name="data"/>; without one, reading or writing data throws.
    /// </summary>
    public ControlProgram Compile(
        GraphDocument document,
        InstanceLayoutBuilder layout,
        IDataPlane? data,
        DiagnosticBag diagnostics)
    {
        foreach (var diagnostic in GraphValidator.Validate(document, Rules))
        {
            diagnostics.Add(diagnostic);
        }
        return ControlProgram.Create(this, document, layout, data);
    }

    internal static bool IsExec(GraphPort port) => port.TypeId == ExecTypeId;
}

/// <summary>Outcome of a control run.</summary>
public enum ControlRunResult
{
    /// <summary>Every continuation ran.</summary>
    Completed,

    /// <summary>The run exceeded the step limit, typically through an exec loop.</summary>
    StepLimitExceeded,

    /// <summary>Pending exec targets exceeded <see cref="ControlSemantics.StackCapacity"/>.</summary>
    StackOverflow,
}

/// <summary>Diagnostic codes reported by control semantics.</summary>
public static class ControlDiagnosticIds
{
    /// <summary>A node has exec ports but no control behavior, so execution stops there.</summary>
    public const string UnboundExecNode = "MG2001";
}

/// <summary>Warns about nodes with exec ports whose type has no control behavior.</summary>
/// <param name="semantics">Holds the bound behaviors.</param>
sealed class ControlBehaviorRule(ControlSemantics semantics) : IGraphRule
{
    public void Validate(GraphValidationContext context)
    {
        foreach (var node in context.AffectedNodes)
        {
            if (node.Ports.Any(ControlSemantics.IsExec) && !semantics.TryGetBehavior(node.TypeId, out _))
            {
                context.Report(new(ControlDiagnosticIds.UnboundExecNode, DiagnosticSeverity.Warning, node.Id,
                    "This node has exec ports but no control behavior; execution stops here."));
            }
        }
    }
}
