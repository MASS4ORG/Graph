namespace MASS4.Graph.Semantics.Tests;

public class StateTests
{
    [Test]
    public async Task TriggersCycleStatesAndTimersAdvance()
    {
        var machine = Machine.Build(CreateState());
        var instance = machine.Instances.Allocate();
        var context = new EvaluationContext();

        await Assert.That(() => machine.Program.CurrentState(machine.Instances, instance))
            .ThrowsExactly<InvalidOperationException>();
        var started = machine.Program.Tick(context, machine.Instances, instance, 0.1);
        var idle = machine.Program.CurrentState(machine.Instances, instance).Id;
        machine.Program.Raise(machine.Instances, instance, machine.Program.ResolveTrigger(Go.Id));
        var walked = machine.Program.Tick(context, machine.Instances, instance, 0.1);
        machine.Program.Tick(context, machine.Instances, instance, 0.25);
        var elapsed = machine.Data.Read<double>(context, machine.Instances, instance, machine.Walking.Port(2));
        machine.Program.Raise(machine.Instances, instance, machine.Program.ResolveTrigger(Stop.Id));
        machine.Program.Tick(context, machine.Instances, instance, 0.1);
        var back = machine.Program.CurrentState(machine.Instances, instance).Id;

        await Assert.That(started).IsFalse();
        await Assert.That(idle).IsEqualTo(machine.Idle.NodeId);
        await Assert.That(walked).IsTrue();
        await Assert.That(elapsed).IsEqualTo(0.25);
        await Assert.That(back).IsEqualTo(machine.Idle.NodeId);
        await Assert.That(machine.Program.TimeInState(machine.Instances, instance)).IsEqualTo(0);
        await Assert.That(machine.Program.StateCount).IsEqualTo(2);
    }

    [Test]
    public async Task HigherPriorityTransitionsWin()
    {
        var state = CreateState();
        var editor = CreateEditor(CreateDataflow(), state.Rules);
        var entry = editor.AddNode(StateNodes.Entry);
        var idle = editor.AddNode(StateNodes.State);
        var low = editor.AddNode(StateNodes.State);
        var high = editor.AddNode(StateNodes.State);
        editor.Wire(entry, 0, idle, 0);
        Join(editor, idle, low, new TransitionPayload(Go.Id));
        Join(editor, idle, high, new TransitionPayload(Go.Id, Priority: 5));
        var layout = new InstanceLayoutBuilder();
        var program = state.Compile(editor.Document, layout, null, new())!;
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();

        program.Initialize(new(), instances, instance);
        program.Raise(instances, instance, program.ResolveTrigger(Go.Id));
        program.Tick(new(), instances, instance, 0);

        await Assert.That(program.CurrentState(instances, instance).Id).IsEqualTo(high.NodeId);
    }

    [Test]
    public async Task RenamedTriggersStillResolve()
    {
        var state = CreateState();
        var jump = new TriggerType(Id(410), "Jump");
        state.Triggers.Register(jump);
        state.Triggers.AddRename(Id(409), jump.Id);
        var editor = CreateEditor(CreateDataflow(), state.Rules);
        var entry = editor.AddNode(StateNodes.Entry);
        var ground = editor.AddNode(StateNodes.State);
        var air = editor.AddNode(StateNodes.State);
        editor.Wire(entry, 0, ground, 0);
        Join(editor, ground, air, new TransitionPayload(Id(409)));
        var layout = new InstanceLayoutBuilder();
        var program = state.Compile(editor.Document, layout, null, new())!;
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();

        program.Raise(instances, instance, program.ResolveTrigger(Id(409)));
        program.Tick(new(), instances, instance, 0);

        await Assert.That(program.CurrentState(instances, instance).Id).IsEqualTo(air.NodeId);
        await Assert.That(program.TryResolveTrigger(Id(499), out _)).IsFalse();
        await Assert.That(() => program.ResolveTrigger(Id(499))).ThrowsExactly<KeyNotFoundException>();
    }

