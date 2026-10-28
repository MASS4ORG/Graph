# MASS4.Graph

Headless, domain-neutral node-graph foundation shared by every MASS4 node- or graph-based system: shader and material graphs, visual scripting, state machines, behavior trees, compositors and automation.

The core has no GUI, engine or framework dependency and is trimming- and AOT-safe.

## What the core provides

- **Model**: documents, nodes, ports (direction, multiplicity, type) and edges. Applications attach payload objects; they never inherit framework node classes. Cycles are allowed; acyclicity is a rule of dataflow semantics.
- **Stable identity**: node types, port types and host functions are addressed by opaque `StableId`s resolved through `IdentityRegistry<T>`. Renames are registry entries, so saved graphs never depend on CLR type names.
- **Commands and undo**: every mutation is an `IGraphCommand` executed by `GraphEditor`, which validates the changes, rolls back commands that produce errors and keeps undo/redo history.
- **Diagnostics**: `{id, severity, elementId, message}` records shared by editors, tools and CI; `GraphValidator` validates whole documents.
- **Port compatibility**: pluggable strategies (`ExactPortCompatibility`, `ConversionTable`) with implicit coercions, wildcards and automatically inserted conversion nodes.
- **Persistence**: deterministic, merge-friendly versioned JSON (`GraphJson`), per-node-type migrations, rename resolution, unknown-node and unknown-property preservation, and layout stored inline, separately or not at all.
- **Blackboard and subgraphs**: typed variables (local, input, output, external) reuse the port type system; a subgraph's exposed variables define the ports of the node referencing it.
- **Instances and evaluation**: assets hold no instance state. Semantics declare per-instance slots through `InstanceLayoutBuilder`; `InstanceBuffer` stores many instances contiguously without per-access allocation. `IGraphBackend` and `ICompiledGraph` define the compilation boundary with progress and cancellation.

## Semantics packages

Each package depends only on the core and claims its own port types, so one document can mix them: state cycles stay legal while data edges stay acyclic. Control and state nodes read guards and publish outputs through the core `IDataPlane`, which the dataflow program implements.

- **`MASS4.Graph.Dataflow`**: register value types, coercions and node behaviors on `DataflowSemantics`; `Compile` returns a `DataflowProgram` that pulls values on demand, caches non-volatile results per instance and infers wildcard port types.
- **`MASS4.Graph.Control`**: exec ports, depth-first execution in request order, early-out, per-instance node memory, tick entries and step/stack limits.
- **`MASS4.Graph.State`**: entry, state and transition nodes; typed triggers resolved through a rename-aware registry; priority-ordered guarded transitions; per-instance current state and time in state.

Programs reserve their slots in a shared `InstanceLayoutBuilder`, so one `InstanceBuffer` holds the state of every semantics for every instance:

```csharp
var layout = new InstanceLayoutBuilder();
var data = dataflow.Compile(document, layout, diagnostics)!;
var machine = states.Compile(document, layout, data, diagnostics)!;
var instances = new InstanceBuffer(layout.Build(), capacity: 1000);
machine.Tick(context, instances, instances.Allocate(), deltaSeconds);
```

## Example

```csharp
var number = new PortType(StableId.Parse("6f1c..."), "number");
var add = new NodeType(StableId.Parse("a2b4..."), "Add",
[
    PortDeclaration.Input(StableId.Parse("..."), "a", number.Id),
    PortDeclaration.Input(StableId.Parse("..."), "b", number.Id),
    PortDeclaration.Output(StableId.Parse("..."), "sum", number.Id),
]);

var registry = new GraphTypeRegistry();
registry.PortTypes.Register(number);
registry.NodeTypes.Register(add);

var editor = new GraphEditor(new GraphDocument(registry));
var first = new AddNodeCommand(add);
var second = new AddNodeCommand(add);
editor.Execute(new CompositeCommand("Add nodes", [first, second]));
var result = editor.Connect(first.PortId(sumKey), second.PortId(aKey));
editor.Undo();

var json = GraphJson.Serialize(editor.Document);
var loaded = GraphJson.Deserialize(json, registry);
```

## Build

```sh
./build.sh test
```
