# MASS4.Graph.Control

Explicit execution flow over the [core graph model](../Graph/README.md). Depends only on `MASS4.Graph`; reads and writes data through `IDataPlane`, which can be supplied by the dataflow package.

Call `ControlSemantics.RegisterTypes(registry)` for the built-in `exec` port type. Declare execution ports with `ExecInput` and `ExecOutput`, register domain `NodeType` entries, and bind their `IControlNode` implementations with `AddNode`. Behaviors execute through `Execute(ref ControlContext)` and can keep per-instance memory, read data, publish results and request subsequent execution. [Generated registration](../Graph.Generator/README.md) can emit the node bindings and port index constants.

Execution is depth-first in request order; fan-out follows target node identity order. Tick entries start each run. `MaxStepsPerRun`, `StackCapacity` and `MaxContinuations` bound execution. Inspect the run result when using `ControlProgram` directly; the [interpreter](../Graph.Interpreter/README.md) throws when a run exceeds its limits.

Supply `Rules` to `GraphEditor` so execution ports without a bound behavior are diagnosed as MG2001. Compile with the document, shared instance layout and optional data plane. Control owns only execution-flow ports, allowing dataflow and state edges to coexist in the same document.

See [semantics tests](../Graph.Semantics.Tests/README.md) for ordered branches, fan-out, memory, limits and mixed state/data/control graphs.
