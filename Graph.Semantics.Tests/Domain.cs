using System.Globalization;

namespace MASS4.Graph.Semantics.Tests;

public sealed record ConstantPayload(double Value);

public sealed record IntPayload(int Value);

public sealed record LogPayload(string Label);

/// <summary>The mixed State × Dataflow × Control document and the nodes tests inspect.</summary>
public sealed record MixedDocument(
    DataflowSemantics Data,
    StateSemantics State,
    ControlSemantics Control,
    GraphEditor Editor,
    AddNodeCommand Idle,
    AddNodeCommand Walk,
    AddNodeCommand Threshold,
    AddNodeCommand Faster,
    AddNodeCommand Stride,
    AddNodeCommand Total);

/// <summary>Host input read by the volatile speed node.</summary>
public sealed class Locomotion
{
    public double Speed { get; set; }
}

/// <summary>Collects the labels of executed log nodes.</summary>
public sealed class Journal
{
    public List<string> Entries { get; } = [];
}

/// <summary>A test domain spanning dataflow, control and state node types.</summary>
static class Domain
{
    static int nextKey = 1000;

    public static readonly PortType Number = new(Id(1), "number") { ValueSerializer = new NumberSerializer() };
    public static readonly PortType Bool = new(Id(2), "bool");
    public static readonly PortType Int = new(Id(3), "int");
    public static readonly PortType Text = new(Id(4), "text");
    public static readonly PortType Any = new(Id(5), "any") { IsWildcard = true };

    public static readonly NodeType Constant = Type(101, "Constant", Out(Number));
    public static readonly NodeType Add = Type(102, "Add", In(Number), In(Number), Out(Number));
    public static readonly NodeType Scale = Type(103, "Scale", In(Number), Out(Number));
    public static readonly NodeType GreaterThan = Type(104, "Greater than", In(Number), In(Number), Out(Bool));
    public static readonly NodeType Not = Type(105, "Not", In(Bool), Out(Bool));
    public static readonly NodeType Speed = Type(106, "Speed", Out(Number));
    public static readonly NodeType Relay = Type(107, "Relay", In(Any), Out(Any));
    public static readonly NodeType IntConstant = Type(108, "Int", Out(Int));
    public static readonly NodeType NumberToText = Type(109, "Number to text", In(Number), Out(Text));
    public static readonly NodeType TextLength = Type(110, "Length", In(Text), Out(Number));
    public static readonly NodeType Collect = Type(111, "Collect",
        new(Key(), "items", PortDirection.Input, Number.Id, PortMultiplicity.Multiple), Out(Number));
    public static readonly NodeType Probe = Type(112, "Probe", Out(Number), Out(Text));

    public static readonly NodeType OnTick = Type(201, "On tick", ControlSemantics.ExecOutput(Key()));
    public static readonly NodeType Sequence = Type(202, "Sequence",
        ControlSemantics.ExecInput(Key()), ControlSemantics.ExecOutput(Key(), "first"),
        ControlSemantics.ExecOutput(Key(), "second"));
    public static readonly NodeType Branch = Type(203, "Branch",
        ControlSemantics.ExecInput(Key()), In(Bool), ControlSemantics.ExecOutput(Key(), "true"),
        ControlSemantics.ExecOutput(Key(), "false"));
    public static readonly NodeType Counter = Type(204, "Counter",
        ControlSemantics.ExecInput(Key()), ControlSemantics.ExecOutput(Key()), Out(Number));
    public static readonly NodeType Log = Type(205, "Log", ControlSemantics.ExecInput(Key()));
    public static readonly NodeType Forever = Type(206, "Forever",
        ControlSemantics.ExecInput(Key()), ControlSemantics.ExecOutput(Key()));

    public static readonly NodeType Walk = Type(301, "Walk", StateNodes.Enter(), StateNodes.Exit(), Out(Number));
    public static readonly NodeType Guarded = StateNodes.CreateGuardedTransition(Id(302), "Guarded", Bool.Id);

    public static readonly TriggerType Go = new(Id(401), "Go");
    public static readonly TriggerType Stop = new(Id(402), "Stop");

    public static StableId Id(int value) => new(new Guid($"00000000-0000-4000-8000-{value:D12}"));

