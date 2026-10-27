# Graph.Samples.Fsm

Guard state-machine contract sample combining state and dataflow semantics. References the core and semantics packages; it has no interpreter dependency.

`GuardLibrary.Register(registry)` supplies the hand-written oracle registration; `CreateDataflow()` and `CreateState()` supply matching semantics. `GuardExample.Build(data, state, registry)` constructs Patrol, Chase, Attack and Search states. Distance guards control Chase/Attack, Hit causes an Attack self-transition, and an After transition times the return from Search. The attack state publishes its entry count from per-instance memory.

Hosts provide `StateTime` when evaluating through `ICompiledGraph`, set the external distance variable and raise registered signals. Triggers and transition priorities are annotations consumed at compile time; `AfterPayload` contains only its duration. Renderer/editor integrations can use the same backend-neutral contract.

[GeneratedDeclarations.cs](GeneratedDeclarations.cs) defines `Generated.GuardModule`, preserving the domain node/port identities. Its SeePlayer signal models the hand library's optional `renamedSeePlayer` release: the old identity becomes a rename entry. The guarded-transition declaration automatically registers its version 1 migration, while the timed transition keeps its own payload format. Generated handles provide typed state/data ports and atomic transition creation.

[FSM tests](../Graph.Samples.Tests/FsmSampleTests.cs) cover the state trace, self-transitions, signal renames, missing features and zero-allocation execution across 1000 instances. [Generated-registration tests](../Graph.Samples.Tests/GeneratedRegistrationTests.cs) compare both registrations and prove domain migration, typed variable writes and failed-transition rollback. See the [State package](../Graph.State/README.md) for the transition/migration contract.
