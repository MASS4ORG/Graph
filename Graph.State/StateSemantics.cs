namespace MASS4.Graph.State;

/// <summary>
/// State-machine semantics: states and transition nodes joined by state-flow edges, which may form cycles. Each
/// instance keeps its current state, time in state and raised triggers. Guards and state outputs use an
/// <see cref="IDataPlane"/>, so state machines compose with dataflow in one document.
/// </summary>
public sealed class StateSemantics
{
    readonly HashSet<StableId> entries = [];
    readonly Dictionary<StableId, IStateNode> states = [];
    readonly Dictionary<StableId, ITransitionNode> transitions = [];

    /// <summary>Creates state semantics with the built-in entry, state and transition types bound.</summary>
    public StateSemantics()
    {
        Rules = [new StateStructureRule(this)];
        AddEntry(StateNodes.Entry.Id);
        AddState(StateNodes.State.Id, EmptyState.Instance);
        AddTransition(StateNodes.Transition.Id, TriggerTransition.Instance);
    }

    /// <summary>
    /// Rules editors run on every change: valid state-flow edges, and triggers registered as signals in the
    /// document's type registry.
    /// </summary>
    public IReadOnlyList<IGraphRule> Rules { get; }

    /// <summary>Marks a node type as the entry of a state machine.</summary>
    /// <exception cref="ArgumentException">The node type already has a role.</exception>
    public StateSemantics AddEntry(StableId nodeTypeId)
    {
        RequireUnbound(nodeTypeId);
        entries.Add(nodeTypeId);
        return this;
    }

    /// <summary>Binds a state behavior to a node type.</summary>
    /// <exception cref="ArgumentException">The node type already has a role.</exception>
    public StateSemantics AddState(StableId nodeTypeId, IStateNode behavior)
    {
        RequireUnbound(nodeTypeId);
        states.Add(nodeTypeId, behavior);
        return this;
    }

    /// <summary>Binds a transition behavior to a node type.</summary>
    /// <exception cref="ArgumentException">The node type already has a role.</exception>
    public StateSemantics AddTransition(StableId nodeTypeId, ITransitionNode behavior)
    {
        RequireUnbound(nodeTypeId);
        transitions.Add(nodeTypeId, behavior);
        return this;
    }

    /// <summary>
    /// Validates a document as one state machine and builds its per-instance program, reserving slots in
    /// <paramref name="layout"/>. Guards and state outputs resolve through <paramref name="data"/>.
    /// </summary>
    /// <returns>The program, or null when the document has errors.</returns>
    public StateMachineProgram? Compile(
        GraphDocument document,
        InstanceLayoutBuilder layout,
        IDataPlane? data,
        DiagnosticBag diagnostics) =>
        StateCompiler.Compile(this, document, layout, data, diagnostics);

    /// <summary>Whether any node of the document has a state role, so the document contains a state machine.</summary>
    public bool Participates(GraphDocument document) =>
        document.Nodes.Any(node => RoleOf(node) != StateRole.None);

    internal StateRole RoleOf(GraphNode node) =>
        entries.Contains(node.TypeId) ? StateRole.Entry
        : states.ContainsKey(node.TypeId) ? StateRole.State
        : transitions.ContainsKey(node.TypeId) ? StateRole.Transition
        : StateRole.None;

    internal IStateNode StateBehavior(GraphNode node) => states[node.TypeId];

    internal ITransitionNode TransitionBehavior(GraphNode node) => transitions[node.TypeId];

    void RequireUnbound(StableId nodeTypeId)
    {
        if (entries.Contains(nodeTypeId) || states.ContainsKey(nodeTypeId) || transitions.ContainsKey(nodeTypeId))
        {
            throw new ArgumentException($"Node type {nodeTypeId} already has a state role.", nameof(nodeTypeId));
        }
    }
}

enum StateRole
{
    None,
    Entry,
    State,
    Transition,
}

/// <summary>Diagnostic codes reported by state semantics.</summary>
public static class StateDiagnosticIds
{
    /// <summary>A state-flow edge joins roles that cannot follow each other.</summary>
    public const string InvalidFlow = "MG3001";

    /// <summary>A state-flow edge touches a node without a state role.</summary>
    public const string UnboundFlowNode = "MG3002";

    /// <summary>A transition names a trigger that is not registered.</summary>
    public const string UnknownTrigger = "MG3003";

    /// <summary>The document does not have exactly one entry node.</summary>
    public const string EntryCount = "MG3004";

    /// <summary>The entry node does not lead to a state.</summary>
    public const string EntryUnconnected = "MG3005";

    /// <summary>A transition does not join two states.</summary>
    public const string TransitionUnconnected = "MG3006";
}

/// <summary>
/// Rejects state-flow edges between roles that cannot follow each other (only entry → state, state → transition and
/// transition → state are valid) and transitions naming unregistered triggers.
/// </summary>
/// <param name="semantics">Holds the bound roles.</param>
sealed class StateStructureRule(StateSemantics semantics) : IGraphRule
{
    public void Validate(GraphValidationContext context)
    {
        var graph = context.Graph;
        foreach (var edge in context.AffectedEdges)
        {
            var source = graph.GetPort(edge.Source);
            if (StateNodes.IsFlow(source) &&
                Check(graph.GetNode(source.NodeId), graph.GetNode(graph.GetPort(edge.Target).NodeId), edge) is
                { } diagnostic)
            {
                context.Report(diagnostic);
            }
        }
        foreach (var node in context.AffectedNodes)
        {
            if (UnknownTrigger(graph.Registry.Signals, node) is { } trigger)
            {
                context.Report(new(StateDiagnosticIds.UnknownTrigger, DiagnosticSeverity.Error, node.Id,
                    $"Trigger {trigger} is not a registered signal."));
            }
        }
    }

    static StableId? UnknownTrigger(IdentityRegistry<SignalType> signals, GraphNode node) =>
        node.Payload is TransitionPayload { Trigger: { } trigger } && !signals.TryResolve(trigger, out _)
            ? trigger
            : null;

    Diagnostic? Check(GraphNode from, GraphNode to, GraphEdge edge)
    {
        var source = semantics.RoleOf(from);
        var target = semantics.RoleOf(to);
        if (source == StateRole.None || target == StateRole.None)
        {
            return new(StateDiagnosticIds.UnboundFlowNode, DiagnosticSeverity.Warning, edge.Id,
                "A state-flow edge touches a node without a state role.");
        }
        var valid = (source, target) is (StateRole.Entry, StateRole.State) or (StateRole.State, StateRole.Transition)
            or (StateRole.Transition, StateRole.State);
        return valid
            ? null
            : new(StateDiagnosticIds.InvalidFlow, DiagnosticSeverity.Error, edge.Id,
                $"A {source} cannot lead directly to a {target}; join states through transitions.");
    }
}
