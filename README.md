# MASS4.Graph

Headless, domain-neutral node graphs for material/shader graphs, state machines, visual scripting, behavior trees, compositors and automation. The libraries target `net10.0`; the core has no package, GUI or engine dependency and supports trimming and NativeAOT.

## Packages

- [MASS4.Graph](Graph/README.md): document model, stable identities, commands/undo, diagnostics, persistence, subgraphs and instance/backend contracts.
- [MASS4.Graph.Dataflow](Graph.Dataflow/README.md): pull evaluation, per-instance caching, wildcard inference, conversions, variables and declared outputs.
- [MASS4.Graph.Control](Graph.Control/README.md): explicit execution flow, ordered branches, behavior memory and bounded execution.
- [MASS4.Graph.State](Graph.State/README.md): cyclic state machines, signals, guards, priorities, timers and transition migrations.
- [MASS4.Graph.Interpreter](Graph.Interpreter/README.md): reference execution backend, live-edit sessions and differential harness.
- [MASS4.Graph.Generator](Graph.Generator/README.md): analyzer package for compile-time registration, serializers, semantics bindings, diagnostics, typed editing helpers and generated backends for saved assets.

Core defines the contracts. Each semantics package depends only on core and owns its own flow/value rules; they compose in a single document through `IDataPlane`. The interpreter composes the semantics. The generator is a compile-time analyzer reference and adds no runtime dependency to consuming libraries.

## Samples

- [Shader/material](Samples/Graph.Samples.Shader/README.md): exposed parameters, textures, typed colour math and explicit conversion nodes.
- [Guard FSM](Samples/Graph.Samples.Fsm/README.md): distance guards, self-transitions, timed transitions, signal renames and shared assets across instances.
- [Video compositor](Samples/Graph.Samples.Compositor/README.md): timeline-driven frames, reusable buffers, asynchronous host rendering, progress and cancellation.
- [Custom nodes](Samples/Graph.Samples.CustomNodes/README.md): a user assembly referencing only the core and the analyzer package.
