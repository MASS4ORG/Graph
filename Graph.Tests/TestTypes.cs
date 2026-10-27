using System.Text.Json.Serialization;

namespace MASS4.Graph.Tests;

/// <summary>Payload of the constant node.</summary>
public sealed record ConstantPayload(double Value);

/// <summary>Payload of the constant node at version 2, which renamed its property.</summary>
public sealed record ConstantPayloadV2(double Amount);

[JsonSerializable(typeof(ConstantPayload))]
[JsonSerializable(typeof(ConstantPayloadV2))]
[JsonSerializable(typeof(double))]
partial class TestJsonContext : JsonSerializerContext;

/// <summary>A small domain used by the tests: numbers, text, triggers and a few node types.</summary>
static class TestTypes
{
    public static readonly StableId NumberId = Id(1);
    public static readonly StableId TextId = Id(2);
    public static readonly StableId AnyId = Id(3);
    public static readonly StableId TriggerId = Id(4);

    public static readonly StableId ValueKey = Id(101);
    public static readonly StableId AKey = Id(102);
    public static readonly StableId BKey = Id(103);
    public static readonly StableId SumKey = Id(104);
    public static readonly StableId InKey = Id(105);
    public static readonly StableId OutKey = Id(106);
    public static readonly StableId EnterKey = Id(107);
    public static readonly StableId ExitKey = Id(108);

    public static readonly PortType Number = new(NumberId, "number")
    {
        ValueSerializer = new JsonTypeInfoSerializer<double>(TestJsonContext.Default.Double),
    };

    public static readonly PortType Text = new(TextId, "text");
    public static readonly PortType Any = new(AnyId, "any") { IsWildcard = true };
    public static readonly PortType Trigger = new(TriggerId, "trigger");

    public static readonly NodeType Constant = new(Id(201), "Constant",
        [PortDeclaration.Output(ValueKey, "value", NumberId)])
    {
        PayloadSerializer = new JsonTypeInfoSerializer<ConstantPayload>(TestJsonContext.Default.ConstantPayload),
    };

    public static readonly NodeType Add = new(Id(202), "Add",
    [
        PortDeclaration.Input(AKey, "a", NumberId),
        PortDeclaration.Input(BKey, "b", NumberId),
        PortDeclaration.Output(SumKey, "sum", NumberId),
    ]);

    public static readonly NodeType Print = new(Id(203), "Print", [PortDeclaration.Input(InKey, "text", TextId)]);

    public static readonly NodeType ToText = new(Id(204), "To text",
    [
        PortDeclaration.Input(InKey, "number", NumberId),
        PortDeclaration.Output(OutKey, "text", TextId),
    ]);

    public static readonly NodeType State = new(Id(205), "State",
    [
        new(EnterKey, "enter", PortDirection.Input, TriggerId, PortMultiplicity.Multiple),
        PortDeclaration.Output(ExitKey, "exit", TriggerId),
    ]);

    public static readonly NodeType Relay = new(Id(206), "Relay",
    [
        PortDeclaration.Input(InKey, "in", AnyId),
        PortDeclaration.Output(OutKey, "out", AnyId),
    ]);

    public static StableId Id(int value) => new(new Guid($"00000000-0000-4000-8000-{value:D12}"));

    public static GraphTypeRegistry CreateRegistry()
    {
        var registry = new GraphTypeRegistry();
        foreach (var type in new[] { Number, Text, Any, Trigger })
        {
            registry.PortTypes.Register(type);
        }
        foreach (var type in new[] { Constant, Add, Print, ToText, State, Relay })
        {
            registry.NodeTypes.Register(type);
        }
        return registry;
    }

    public static GraphEditor CreateEditor(IPortCompatibility? compatibility = null) =>
        new(new(CreateRegistry(), Id(900)), compatibility);

    public static AddNodeCommand AddNode(this GraphEditor editor, NodeType type, object? payload = null)
    {
        var command = new AddNodeCommand(type, payload);
        var result = editor.Execute(command);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
        }
        return command;
    }
}
