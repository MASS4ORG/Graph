# MASS4.Graph

Headless graph model, editing, persistence and evaluation contracts. Targets `net10.0`, has no package dependencies, and supports trimming and NativeAOT. It references no semantics, interpreter, GUI or engine package.

## Model and identity

`GraphDocument` owns nodes, directed ports, edges, blackboard variables, subgraphs and optional layout. Node payloads are application values; domains do not inherit a framework node class. Cycles are legal in the model; semantics packages impose their own rules.

`StableId` identifies registered types and signals; `ElementId` identifies elements within an asset. Register `NodeType`, `PortType`, `SignalType`, `AnnotationType` and `HostFunction` entries through `GraphTypeRegistry`. Identities are explicit and independent of CLR names. `IdentityRegistry<T>` resolves old identities through rename entries; port keys are stable within their owning node type. Use [generated registration](../Graph.Generator/README.md) to declare registrations and typed helpers together.

## Editing

Apply `IGraphCommand` objects through `GraphEditor`. Edits validate direction, uniqueness, multiplicity, type compatibility and any supplied rules. Failed edits roll back; successful edits support undo and redo. `CompositeCommand` makes a group atomic. `NodeType.DefaultPayload` supplies new-node defaults; loading an asset never applies those defaults.

```csharp
var number = new PortType(StableId.Parse("10000000-0000-4000-8000-000000000001"), "number");
var input = StableId.Parse("10000000-0000-4000-8000-000000000002");
var output = StableId.Parse("10000000-0000-4000-8000-000000000003");
var relay = new NodeType(StableId.Parse("10000000-0000-4000-8000-000000000004"), "Relay",
    [PortDeclaration.Input(input, "in", number.Id), PortDeclaration.Output(output, "out", number.Id)]);
var registry = new GraphTypeRegistry();
registry.PortTypes.Register(number);
registry.NodeTypes.Register(relay);
var editor = new GraphEditor(new GraphDocument(registry));
var first = new AddNodeCommand(relay);
var second = new AddNodeCommand(relay);
var result = editor.Execute(new CompositeCommand("Connect relays",
    [first, second, new ConnectCommand(first.PortId(output), second.PortId(input))]));
if (!result.Succeeded)
{
    throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
}
editor.Undo();
```

## Persistence and metadata

`GraphJson.Serialize` writes deterministic JSON; `GraphJson.Deserialize` resolves renames, applies registered version migrations and reconciles ports. The writer emits schema 2 and the reader accepts schemas 1–2. Unknown nodes, payloads, annotation types and properties are preserved so unavailable domain packages do not destroy asset data. Supply an `IValueSerializer` for non-null payloads and persisted variable values; `JsonTypeInfoSerializer<T>` adapts source-generated JSON metadata without reflection.

Annotations use registered identities and the closed `AnnotationKind` value set. They are edit/compile-time metadata validated by the owning semantics package. Evaluation data belongs in payloads, variables or ports.

## Evaluation contracts

`InstanceLayoutBuilder` reserves unmanaged and reference slots; `InstanceBuffer` holds per-instance state separately from the document. `EvaluationContext` provides typed host features, cancellation and progress. `Get<T>` and `Require` throw `MissingFeatureException` when required services are absent.

`IGraphBackend` compiles a `GraphSource` to an `ICompiledGraph`. Hosts initialize and evaluate instances, raise registered signals, write typed variables, and read declared outputs through `ReadOutput<T>` or `ReadOutputReference<T>`. `Observe` provides boxed inspection values. Typed variable handles bind once to an asset and reject incompatible identities, scopes or unsupported writes.

See [core tests](../Graph.Tests/README.md), the [semantics packages](../README.md#packages) and the [reference interpreter](../Graph.Interpreter/README.md).