    [Test]
    public async Task EditorsRejectInvalidFlowAndUnknownTriggers()
    {
        var loose = new NodeType(Id(350), "Loose", [StateNodes.Enter()]);
        var state = CreateState();
        var editor = CreateEditor(CreateDataflow(), state.Rules);
        editor.Document.Registry.NodeTypes.Register(loose);
        var a = editor.AddNode(StateNodes.State);
        var b = editor.AddNode(StateNodes.State);
        var stray = editor.AddNode(loose);

        var direct = editor.Link(a, 1, b, 0);
        var unknown = editor.Execute(new AddNodeCommand(StateNodes.Transition, new TransitionPayload(Id(499))));
        var unbound = editor.Link(a, 1, stray, 0);

        await Assert.That(direct.Diagnostics.Single().Id).IsEqualTo(StateDiagnosticIds.InvalidFlow);
        await Assert.That(unknown.Diagnostics.Single().Id).IsEqualTo(StateDiagnosticIds.UnknownTrigger);
        await Assert.That(unbound.Succeeded).IsTrue();
        await Assert.That(unbound.Diagnostics.Single().Id).IsEqualTo(StateDiagnosticIds.UnboundFlowNode);
        await Assert.That(() => state.AddState(StateNodes.Transition.Id, new CountingState()))
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task CompilationRequiresOneConnectedEntryAndJoinedTransitions()
    {
        var state = CreateState();
        var editor = CreateEditor(CreateDataflow(), state.Rules);
        var idle = editor.AddNode(StateNodes.State);
        var none = Errors(state, editor.Document);
        var entry = editor.AddNode(StateNodes.Entry);
        var unconnected = Errors(state, editor.Document);
        editor.Wire(entry, 0, idle, 0);
        var dangling = editor.AddNode(StateNodes.Transition);
        editor.Wire(idle, 1, dangling, 0);
        var halfJoined = Errors(state, editor.Document);
        editor.AddNode(StateNodes.Entry);
        var twoEntries = Errors(state, editor.Document);

        await Assert.That(none).IsEqualTo(StateDiagnosticIds.EntryCount);
        await Assert.That(unconnected).IsEqualTo(StateDiagnosticIds.EntryUnconnected);
        await Assert.That(halfJoined).IsEqualTo(StateDiagnosticIds.TransitionUnconnected);
        await Assert.That(twoEntries).Contains(StateDiagnosticIds.EntryCount);
    }

    [Test]
    public async Task StatesKeepMemoryAndCustomTransitionsQueryTriggers()
    {
        var counting = new NodeType(Id(360), "Counting", [StateNodes.Enter(), StateNodes.Exit(), Walk.Ports[2]]);
        var manual = new NodeType(Id(361), "Manual", [StateNodes.From(), StateNodes.To()]);
        var state = CreateState()
            .AddState(counting.Id, new CountingState())
            .AddTransition(manual.Id, new StopTransition());
        var data = CreateDataflow();
        var editor = CreateEditor(data, state.Rules);
        editor.Document.Registry.NodeTypes.Register(counting);
        editor.Document.Registry.NodeTypes.Register(manual);
        var entry = editor.AddNode(StateNodes.Entry);
        var busy = editor.AddNode(counting);
        var done = editor.AddNode(StateNodes.State);
        editor.Wire(entry, 0, busy, 0);
        var transition = editor.AddNode(manual);
        editor.Wire(busy, 1, transition, 0);
        editor.Wire(transition, 1, done, 0);
        var layout = new InstanceLayoutBuilder();
        var plane = CompileData(data, editor.Document, layout);
        var program = state.Compile(editor.Document, layout, plane, new())!;
        var instances = new InstanceBuffer(layout.Build());
        var instance = instances.Allocate();

        program.Tick(new(), instances, instance, 0);
        program.Tick(new(), instances, instance, 0);
        var count = plane.Read<double>(new(), instances, instance, busy.Port(2));
        program.Raise(instances, instance, program.ResolveTrigger(Stop.Id));
        program.Tick(new(), instances, instance, 0);

        await Assert.That(count).IsEqualTo(2);
        await Assert.That(program.CurrentState(instances, instance).Id).IsEqualTo(done.NodeId);
    }

    [Test]
    public async Task StatesWithoutDataPlaneOrMemoryThrow()
    {
        var hungry = new NodeType(Id(370), "Hungry", [StateNodes.Enter(), StateNodes.Exit()]);
        var state = CreateState().AddState(hungry.Id, new HungryState());
        var noData = Machine.Build(CreateState(), withData: false);
        var editor = CreateEditor(CreateDataflow(), state.Rules);
        editor.Document.Registry.NodeTypes.Register(hungry);
        editor.Wire(editor.AddNode(StateNodes.Entry), 0, editor.AddNode(hungry), 0);
        var layout = new InstanceLayoutBuilder();
        var program = state.Compile(editor.Document, layout, null, new())!;
        var instances = new InstanceBuffer(layout.Build());
        var walker = noData.Instances.Allocate();
        noData.Program.Raise(noData.Instances, walker, noData.Program.ResolveTrigger(Go.Id));

        await Assert.That(() => noData.Program.Tick(new(), noData.Instances, walker, 0))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => program.Initialize(new(), instances, instances.Allocate()))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task TransitionPayloadsRoundTrip()
    {
        var state = CreateState();
        var editor = CreateEditor(CreateDataflow(), state.Rules);
        editor.AddNode(StateNodes.Transition, new TransitionPayload(Go.Id, 3));
        editor.AddNode(StateNodes.Transition, new TransitionPayload());

        var loaded = GraphJson.Deserialize(GraphJson.Serialize(editor.Document), CreateRegistry()).Document!;

        var payloads = loaded.Nodes.Select(node => node.Payload).OfType<TransitionPayload>().ToArray();
        await Assert.That(payloads).Contains(new TransitionPayload(Go.Id, 3));
        await Assert.That(payloads).Contains(new TransitionPayload());
    }

    [Test]
    public async Task OneAssetThousandInstancesTickWithoutAllocating()
    {
        var machine = Machine.Build(CreateState(), guarded: true);
        var handles = new InstanceHandle[1000];
        var instances = new InstanceBuffer(machine.Instances.Layout, handles.Length);
        for (var i = 0; i < handles.Length; i++)
        {
            handles[i] = instances.Allocate();
        }
        var context = new EvaluationContext().Set(new Locomotion { Speed = 1 });
        var stop = machine.Program.ResolveTrigger(Stop.Id);

        Frame(machine.Program, context, instances, handles, stop, 0);
        Frame(machine.Program, context, instances, handles, stop, 1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 2; frame < 12; frame++)
        {
            Frame(machine.Program, context, instances, handles, stop, frame);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(machine.Program.CurrentState(instances, handles[999]).Id).IsEqualTo(machine.Walking.NodeId);
    }

    static void Frame(
        StateMachineProgram program,
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle[] handles,
        TriggerHandle stop,
        int frame)
    {
        foreach (var handle in handles)
        {
            if (frame % 2 == 0)
            {
                program.Raise(instances, handle, stop);
            }
            program.Tick(context, instances, handle, 1.0 / 60);
        }
    }

    static string Errors(StateSemantics state, GraphDocument document)
    {
        var diagnostics = new DiagnosticBag();
        state.Compile(document, new(), null, diagnostics);
        return string.Join(",", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id));
    }

    static void Join(GraphEditor editor, AddNodeCommand from, AddNodeCommand to, TransitionPayload payload)
    {
        var transition = editor.AddNode(StateNodes.Transition, payload);
        editor.Wire(from, 1, transition, 0);
        editor.Wire(transition, 1, to, 0);
    }

    /// <summary>Entry → Idle ↔ Walk, through a Go trigger (or a speed guard) and a Stop trigger.</summary>
    sealed record Machine(
        StateMachineProgram Program,
        DataflowProgram Data,
        InstanceBuffer Instances,
        AddNodeCommand Idle,
        AddNodeCommand Walking)
    {
        public static Machine Build(StateSemantics state, bool guarded = false, bool withData = true)
        {
            var data = CreateDataflow();
            var editor = CreateEditor(data, state.Rules);
            var entry = editor.AddNode(StateNodes.Entry);
            var idle = editor.AddNode(StateNodes.State);
            var walk = editor.AddNode(Walk);
            editor.Wire(entry, 0, idle, 0);
            var payload = new TransitionPayload(guarded ? null : Go.Id);
            var go = editor.AddNode(guarded ? Guarded : StateNodes.Transition, payload);
            editor.Wire(idle, 1, go, 0);
            editor.Wire(go, 1, walk, 0);
            if (guarded)
            {
                var speed = editor.AddNode(Speed);
                var threshold = editor.AddNode(Constant, new ConstantPayload(0.5));
                var faster = editor.AddNode(GreaterThan);
                editor.Wire(speed, 0, faster, 0);
                editor.Wire(threshold, 0, faster, 1);
                editor.Wire(faster, 2, go, 2);
            }
            var stop = editor.AddNode(StateNodes.Transition, new TransitionPayload(Stop.Id));
            editor.Wire(walk, 1, stop, 0);
            editor.Wire(stop, 1, idle, 0);
            var layout = new InstanceLayoutBuilder();
            var plane = CompileData(data, editor.Document, layout);
            var program = state.Compile(editor.Document, layout, withData ? plane : null, new())!;
            return new(program, plane, new(layout.Build()), idle, walk);
        }
    }

    /// <summary>Counts its updates in instance memory and publishes the count.</summary>
    sealed class CountingState : IStateNode
    {
        public int MemorySize => sizeof(int);

        public void OnUpdate(StateContext context)
        {
            ref var count = ref context.Memory<int>();
            count++;
            var port = context.PortIndex(Walk.Ports[2].Key);
            context.Write(port, (double)count);
            if (context.Payload is not null || context.Read<double>(port) != count)
            {
                throw new InvalidOperationException("The published count must read back unchanged.");
            }
        }
    }

    sealed class HungryState : IStateNode
    {
        public int MemorySize => 1;

        public void OnEnter(StateContext context) => context.Memory<long>() = 1;
    }

    /// <summary>Fires on the Stop trigger without naming it in a payload.</summary>
    sealed class StopTransition : ITransitionNode
    {
        public bool ShouldFire(TransitionContext context) =>
            !context.HasTrigger && context.IsRaised(Stop.Id) && context.TimeInState >= 0 &&
            context.Payload is null && context.Node.TypeId == Id(361);
    }
}
