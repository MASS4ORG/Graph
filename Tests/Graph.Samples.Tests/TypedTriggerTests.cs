using MASS4.Graph.Samples.Fsm;
using MASS4.Graph.State;
using F = MASS4.Graph.Samples.Fsm.Generated;

namespace MASS4.Graph.Samples.Tests;

/// <summary>
/// Triggers are signal declarations: generated builders and raises take them by symbol, saved documents follow
/// identity renames, and no resolution ever compares names.
/// </summary>
public class TypedTriggerTests
{
    [Test]
    public async Task GeneratedBuildersProduceTheHandBuiltGuard()
    {
        var hand = new GraphTypeRegistry();
        GuardLibrary.Register(hand, F.SeePlayer.Type.Id);
        var handExample = GuardExample.Build(GuardLibrary.CreateDataflow(), GuardLibrary.CreateState(), hand);
        var module = new F.GuardModule();
        var generatedExample = F.GeneratedGuardExample.Build(module, new GraphTypeRegistry().Add(module));
        await Assert.That(Shape(generatedExample)).IsEqualTo(Shape(handExample));
        await Assert.That(Shape(generatedExample)).Contains($"{StateNodes.Trigger.Id}={F.SeePlayer.Type.Id}");
    }

    [Test]
    public async Task GeneratedAndHandBuiltGuardsWalkTheSameCycle()
    {
        var hand = new GraphTypeRegistry();
        GuardLibrary.Register(hand);
        var handExample = GuardExample.Build(GuardLibrary.CreateDataflow(), GuardLibrary.CreateState(), hand);
        var handTrace = Walk(handExample, new(GuardLibrary.CreateDataflow(), state: GuardLibrary.CreateState()),
            static (graph, instances, guard, signal) => graph.Raise(instances, guard, signal switch
            {
                Signal.See => GuardLibrary.SeePlayer,
                Signal.Hit => GuardLibrary.Hit,
                _ => GuardLibrary.LosePlayer,
            }));
        var module = new F.GuardModule();
        var generatedExample = F.GeneratedGuardExample.Build(module, new GraphTypeRegistry().Add(module));
        var generatedTrace = Walk(generatedExample, new(module.CreateDataflow(), state: module.CreateState()),
            static (graph, instances, guard, signal) => signal switch
            {
                Signal.See => F.SeePlayer.Raise(graph, instances, guard),
                Signal.Hit => F.Hit.Raise(graph, instances, guard),
                _ => F.LosePlayer.Raise(graph, instances, guard),
            });
        await Assert.That(generatedTrace).IsEqualTo(handTrace);
        await Assert.That(handTrace)
            .IsEqualTo("Patrol:0;Chase:0;Chase:0;Attack:1;Attack:2;Chase:2;Search:2;Search:2;Patrol:2");
    }

    [Test]
    public async Task SavedTriggersFollowASignalRenameIntoTypedRaises()
    {
        var before = new GraphTypeRegistry();
        GuardLibrary.Register(before);
        var saved = GraphJson.Serialize(
            GuardExample.Build(GuardLibrary.CreateDataflow(), GuardLibrary.CreateState(), before).Document);
        await Assert.That(saved).Contains(GuardLibrary.SeePlayer.Id.ToString());
        await Assert.That(saved).DoesNotContain(F.SeePlayer.Type.Id.ToString());

        var module = new F.GuardModule();
        var loaded = GraphJson.Deserialize(saved, new GraphTypeRegistry().Add(module));
        await Assert.That(loaded.Diagnostics.Count).IsEqualTo(0);
        var triggers = loaded.Document!.Nodes
            .Select(static n => n.Annotations.TryGetValue(StateNodes.Trigger.Id, out var t) ? (StableId?)t : null)
            .OfType<StableId>().ToArray();
        await Assert.That(triggers).Contains(F.SeePlayer.Type.Id);
        await Assert.That(triggers).DoesNotContain(GuardLibrary.SeePlayer.Id);
        await Assert.That(GraphJson.Serialize(loaded.Document)).DoesNotContain(GuardLibrary.SeePlayer.Id.ToString());

        var graph = new GraphInterpreter(module.CreateDataflow(), state: module.CreateState())
            .Compile(loaded.Document).Graph!;
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var context = new EvaluationContext().Set(new StateTime { DeltaSeconds = .1 });
        var byDeclaration = instances.Allocate();
        var byObsoleteSignal = instances.Allocate();
        graph.Initialize(context, instances, byDeclaration);
        graph.Initialize(context, instances, byObsoleteSignal);
        await Assert.That(F.SeePlayer.Raise(graph, instances, byDeclaration)).IsTrue();
        await Assert.That(graph.Raise(instances, byObsoleteSignal, GuardLibrary.SeePlayer)).IsTrue();
        await graph.EvaluateAsync(context, instances, byDeclaration);
        await graph.EvaluateAsync(context, instances, byObsoleteSignal);
        var machine = ((InterpretedGraph)graph).StateMachine!;
        var chase = loaded.Document.Nodes.Single(n => n.TypeId == StateNodes.State.Id &&
            loaded.Document.GetEdges(n.GetPort(StateNodes.EnterKey).Id).Count == 3);
        await Assert.That(machine.CurrentState(instances, byDeclaration).Id).IsEqualTo(chase.Id);
        await Assert.That(machine.CurrentState(instances, byObsoleteSignal).Id).IsEqualTo(chase.Id);
    }

