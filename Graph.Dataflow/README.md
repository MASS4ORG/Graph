# MASS4.Graph.Dataflow

Pull-based data evaluation over the [core graph model](../Graph/README.md). Depends only on `MASS4.Graph`; control and state machines compose with it through the core `IDataPlane` contract.

Register each port type's CLR storage with `DataflowSemantics.AddValueType<T>` for unmanaged values or `AddReferenceType<T>` for references. Bind evaluation through `AddNode` and `IDataflowNode`/`DataflowNode`. Coercions and explicit conversion nodes extend the `Conversions` compatibility strategy; generic ports use wildcard type inference. [Generated declarations](../Graph.Generator/README.md) emit these registrations consistently.

## Evaluation and caching

Compile with `Compile(document, layout, diagnostics)` using a shared `InstanceLayoutBuilder`. The resulting `DataflowProgram` pulls dependencies when a value is read. Non-volatile subgraphs stay cached per instance; volatile nodes evaluate once per evaluation epoch. Variable writes invalidate the instance cache. Declared `RequiredFeatures` flow into the compiled graph and must be present in the evaluation context.

Control and state behaviors may publish data through `IDataPlane.Write` only to outputs that dataflow does not compute. Such outputs are volatile roots; every publish advances the epoch. Publishing to an input or a dataflow-computed output throws.

## Variables and declared outputs

Call `DataflowNodes.Register(registry)` for the built-in variable-read and output nodes. `GetVariable` uses a `VariableReference` payload and infers its output type from the referenced variable. Define a host-visible result as a `VariableScope.Output` variable targeted by one `DataflowNodes.Output` node. Hosts read it by variable identity through `ICompiledGraph`, independently of the backend. Output variables cannot be read inside the graph.

Rules and compilation reject data cycles, unresolved/conflicting types, unknown variables, invalid output roles and duplicate output definitions. Diagnostic codes are MG1001–MG1011. Supply `Conversions` and `Rules` to `GraphEditor` for edit-time validation.

See the [shader](../Graph.Samples.Shader/README.md) and [compositor](../Graph.Samples.Compositor/README.md) samples and [semantics tests](../Graph.Semantics.Tests/README.md).