    public static GraphTypeRegistry CreateRegistry()
    {
        var registry = new GraphTypeRegistry();
        foreach (var type in new[] { Number, Bool, Int, Text, Any })
        {
            registry.PortTypes.Register(type);
        }
        ControlSemantics.RegisterTypes(registry);
        StateNodes.Register(registry);
        DataflowNodes.Register(registry);
        NodeType[] nodes =
        [
            Constant, Add, Scale, GreaterThan, Not, Speed, Relay, IntConstant, NumberToText, TextLength, Collect,
            Probe, OnTick, Sequence, Branch, Counter, Log, Forever, Walk, Guarded,
        ];
        foreach (var node in nodes)
        {
            registry.NodeTypes.Register(node);
        }
        return registry;
    }

    public static DataflowSemantics CreateDataflow() => new DataflowSemantics()
        .AddValueType<double>(Number.Id)
        .AddValueType<bool>(Bool.Id)
        .AddValueType<int>(Int.Id)
        .AddReferenceType<string>(Text.Id)
        .AddCoercion<int, double>(Int.Id, Number.Id, static value => value)
        .AddNode(Constant.Id, new DataflowNode(static c => c.Write(0, ((ConstantPayload)c.Payload!).Value)))
        .AddNode(Add.Id, new DataflowNode(static c => c.Write(2, c.Read<double>(0) + c.Read<double>(1))))
        .AddNode(Scale.Id, new DataflowNode(static c => c.Write(1, c.Read<double>(0) * 2)))
        .AddNode(GreaterThan.Id, new DataflowNode(static c => c.Write(2, c.Read<double>(0) > c.Read<double>(1))))
        .AddNode(Not.Id, new DataflowNode(static c => c.Write(1, !c.Read<bool>(0))))
        .AddNode(Speed.Id, new DataflowNode(static c =>
            c.Write(0, c.Evaluation.TryGet<Locomotion>(out var locomotion) ? locomotion.Speed : 0), isVolatile: true))
        .AddNode(Relay.Id, new DataflowNode(static c => c.Copy(0, 1)))
        .AddNode(IntConstant.Id, new DataflowNode(static c => c.Write(0, ((IntPayload)c.Payload!).Value)))
        .AddNode(TextLength.Id, new DataflowNode(static c =>
            c.Write(1, (double)(c.ReadReference<string>(0)?.Length ?? 0))))
        .AddNode(Collect.Id, new DataflowNode(static c => c.Write(1, c.Read<double>(0))))
        .AddConversionNode(NumberToText, new DataflowNode(static c =>
            c.WriteReference(1, c.Read<double>(0).ToString(CultureInfo.InvariantCulture))));

    public static ControlSemantics CreateControl(ControlSemantics? semantics = null) => (semantics ?? new())
        .AddNode(OnTick.Id, new ControlNode(static (ref c) => c.Continue(0), isTickEntry: true))
        .AddNode(Sequence.Id, new ControlNode(static (ref c) =>
        {
            c.Continue(1);
            c.Continue(2);
        }))
        .AddNode(Branch.Id, new ControlNode(static (ref c) => c.Continue(c.Read<bool>(1) ? 2 : 3)))
        .AddNode(Counter.Id, new ControlNode(static (ref c) =>
        {
            ref var count = ref c.Memory<int>();
            count++;
            c.Write(2, (double)count);
            c.Continue(1);
        }, memorySize: sizeof(int)))
        .AddNode(Log.Id, new ControlNode(static (ref c) =>
        {
            if (c.Evaluation.TryGet<Journal>(out var journal))
            {
                journal.Entries.Add(((LogPayload)c.Payload!).Label);
            }
        }))
        .AddNode(Forever.Id, new ControlNode(static (ref c) => c.Continue(1)));

    public static StateSemantics CreateState()
    {
        var semantics = new StateSemantics()
            .AddState(Walk.Id, new WalkState())
            .AddTransition(Guarded.Id, GuardedTransition.Instance);
        semantics.Triggers.Register(Go);
        semantics.Triggers.Register(Stop);
        return semantics;
    }

