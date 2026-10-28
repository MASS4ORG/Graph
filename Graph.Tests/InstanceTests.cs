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
    public async Task ArraySlotsAreAlignedAndIndependentPerInstance()
    {
        var builder = new InstanceLayoutBuilder();
        var flag = builder.Add<byte>();
        var timers = builder.AddArray<double>(3);
        var empty = builder.AddArray<int>(0);
        var buffer = new InstanceBuffer(builder.Build(), capacity: 1);
        var first = buffer.Allocate();
        var second = buffer.Allocate();

        buffer.GetSpan(first, timers)[2] = 1.5;
        buffer.Get(second, flag) = 1;

        await Assert.That(timers.Offset).IsEqualTo(8);
        await Assert.That(buffer.Layout.Stride).IsEqualTo(32);
        await Assert.That(buffer.GetSpan(first, timers)[2]).IsEqualTo(1.5);
        await Assert.That(buffer.GetSpan(second, timers)[2]).IsEqualTo(0);
        await Assert.That(buffer.GetSpan(first, empty).Length).IsEqualTo(0);
        await Assert.That(() => builder.AddArray<int>(-1)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ReferenceSlotsSurviveGrowthAndClearOnRelease()
    {
        var builder = new InstanceLayoutBuilder();
        var frame = builder.AddReference<string>();
        var label = builder.AddReference<string>();
        var buffer = new InstanceBuffer(builder.Build(), capacity: 1);
        var first = buffer.Allocate();
        buffer.SetReference(first, label, "kept");
        var second = buffer.Allocate();
        buffer.SetReference(second, frame, "frame");

        buffer.Release(second);
        var reused = buffer.Allocate();

        await Assert.That(buffer.Layout.ReferenceCount).IsEqualTo(2);
        await Assert.That(buffer.GetReference(first, label)).IsEqualTo("kept");
        await Assert.That(buffer.GetReference(first, frame)).IsNull();
        await Assert.That(buffer.GetReference(reused, frame)).IsNull();
    }
}
