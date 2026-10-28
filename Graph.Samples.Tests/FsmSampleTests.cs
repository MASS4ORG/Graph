using MASS4.Graph.Samples.Fsm;
using MASS4.Graph.State;

namespace MASS4.Graph.Samples.Tests;

public class FsmSampleTests
{
    [Test]
    public async Task TheGuardWalksItsWholeCycle()
    {
        var (example, graph) = Compile(GuardLibrary.CreateState());
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var guard = instances.Allocate();
        var time = new StateTime();
        var context = new EvaluationContext().Set(time);
        graph.Initialize(context, instances, guard);
        List<(ElementId State, double Attacks)> trace = [];
        void Step(double seconds, StableId? signal = null, double? distance = null)
        {
            if (signal is { } raised)
            {
                graph.Raise(instances, guard, raised);
            }
            if (distance is { } value)
            {
                graph.SetVariable(instances, guard, example.Distance.Id, value);
            }
            time.DeltaSeconds = seconds;
            Evaluate(graph, context, instances, guard);
            trace.Add((graph.StateMachine!.CurrentState(instances, guard).Id,
                graph.Data.Read<double>(context, instances, guard, example.Attacks)));
        }

        Step(0.1);
        Step(0.1, GuardLibrary.SeePlayer.Id);
        Step(0.1);
        Step(0.1, distance: 1);
        Step(0.1, GuardLibrary.Hit.Id);
        Step(0.1, distance: 3);
        Step(0.1, GuardLibrary.LosePlayer.Id);
        Step(2);
        Step(1.5);

        (ElementId, double)[] expected =
        [
            (example.Patrol, 0), (example.Chase, 0), (example.Chase, 0), (example.Attack, 1), (example.Attack, 2),
            (example.Chase, 2), (example.Search, 2), (example.Search, 2), (example.Patrol, 2),
        ];
        await Assert.That(string.Join(";", trace)).IsEqualTo(string.Join(";", expected));
    }

    [Test]
    public async Task NewNodesGetTheirTypesDefaultPayloads()
    {
        await Assert.That(new AddNodeCommand(GuardLibrary.Constant).Payload).IsEqualTo(new NumberPayload(0));
        await Assert.That(new AddNodeCommand(GuardLibrary.After).Payload).IsEqualTo(new AfterPayload(1));
        await Assert.That(new AddNodeCommand(GuardLibrary.Guarded).Payload).IsNull();
        await Assert.That(new AddNodeCommand(StateNodes.Transition).Payload).IsNull();
    }

    [Test]
    public async Task SavedGuardsSurviveATriggerRename()
    {
        var (example, _) = Compile(GuardLibrary.CreateState());
        var json = GraphJson.Serialize(example.Document);
        var renamed = new StableId(Guid.Parse("9a4d0000-0000-4000-8000-000000000f01"));
        var state = GuardLibrary.CreateState();
        var registry = new GraphTypeRegistry();
        GuardLibrary.Register(registry, renamed);
        var loaded = GraphJson.Deserialize(json, registry).Document!;
        var graph = (InterpretedGraph)new GraphInterpreter(GuardLibrary.CreateDataflow(), state: state)
            .Compile(loaded).Graph!;
        var instances = new InstanceBuffer(graph.InstanceLayout);
        var byNewName = instances.Allocate();
        var byOldName = instances.Allocate();
        var context = new EvaluationContext().Set(new StateTime());

        await Assert.That(() => Evaluate(graph, new EvaluationContext(), instances, byNewName))
            .ThrowsExactly<MissingFeatureException>();
        graph.Raise(instances, byNewName, renamed);
        graph.Raise(instances, byOldName, GuardLibrary.SeePlayer.Id);
        Evaluate(graph, context, instances, byNewName);
        Evaluate(graph, context, instances, byOldName);

        await Assert.That(() => GuardExample.Build(GuardLibrary.CreateDataflow(), state, new GraphTypeRegistry()))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(GraphValidator.Validate(loaded, state.Rules).Count).IsEqualTo(0);
        await Assert.That(graph.StateMachine!.CurrentState(instances, byNewName).Id).IsEqualTo(example.Chase);
        await Assert.That(graph.StateMachine.CurrentState(instances, byOldName).Id).IsEqualTo(example.Chase);
    }