    public static GraphEditor CreateEditor(DataflowSemantics data, params IEnumerable<IGraphRule>[] rules) =>
        new(new(CreateRegistry()), data.Conversions, [.. data.Rules, .. rules.SelectMany(static set => set)]);

    public static AddNodeCommand AddNode(this GraphEditor editor, NodeType type, object? payload = null)
    {
        var command = new AddNodeCommand(type, payload);
        Require(editor.Execute(command));
        return command;
    }

    public static ElementId Port(this AddNodeCommand node, int index) => node.PortId(node.Type.Ports[index].Key);

    public static CommandResult Link(
        this GraphEditor editor,
        AddNodeCommand from,
        int output,
        AddNodeCommand to,
        int input) =>
        editor.Connect(from.Port(output), to.Port(input));

    public static void Wire(this GraphEditor editor, AddNodeCommand from, int output, AddNodeCommand to, int input) =>
        Require(editor.Link(from, output, to, input));

    public static DataflowProgram CompileData(
        DataflowSemantics semantics,
        GraphDocument document,
        InstanceLayoutBuilder layout)
    {
        var diagnostics = new DiagnosticBag();
        return semantics.Compile(document, layout, diagnostics) ??
               throw new InvalidOperationException(string.Join("; ", diagnostics));
    }

    /// <summary>
    /// Entry → Idle ↔ Walk: Idle leaves when a dataflow guard sees speed above 0.5, Walk returns on Stop. Walk
    /// publishes its time in state, which dataflow doubles (stride) and adds to a control counter (total).
    /// </summary>
    public static MixedDocument BuildMixed()
    {
        var data = CreateDataflow();
        var state = CreateState();
        var control = CreateControl();
        var editor = CreateEditor(data, state.Rules, control.Rules);
        var entry = editor.AddNode(StateNodes.Entry);
        var idle = editor.AddNode(StateNodes.State);
        var walk = editor.AddNode(Walk);
        var start = editor.AddNode(Guarded, new TransitionPayload());
        var stop = editor.AddNode(StateNodes.Transition, new TransitionPayload(Stop.Id));
        var speed = editor.AddNode(Speed);
        var threshold = editor.AddNode(Constant, new ConstantPayload(0.5));
        var faster = editor.AddNode(GreaterThan);
        var stride = editor.AddNode(Scale);
        var total = editor.AddNode(Add);
        var tick = editor.AddNode(OnTick);
        var steps = editor.AddNode(Counter);
        editor.Wire(entry, 0, idle, 0);
        editor.Wire(idle, 1, start, 0);
        editor.Wire(start, 1, walk, 0);
        editor.Wire(walk, 1, stop, 0);
        editor.Wire(stop, 1, idle, 0);
        editor.Wire(speed, 0, faster, 0);
        editor.Wire(threshold, 0, faster, 1);
        editor.Wire(faster, 2, start, 2);
        editor.Wire(walk, 2, stride, 0);
        editor.Wire(walk, 2, total, 0);
        editor.Wire(steps, 2, total, 1);
        editor.Wire(tick, 0, steps, 0);
        return new(data, state, control, editor, idle, walk, threshold, faster, stride, total);
    }

    static void Require(CommandResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
        }
    }

    static NodeType Type(int id, string name, params PortDeclaration[] ports) => new(Id(id), name, ports);

    static PortDeclaration In(PortType type) => PortDeclaration.Input(Key(), type.DisplayName, type.Id);

    static PortDeclaration Out(PortType type) => PortDeclaration.Output(Key(), type.DisplayName, type.Id);

    static StableId Key() => Id(nextKey++);

    /// <summary>Saves number defaults as JSON numbers.</summary>
    sealed class NumberSerializer : IValueSerializer
    {
        public void Write(System.Text.Json.Utf8JsonWriter writer, object? value) =>
            writer.WriteNumberValue((double)value!);

        public object? Read(System.Text.Json.JsonElement element) => element.GetDouble();
    }

    /// <summary>Publishes its time in state on its data output.</summary>
    sealed class WalkState : IStateNode
    {
        public void OnEnter(StateContext context) => context.Write(2, 0.0);

        public void OnUpdate(StateContext context) => context.Write(2, context.TimeInState);
    }
}