    [Test]
    public async Task TriggersResolveByIdentityNeverByName()
    {
        var registry = new GraphTypeRegistry();
        StateNodes.Register(registry);
        SignalType left = new(StableId.New(), "Same name");
        SignalType right = new(StableId.New(), "Same name");
        registry.Signals.Register(left);
        registry.Signals.Register(right);
        var state = new StateSemantics();
        var editor = new GraphEditor(new GraphDocument(registry), additionalRules: state.Rules);
        var entry = StateGraphBuilder.AddEntry(editor);
        var idle = StateGraphBuilder.AddState(editor);
        var viaLeft = StateGraphBuilder.AddState(editor);
        var viaRight = StateGraphBuilder.AddState(editor);
        StateGraphBuilder.Start(editor, entry, idle.Enter);
        StateGraphBuilder.AddTransition(editor, idle.Exit, viaLeft.Enter, left);
        StateGraphBuilder.AddTransition(editor, idle.Exit, viaRight.Enter, right);

        var relabelled = new GraphTypeRegistry();
        StateNodes.Register(relabelled);
        relabelled.Signals.Register(new(left.Id, "Renamed for display"));
        relabelled.Signals.Register(new(right.Id, "Same name"));
        var document = GraphJson.Deserialize(GraphJson.Serialize(editor.Document), relabelled).Document!;
        var graph = new GraphInterpreter(new(), state: state).Compile(document).Graph!;
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var context = new EvaluationContext().Set(new StateTime());
        var instance = instances.Allocate();
        graph.Initialize(context, instances, instance);
        graph.Raise(instances, instance, right);
        await graph.EvaluateAsync(context, instances, instance);
        await Assert.That(((InterpretedGraph)graph).StateMachine!.CurrentState(instances, instance).Id)
            .IsEqualTo(viaRight.NodeId);
        await Assert.That(graph.Raise(instances, instance, new SignalType(StableId.New(), "Same name"))).IsFalse();
    }

    [Test]
    public async Task TypedTransitionsRejectUnregisteredSignalsAtomically()
    {
        var registry = new GraphTypeRegistry();
        StateNodes.Register(registry);
        var state = new StateSemantics();
        var editor = new GraphEditor(new GraphDocument(registry), additionalRules: state.Rules);
        var a = StateGraphBuilder.AddState(editor);
        var b = StateGraphBuilder.AddState(editor);
        var nodes = editor.Document.Nodes.Count;
        await Assert.That(() => StateGraphBuilder.AddTransition(editor, a.Exit, b.Enter, new SignalType(StableId.New(), "Unknown")))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(editor.Document.Nodes.Count).IsEqualTo(nodes);
        await Assert.That(editor.Document.Edges.Count).IsEqualTo(0);
    }

    enum Signal { See, Hit, Lose }

    static string Walk(GuardExample example, GraphInterpreter interpreter,
        Func<ICompiledGraph, InstanceBuffer, InstanceHandle, Signal, bool> raise)
    {
        var graph = (InterpretedGraph)interpreter.Compile(example.Document).Graph!;
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var guard = instances.Allocate();
        var time = new StateTime();
        var context = new EvaluationContext().Set(time);
        graph.Initialize(context, instances, guard);
        var names = new Dictionary<ElementId, string>
        {
            [example.Patrol] = "Patrol", [example.Chase] = "Chase", [example.Attack] = "Attack", [example.Search] = "Search",
        };
        List<string> trace = [];
        void Step(double seconds, Signal? signal = null, double? distance = null)
        {
            if (signal is { } raised && !raise(graph, instances, guard, raised))
            {
                throw new InvalidOperationException($"{raised} did not resolve.");
            }
            if (distance is { } value)
            {
                graph.SetVariable(instances, guard, example.Distance.Id, value);
            }
            time.DeltaSeconds = seconds;
            var evaluation = graph.EvaluateAsync(context, instances, guard);
            if (!evaluation.IsCompleted)
            {
                throw new InvalidOperationException("The interpreter must complete synchronously.");
            }
            evaluation.GetAwaiter().GetResult();
            trace.Add($"{names[graph.StateMachine!.CurrentState(instances, guard).Id]}:" +
                graph.Data.Read<double>(context, instances, guard, example.Attacks));
        }

        Step(.1);
        Step(.1, Signal.See);
        Step(.1);
        Step(.1, distance: 1);
        Step(.1, Signal.Hit);
        Step(.1, distance: 3);
        Step(.1, Signal.Lose);
        Step(2);
        Step(1.5);
        return string.Join(";", trace);
    }

    /// <summary>
    /// Element identities differ between builds, so edges are compared by what they join: named states by role, other
    /// nodes by type, payload and annotations.
    /// </summary>
    static string Shape(GuardExample example)
    {
        var document = example.Document;
        var roles = new Dictionary<ElementId, string>
        {
            [example.Patrol] = "Patrol", [example.Chase] = "Chase", [example.Attack] = "Attack", [example.Search] = "Search",
        };
        string Label(GraphNode node) => roles.TryGetValue(node.Id, out var role) ? role
            : $"{node.TypeId}|{(node.Payload is VariableReference ? "variable" : node.Payload)}|" +
              string.Join(",", node.Annotations.Select(static a => $"{a.Key}={a.Value}").Order(StringComparer.Ordinal));
        string End(ElementId portId)
        {
            var port = document.GetPort(portId);
            return $"{Label(document.GetNode(port.NodeId))}.{port.Declaration.Key}";
        }
        string Edge(GraphEdge edge) => $"{End(edge.Source)}->{End(edge.Target)}";
        return string.Join("\n", document.Nodes.Select(Label).Order(StringComparer.Ordinal)
            .Concat(document.Edges.Select(Edge).Order(StringComparer.Ordinal)));
    }
}