    [Test]
    public async Task OneAssetThousandGuardsTickWithoutAllocating()
    {
        var (example, graph) = Compile(GuardLibrary.CreateState());
        var instances = new InstanceBuffer(graph.InstanceLayout, 1000);
        var guards = new InstanceHandle[1000];
        var time = new StateTime { DeltaSeconds = 0.5 };
        var context = new EvaluationContext().Set(time);
        for (var i = 0; i < guards.Length; i++)
        {
            guards[i] = instances.Allocate();
            graph.Initialize(context, instances, guards[i]);
        }

        Frames(graph, example, context, instances, guards, 0, 2);
        var allocated = Frames(graph, example, context, instances, guards, 2, 12);
        var states = guards.Select(guard => graph.StateMachine!.CurrentState(instances, guard).Id).Distinct().Count();

        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(states).IsGreaterThan(1);
    }

    [Test]
    public async Task TheInterpreterMatchesItselfOverAPatrolScenario()
    {
        var (example, _) = Compile(GuardLibrary.CreateState());
        var interpreter = new GraphInterpreter(GuardLibrary.CreateDataflow(), state: GuardLibrary.CreateState());
        var scenario = new Scenario("patrol", 4,
        [
            new EvaluateStep(0.1),
            new RaiseStep(GuardLibrary.SeePlayer.Id),
            new EvaluateStep(0.1),
            new SetVariableStep(example.Distance.Id, 1.0, Instance: 2),
            new EvaluateStep(0.1),
            new RaiseStep(GuardLibrary.Hit.Id, Instance: 2),
            new RaiseStep(GuardLibrary.LosePlayer.Id, Instance: 0),
            new EvaluateStep(0.1),
            new EvaluateStep(4),
        ]);

        var report = await DifferentialHarness.CompareAsync(new(example.Document), interpreter, interpreter, scenario);

        await Assert.That(report.Equivalent).IsTrue();
    }

    /// <summary>
    /// Runs frames and returns the bytes they allocated. Measuring in a synchronous method keeps closures and state
    /// machines of the async test method out of the measured window.
    /// </summary>
    static long Frames(
        ICompiledGraph graph,
        GuardExample example,
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle[] guards,
        int first,
        int end)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = first; frame < end; frame++)
        {
            Frame(graph, example, context, instances, guards, frame);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    static void Frame(
        ICompiledGraph graph,
        GuardExample example,
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle[] guards,
        int frame)
    {
        for (var i = 0; i < guards.Length; i++)
        {
            var guard = guards[i];
            graph.SetVariable(instances, guard, example.Distance.Id, (double)((i + frame) % 4));
            if ((i + frame) % 3 == 0)
            {
                graph.Raise(instances, guard, GuardLibrary.SeePlayer.Id);
            }
            if ((i + frame) % 5 == 0)
            {
                graph.Raise(instances, guard, GuardLibrary.Hit.Id);
            }
            if ((i + frame) % 7 == 0)
            {
                graph.Raise(instances, guard, GuardLibrary.LosePlayer.Id);
            }
            Evaluate(graph, context, instances, guard);
        }
    }

    static void Evaluate(
        ICompiledGraph graph,
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle guard)
    {
        var evaluation = graph.EvaluateAsync(context, instances, guard);
        if (!evaluation.IsCompleted)
        {
            throw new InvalidOperationException("The interpreter must complete synchronously.");
        }
        evaluation.GetAwaiter().GetResult();
    }

    static (GuardExample Example, InterpretedGraph Graph) Compile(StateSemantics state)
    {
        var registry = new GraphTypeRegistry();
        GuardLibrary.Register(registry);
        var data = GuardLibrary.CreateDataflow();
        var example = GuardExample.Build(data, state, registry);
        var result = new GraphInterpreter(data, state: state).Compile(example.Document);
        return (example, (InterpretedGraph)(result.Graph ?? throw new InvalidOperationException(
            string.Join("; ", result.Diagnostics))));
    }
}
