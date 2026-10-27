# Graph.Samples.Tests

TUnit acceptance tests for the shader, guard FSM and compositor samples. This project supplies the interpreter so sample libraries remain backend-independent.

Domain tests verify rendered values, cache behavior, persistence, required features, cancellation/progress, state traces and zero-allocation execution. [GeneratedRegistrationTests.cs](GeneratedRegistrationTests.cs) compares each generated module with its hand-written registration oracle: stable identities, ordered port declarations, defaults, serializers and persistence must agree, and differential scenarios must report no divergence.

The FSM comparison includes the hand library's simulated signal-rename release. A domain guarded transition is loaded from a version 1 payload through the generated registry, proving automatic migration registration, trigger/priority lifting and the missing-migration diagnostic. Builder tests cover matching typed ports, variable/reference handles, unsupported writes and atomic transition rollback. Compositor scenarios use `FrameComparer` through the differential options so distinct frame objects compare by content.

Run from the repository root:

```sh
dotnet test --project Graph.Samples.Tests
```

See the [shader](../Graph.Samples.Shader/README.md), [FSM](../Graph.Samples.Fsm/README.md), [compositor](../Graph.Samples.Compositor/README.md) and [generator](../Graph.Generator/README.md) READMEs for usage. [Graph.AotSmoke](../Graph.AotSmoke/README.md) checks the generated registrations in a native executable.
