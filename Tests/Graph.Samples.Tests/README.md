# Graph.Samples.Tests

TUnit acceptance tests for the shader, guard FSM and compositor samples. This project supplies the interpreter so sample libraries remain backend-independent.

Domain tests verify rendered values, cache behavior, persistence, required features, cancellation/progress, state traces and zero-allocation execution. [GeneratedRegistrationTests.cs](GeneratedRegistrationTests.cs) compares each generated module with its hand-written registration oracle: stable identities, ordered port declarations, rename entries, migrations, defaults, serializers, semantics bindings and persistence must agree, and differential scenarios must report no divergence.

The FSM comparison includes the hand library's simulated signal-rename release. A domain guarded transition is loaded from a version 1 payload through the generated registry, proving automatic migration registration, trigger/priority lifting and the missing-migration diagnostic. Builder tests cover matching typed ports, variable/reference handles, unsupported writes and atomic transition rollback. [TypedTriggerTests.cs](TypedTriggerTests.cs) build the guard through generated builders and typed triggers and require the same document shape and state trace as the hand-built guard. They also cover saved triggers following a signal rename and identity-only resolution between same-named signals. [GroupNeutralityTests.cs](GroupNeutralityTests.cs) wrap each sample in nested, partly collapsed, persisted groups and require identical validation diagnostics and differential results. [GeneratedBackendTests.cs](GeneratedBackendTests.cs) run each compiled sample asset against the interpreter through the differential harness. They require identical layouts, features and outputs, zero-allocation guard ticks, rejection of changed topologies, and payload edits that need no rebuild. Compositor scenarios use `FrameComparer` through the differential options so distinct frame objects compare by content.

Run from the repository root:

```sh
dotnet test --project Graph.Samples.Tests
```

See the [shader](../../Samples/Graph.Samples.Shader/README.md), [FSM](../../Samples/Graph.Samples.Fsm/README.md), [compositor](../../Samples/Graph.Samples.Compositor/README.md) and [generator](../../Graph.Generator/README.md) READMEs for usage. [Graph.AotSmoke](../../Samples/Graph.AotSmoke/README.md) checks the generated registrations in a native executable.
