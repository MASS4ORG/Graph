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

## Interpreter and differential testing

`MASS4.Graph.Interpreter` is the always-available reference backend. `GraphInterpreter` compiles a document with every semantics it participates in into an `InterpretedGraph` (`ICompiledGraph`): one evaluation starts a new data evaluation, ticks the state machine by the context's `StateTime`, then runs control tick entries, synchronously and without allocating. `InterpreterSession` gives editors live edit: it recompiles on the first evaluation after a document change and keeps the last good graph running when an edit breaks compilation.

`DifferentialHarness` runs a `Scenario` (evaluations, signals, variable writes, host input changes) against a reference and a candidate backend and reports every observation that differs, by step, instance and element identity. Generated backends must be equivalent to the interpreter on every sample.

## Contract samples

Three headless samples are API tests, not demos, and run in CI through the interpreter and the differential harness:

- `Graph.Samples.Shader`: a material graph with colour, scalar, UV and texture ports, exposed parameters and an editor-inserted conversion node.
- `Graph.Samples.Fsm`: a guard state machine with cyclic states, a self-transition, typed triggers, dataflow guards and timed transitions; one asset ticks 1000 instances without allocating and survives a trigger rename.
- `Graph.Samples.Compositor`: a video compositor driven by a timeline feature, rendered asynchronously with progress and cancellation; it references no engine assembly.

They register their types by hand on purpose; `docs/registration-friction.md` records what that costs and is the requirement list for source-generated registration.

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
