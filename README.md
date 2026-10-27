# MASS4.Graph

Headless, domain-neutral node graphs for material/shader graphs, state machines, visual scripting, behavior trees, compositors and automation. The libraries target `net10.0`; the core has no package, GUI or engine dependency and supports trimming and NativeAOT.

## Packages

- [MASS4.Graph](Graph/README.md): document model, stable identities, commands/undo, diagnostics, persistence, subgraphs and instance/backend contracts.
- [MASS4.Graph.Dataflow](Graph.Dataflow/README.md): pull evaluation, per-instance caching, wildcard inference, conversions, variables and declared outputs.
- [MASS4.Graph.Control](Graph.Control/README.md): explicit execution flow, ordered branches, behavior memory and bounded execution.
- [MASS4.Graph.State](Graph.State/README.md): cyclic state machines, signals, guards, priorities, timers and transition migrations.
- [MASS4.Graph.Interpreter](Graph.Interpreter/README.md): reference execution backend, live-edit sessions and differential harness.
- [MASS4.Graph.Generator](Graph.Generator/README.md): compile-time registration, serializers, semantics bindings, diagnostics and typed editing helpers.

Core defines the contracts. Each semantics package depends only on core and owns its own flow/value rules; they compose in a single document through `IDataPlane`. The interpreter composes the semantics. The generator is a compile-time analyzer reference and adds no runtime dependency to consuming libraries.

## Samples

- [Shader/material](Graph.Samples.Shader/README.md): exposed parameters, textures, typed colour math and explicit conversion nodes.
- [Guard FSM](Graph.Samples.Fsm/README.md): distance guards, self-transitions, timed transitions, signal renames and shared assets across instances.
- [Video compositor](Graph.Samples.Compositor/README.md): timeline-driven frames, reusable buffers, asynchronous host rendering, progress and cancellation.

Each sample keeps a hand-written registration oracle alongside generated declarations. Acceptance tests compare registration, persistence and execution, including signal renames and domain transition migration. Sample libraries accept backend-neutral contracts and do not reference the interpreter or an engine.

## Build and test

Run from the repository root with a .NET 10 SDK:

```sh
./build.sh TestReport --configuration Release --verbosity minimal
```

The test target writes merged coverage to `coverage/coverage.xml` and HTML to `coverage/report/`. See the [.nuke build harness](.nuke/README.md) for build ownership.

- [Core tests](Graph.Tests/README.md): model, identity, commands, persistence and evaluation contracts.
- [Semantics tests](Graph.Semantics.Tests/README.md): dataflow/control/state behavior, composition and migrations.
- [Interpreter tests](Graph.Interpreter.Tests/README.md): backend behavior, live editing and differential-harness failures.
- [Generator tests](Graph.Generator.Tests/README.md): declaration diagnostics and emitted-code compilation.
- [Sample tests](Graph.Samples.Tests/README.md): domain results, registration equivalence and host contracts.
- [NativeAOT smoke app](Graph.AotSmoke/README.md): generated registration/persistence/compilation with JSON reflection disabled.

Release notes are in the [changelog](docs/CHANGELOG.md). Public usage guidance lives in each project's README.
