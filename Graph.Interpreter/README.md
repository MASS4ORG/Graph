# MASS4.Graph.Interpreter

Reference backend and differential-test oracle for the core, dataflow, control and state packages. Targets `net10.0`; requires dataflow semantics and accepts optional control/state semantics. It compiles only the semantics in which the document participates.

`GraphInterpreter.Compile(document)` or `CompileAsync(new GraphSource(document))` returns a graph and diagnostics. Check that the graph is present before allocating an `InstanceBuffer` from its layout. Register domain types separately; [generated modules](../Graph.Generator/README.md) can supply the registry and matching semantics.

`InterpretedGraph` implements `ICompiledGraph`. Each evaluation starts a data epoch, ticks a participating state machine using `StateTime`, then runs control tick entries. Hosts provide every `RequiredFeatures` entry before initialization/evaluation. Missing features and control runaway fail explicitly. Use typed variable writes and declared-output reads on `ICompiledGraph` so host code can work with other backends.

## Live editing

`InterpreterSession` subscribes to document changes and recompiles on refresh or the first evaluation after an edit. A successful recompile restarts instances; instance state is not migrated across edits. Failed compilation retains the last good graph and reports diagnostics. Dispose the session to unsubscribe from the document.

## Differential testing

`DifferentialHarness.CompareAsync` compiles one source with reference and candidate backends, then compares observations after initialization and every scenario step. `Scenario` supports evaluation, signal raises, variable writes and host-context configuration across multiple instances. Reports identify divergences by step, instance and element identity.

`DifferentialOptions` provides floating-point tolerance and type-specific content comparers. Use `Comparers` for reference values such as frames instead of changing domain-object equality. Backends must require matching feature sets; mismatches are MG4001 and prevent execution.

The interpreter is the execution oracle for future compiler backends. Generated registration currently compares independently configured interpreters using hand-written and generated registries; it does not generate an execution backend. See [interpreter tests](../Graph.Interpreter.Tests/README.md) and [sample equivalence tests](../Graph.Samples.Tests/README.md).
