# Graph.Semantics.Tests

TUnit tests for dataflow, control and state semantics independently and in one mixed document. [Domain.cs](Domain.cs) provides explicit domain registrations and graph-building fixtures.

Coverage includes pull caching, coercions and conversions, wildcard inference, blackboard variables and declared outputs; execution order, fan-out, memory and control limits; and cyclic state machines, triggers, priority, custom transitions and per-instance state. The mixed-document tests verify the shared `IDataPlane` boundary and zero-allocation execution.

[StateMigrationTests.cs](StateMigrationTests.cs) loads the [version 1 fixture](Fixtures/state-machine-v1.json), checks trigger/priority lifting into annotations, runs the migrated machine and verifies round-trip and missing-migration behavior. Generated domain transition migration is additionally checked by [sample tests](../Graph.Samples.Tests/GeneratedRegistrationTests.cs).

Run from the repository root:

```sh
dotnet test --project Graph.Semantics.Tests
```

See the [Dataflow](../../Graph.Dataflow/README.md), [Control](../../Graph.Control/README.md) and [State](../../Graph.State/README.md) READMEs for the public contracts; use the [repository test target](../../README.md#build-and-test) for merged coverage.
