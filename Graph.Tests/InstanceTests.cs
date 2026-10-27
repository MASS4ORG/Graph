namespace MASS4.Graph.Tests;

public class InstanceTests
{
    [Test]
    public async Task LayoutBuilderAlignsSlotsAndStride()
    {
        var builder = new InstanceLayoutBuilder();
        var flag = builder.Add<byte>();
        var timer = builder.Add<double>();
        var state = builder.Add<int>();

        var layout = builder.Build();

        await Assert.That(flag.Offset).IsEqualTo(0);
        await Assert.That(timer.Offset).IsEqualTo(8);
        await Assert.That(state.Offset).IsEqualTo(16);
        await Assert.That(layout.Stride).IsEqualTo(24);
        await Assert.That(layout.SlotCount).IsEqualTo(3);
        await Assert.That(new InstanceLayoutBuilder().Build().Stride).IsEqualTo(1);
    }

    [Test]
    public async Task BufferGrowsReusesRecordsAndRejectsStaleHandles()
    {
        var builder = new InstanceLayoutBuilder();
        var value = builder.Add<long>();
        var buffer = new InstanceBuffer(builder.Build(), capacity: 0);
        var handles = Enumerable.Range(0, 10).Select(_ => buffer.Allocate()).ToArray();
        for (var i = 0; i < handles.Length; i++)
        {
            buffer.Get(handles[i], value) = i * 100;
        }

        buffer.Release(handles[3]);
        var reused = buffer.Allocate();

        await Assert.That(buffer.Capacity).IsGreaterThanOrEqualTo(10);
        await Assert.That(buffer.Get(handles[9], value)).IsEqualTo(900);
        await Assert.That(reused.Index).IsEqualTo(3);
        await Assert.That(buffer.Get(reused, value)).IsEqualTo(0);
        await Assert.That(buffer.IsAlive(handles[3])).IsFalse();
        await Assert.That(buffer.IsAlive(reused)).IsTrue();
        await Assert.That(buffer.Count).IsEqualTo(10);
        await Assert.That(buffer.GetRecord(reused).Length).IsEqualTo(8);
        await Assert.That(() => buffer.Get(handles[3], value)).ThrowsExactly<ArgumentException>();
        await Assert.That(() => buffer.Release(new(42, 0))).ThrowsExactly<ArgumentException>();
        await Assert.That(() => new InstanceBuffer(builder.Build(), -1)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task OneAssetThousandInstancesTickWithoutAllocating()
    {
        var editor = CreateEditor();
        var idle = editor.AddNode(State);
        var walk = editor.AddNode(State);
        editor.Connect(idle.PortId(ExitKey), walk.PortId(EnterKey));
        editor.Connect(walk.PortId(ExitKey), idle.PortId(EnterKey));
        var compiled = new TogglingStateMachine(editor.Document);
        var instances = new InstanceBuffer(compiled.InstanceLayout, 1000);
        var handles = new InstanceHandle[1000];
        for (var i = 0; i < handles.Length; i++)
        {
            handles[i] = instances.Allocate();
            compiled.Initialize(instances, handles[i]);
        }
        var context = new EvaluationContext();

        Tick(compiled, context, instances, handles);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 9; frame++)
        {
            Tick(compiled, context, instances, handles);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(compiled.CurrentState(instances, handles[0])).IsEqualTo(0);
        await Assert.That(compiled.Ticks(instances, handles[999])).IsEqualTo(10);
    }

    static void Tick(
        ICompiledGraph graph,
        EvaluationContext context,
        InstanceBuffer instances,
        InstanceHandle[] handles)
    {
        foreach (var handle in handles)
        {
            var evaluation = graph.EvaluateAsync(context, instances, handle);
            if (!evaluation.IsCompleted)
            {
                throw new InvalidOperationException("The test artifact must complete synchronously.");
            }
            evaluation.GetAwaiter().GetResult();
        }
    }

    /// <summary>A hand-written stand-in for a compiled state machine that alternates states each tick.</summary>
    sealed class TogglingStateMachine : ICompiledGraph
    {
        readonly InstanceSlot<int> state;
        readonly InstanceSlot<int> ticks;
        readonly InstanceSlot<float> timer;

        public TogglingStateMachine(GraphDocument document)
        {
            AssetId = document.AssetId;
            var builder = new InstanceLayoutBuilder();
            state = builder.Add<int>();
            ticks = builder.Add<int>();
            timer = builder.Add<float>();
            InstanceLayout = builder.Build();
        }

        public StableId AssetId { get; }

        public InstanceLayout InstanceLayout { get; }

        public void Initialize(InstanceBuffer instances, InstanceHandle instance) => instances.Get(instance, state) = 0;

        public ValueTask EvaluateAsync(EvaluationContext context, InstanceBuffer instances, InstanceHandle instance)
        {
            ref var current = ref instances.Get(instance, state);
            current = 1 - current;
            instances.Get(instance, ticks)++;
            instances.Get(instance, timer) += 1f / 60f;
            return ValueTask.CompletedTask;
        }

        public int CurrentState(InstanceBuffer instances, InstanceHandle instance) => instances.Get(instance, state);

        public int Ticks(InstanceBuffer instances, InstanceHandle instance) => instances.Get(instance, ticks);
    }
}
