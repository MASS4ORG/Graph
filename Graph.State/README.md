# MASS4.Graph.State

State machines over the [core graph model](../Graph/README.md). Depends only on `MASS4.Graph`; boolean guards and state outputs compose with data evaluation through `IDataPlane`.

## Registration and execution

Call `StateNodes.Register(registry)` to register state-flow ports, built-in entry/state/transition node types, trigger/priority annotations and the built-in transition migration. `StateSemantics` binds those built-ins by default. Bind domain types through `AddEntry`, `AddState` and `AddTransition`; one type has one state role. `IsEntry`, `TryGetState` and `TryGetTransition` report the bindings.

A machine requires one connected entry node, and each transition must join two states. Cycles and self-transitions are legal. Each instance stores its current state, time in state, raised triggers and behavior memory. A tick takes at most one transition and then clears triggers; higher priorities are checked first, with transition node identity breaking ties. The [interpreter](../Graph.Interpreter/README.md) gets tick duration from the required `StateTime` host feature. `StateContext` and `TransitionContext` wrap a `StateFrame`; `StateMachineProgram` is the interpreter's frame and generated backends supply their own.

## Signals, payloads and migrations

Triggers are registered `SignalType` identities in `GraphTypeRegistry.Signals`; old identities resolve through the same registry's rename entries. Set a transition's trigger and priority with `StateNodes.Annotations(trigger, priority)`, passing the `SignalType` declaration rather than its identity. `StateGraphBuilder` adds typed entry, state and transition nodes (`AddTransition(editor, from.Exit, to.Enter, signal, priority)`) as atomic edits, and hosts raise with `graph.Raise(instances, instance, signal)`. Edits and loads both store a renamed signal under its current identity; names never take part in resolution. These are compile-time annotations; the machine checks the trigger before the transition behavior runs. Custom evaluation payloads such as a timer duration remain independent of those annotations.

`StateNodes.CreateGuardedTransition` creates a version 2 transition with a boolean condition input. Hand-written registrations must also call `StateNodes.AddTransitionMigration(registry, domainType.Id)`: it lifts version 1 trigger/priority payloads into annotations. [Generated guarded declarations](../Graph.Generator/README.md) emit this migration automatically. A domain's custom payload migration remains explicit; the legacy State migration must not strip unrelated evaluation payloads.

Use `Rules` during editing and check compilation diagnostics for machine completeness. MG3001–MG3007 cover flow roles, signals, entry/transition wiring and misplaced annotations. Missing migrations preserve old data and report a diagnostic.

See the [guard FSM sample](../Samples/Graph.Samples.Fsm/README.md) and [migration/composition tests](../Tests/Graph.Semantics.Tests/README.md).
