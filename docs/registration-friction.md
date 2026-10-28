# Hand-registration friction log

The contract samples register their node types by hand on purpose. Every point of friction is recorded here; together they are the requirements for source-generated registration (#11). When the generator lands, its output for each sample is differential-tested against the hand-written registration: identical stable identities, rename entries included.

Each entry names the sample that surfaced it, what happened, and what the generator (or, where marked, the core) must do.

## From the shader sample (#21)

### R1 — Every identity is a hand-written GUID

Port types, node types and every port key need an explicit `StableId`. The sample uses a private `Id(n)` helper with a per-library GUID prefix to keep this manageable. Nothing detects a collision with another library, and a copy-pasted node type keeps its source's identity until registration throws at run time.

Requirement: identities are declared once, next to the type, and checked at build time for duplicates within the assembly. They are never derived from names, so renames stay safe.

### R2 — Behaviors address ports by declaration index

`Lerp` writes `c.Write(3, …)` because its output is the fourth declared port. Inserting or reordering a port silently rewires every behavior, and reading a colour port as `float` fails only at evaluation.

Requirement: generated, typed per-node accessors (for example `inputs.T`, `outputs.Out = …`) or index constants derived from the declaration, so port order and types are checked by the compiler.

### R3 — One node, several registration surfaces

A node needs its `NodeType` registered in `GraphTypeRegistry` and its behavior bound in `DataflowSemantics`; a conversion node needs a third call (`AddConversionNode`). A port type is registered twice: as a `PortType` and as a dataflow value type with a CLR type that must agree. A mismatch only fails at run time.

Requirement: one declaration emits every registration consistently, as one `IGraphTypeModule` plus the semantics bindings.

### R4 — Serializers are written twice

Each port type needs a value serializer for variable defaults, and each payload type a payload serializer. A payload that wraps one value (`ScalarPayload`) duplicates its value serializer.

Requirement: generated, reflection-free payload serializers that reuse the value serializer of single-value payloads.

### R5 — Port keys must be public constants

Document builders wire by port key, so every key is a public member. To keep the identity count down the sample shares generic keys (`AKey`, `BKey`) across unrelated node types, which couples them.

Requirement: generated per-node key members (`Lerp.Keys.T`), each a distinct identity.

### R6 — No default payload

`new AddNodeCommand(ShaderLibrary.ScalarConstant)` without a payload leaves it null, and the behavior's `(ScalarPayload)c.Payload!` throws at evaluation, far from the cause.

Requirement: node declarations carry a default payload; the generator emits it (core candidate: a default-payload factory on `NodeType` used by `AddNodeCommand`).

### R7 — Every consumer rewrites the same builder helpers

The sample and every test project reimplement `Add`, `Connect` and `Require` over `AddNodeCommand` and `PortId(key)`. Wiring mistakes surface as command diagnostics at run time.

Requirement: a generated typed builder that returns typed port handles, so connecting a colour output to a scalar input does not compile unless a conversion exists.

### R8 — No declared graph output (core)

The renderer must be handed the albedo input port of the material output node; the document has no notion of the outputs a host reads.

Requirement (core, not generator): a declared output role, for example output-scope blackboard variables that a set-variable node writes, so hosts read outputs by variable identity.

## From the FSM sample (#22)

### R9 — Custom transition payloads lose trigger and priority (core)

`AfterPayload` cannot also carry a trigger or a priority: `TransitionPayload` is sealed, and both `ITransitionNode.Priority` and `TransitionContext.TriggerSatisfied` understand only `TransitionPayload`. A timed transition that should also wait for a trigger would re-implement both.

Requirement (core, State): a payload contract such as `ITransitionPayload { Trigger; Priority }` that custom payloads implement; generated payloads compose the standard fields.

### R10 — State roles are yet another registration surface

Each state or transition type is bound with `AddState` or `AddTransition` on `StateSemantics`, separately from its `NodeType` registration. The attack state's data output also depends on a dataflow value type registered elsewhere. Three registries must agree on one node.

Requirement: the node declaration names its role and behavior; the generator emits the binding with the type (extends R3).

### R11 — Trigger identities and renames live in a different registry

Triggers are registered in `StateSemantics.Triggers`, not `GraphTypeRegistry`, so one asset's identities are split across registries. Simulating a release that renames a trigger meant restructuring `CreateState` with a branch: the old identity must stop being registered and become a rename entry (registering both throws).

Requirement: trigger declarations and `[RenamedFrom]`-style rename entries are emitted together into the semantics registry, with a build-time check that a renamed identity is never also registered.

### R12 — State behaviors also publish by port index

`AttackState` writes port 2. `StateContext.PortIndex(key)` exists as a workaround but scans the ports on every call.

Requirement: the typed accessors of R2 cover state and control behaviors too.

### R13 — Transitions take three commands each

Every transition is add node, wire `from`, wire `to`; the sample wraps this in a `Join` helper.

Requirement: the generated builder of R7 offers `machine.Transition(from, to, payload)`.

### R14 — Per-instance host input is addressed by element identity

The guard's distance is an external variable written with `SetVariable(instance, distance.Id, value)`; hosts must carry the variable's `ElementId`.

Requirement: generated, typed variable handles per asset (`guard.Distance.Set(instance, 3.0)`) resolved once.

## From the video compositor sample (#23)

### R15 — Object values need content equality for differential testing

The harness compares observations with `Equals`. `Frame` must override `Equals` and `GetHashCode` by content, or every frame from a second backend reports as divergent. Nothing in registration says how values of a reference type compare.

Requirement: value-type declarations carry an equality (or digest) the harness uses; the generator emits it or registers a comparer per port type (harness candidate: comparers in `DifferentialOptions`).

### R16 — Reusing output buffers is an index-addressed idiom

To avoid allocating a frame per evaluation, nodes read their own previous output with `ReadReference<Frame>(outputIndex)` and write the same object back. It works, but nothing names or documents the pattern.

Requirement: typed outputs offer reuse directly (`outputs.Frame.Reuse(width, height)`), building on R2.

### R17 — Host features are fetched, and silently defaulted, in every node

`Time`, `Ramp`, `Pulse` and every frame-producing node call `TryGet<Timeline>` or `TryGet<RenderSettings>` and fall back to defaults when the host forgot them, so a missing feature renders a plausible but wrong image.

Requirement: node declarations list the context features they need; the generator emits that list, and backends report a missing feature as a diagnostic before evaluating (core candidate: required features on the compiled graph).

### R18 — Single-value payloads, again

Three payloads each hold one number. The sample wrote a generic `NumberPayloadSerializer<T>` to avoid three copies, confirming R4.

### R19 — Typed output reads are interpreter-bound (core)

The renderer must take an `InterpretedGraph` to read the output frame through `Data.ReadReference`; the backend-neutral `ICompiledGraph` only offers boxed `Observe`. A generated backend would need its own renderer.

Requirement (core): typed reads of declared outputs on `ICompiledGraph`, together with R8.

## From the annotations change (R9)

### R20 — Domain transition types register their migration separately

`GuardLibrary.Guarded` is created with `StateNodes.CreateGuardedTransition` at version 2, but its saved version 1 documents only migrate if the domain also calls `StateNodes.AddTransitionMigration(registry, Guarded.Id)`. Forgetting it loads old assets with `MigrationUnavailable` and preserved payloads, not lifted triggers.

Requirement: the generator emits the migration registration for every transition type built from the State helpers, with the type.

### Guard 4 note

No domain stashed semantics in annotations in this change: the only annotation types are State's trigger and priority, both consumed by the State compiler. `AfterPayload` stays a payload because its seconds are read by the behavior at evaluation time.

## Status after the core batch

- Resolved in the core: R6 (`NodeType.DefaultPayload`), R8 and R19 (output-scope variables defined by `DataflowNodes.Output`, typed `ReadOutput`/`ReadOutputReference` on `ICompiledGraph`; the samples' renderers now take any backend), R11's split registry (triggers are `SignalType`s in `GraphTypeRegistry.Signals`), R14's boxing (typed `ICompiledGraph.SetVariable<T>`).
- Resolved in the harness: R15 (`DifferentialOptions.Comparers`; `Frame` no longer overrides equality), R17 (behaviors declare `RequiredFeatures`, backends call `EvaluationContext.Require`, nodes use `Get<T>`; the samples' silent fallbacks are gone and missing features throw `MissingFeatureException`).
- Resolved after the owner decision on the serialized format: R9. Trigger and priority are `StateNodes.Trigger` and `StateNodes.Priority` annotations, registered identities validated by State (MG3007 on non-transitions, MG3003 for unknown signals) and consumed when the machine compiles. The machine gates every transition on its trigger before its behavior runs, so `After` takes a trigger like any other transition. Schema 2 stores annotations; State's version 1 → 2 migration lifts old transition payloads, with an old-format fixture in the State tests.
- Still generator scope: R1–R5, R7, R10, R11's rename emission, R12, R13, R14's typed handles, R16, R18.

## Summary for #11

- Generator scope: R1–R7, R10–R14, R16–R18, R20.
- Core changes the generator depends on, all landed: R8 and R19 (declared outputs with typed reads), R9 (annotations), R6 (default payloads on `NodeType`).
- Harness, landed: R15 (comparers for object values) and R17 (required context features).
- Acceptance: for each sample, the generated registration equals the hand-written one (identities, port keys, rename entries, value and payload serialization) and the differential harness reports no divergence between documents built with either.
