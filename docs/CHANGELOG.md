# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `MASS4.Graph` core: headless graph model, stable identity registry, reversible commands with undo, diagnostics, versioned JSON persistence, blackboard, subgraph contracts and instance/evaluation contracts
- `MASS4.Graph.Dataflow`: pull evaluation with per-instance caching, acyclicity, coercions and wildcard inference #13
- `MASS4.Graph.Dataflow`: blackboard variable reads with per-instance values and default initialization #13
- `MASS4.Graph.Control`: exec flow with deterministic ordering, early-out, node memory and tick entries #14
- `MASS4.Graph.State`: state machines with typed triggers, guarded transitions and per-instance state and timers #15
- `MASS4.Graph.Interpreter`: reference backend for mixed documents with a live-edit session; `ICompiledGraph` gains signals, variables and observations #17
- `DifferentialHarness`: scenario-driven comparison of any backend with the interpreter, reporting divergences by step, instance and element #18
- Shader/material contract sample with hand registration and the registration friction log #21
- FSM contract sample: one guard asset across 1000 instances with typed triggers, a self-transition and timed transitions #22
- Video compositor contract sample: frame values, timeline feature and asynchronous rendering, with no engine references #23
- Declared graph outputs (output-scope variables defined by `DataflowNodes.Output`) with typed reads on `ICompiledGraph` #10
- Signals in `GraphTypeRegistry` replace the State trigger registry, so trigger renames live with every other rename #15
- `NodeType.DefaultPayload` for newly added nodes #8
- Required context features: `EvaluationContext.Get`/`Require`, `MissingFeatureException`, and `RequiredFeatures` on behaviors, programs and `ICompiledGraph`; missing features fail instead of defaulting #18
- Typed `ICompiledGraph.SetVariable<T>` and per-type comparers in the differential harness #18
- `IDataPlane`, instance array and reference slots, and `GraphDocument.GetNode`/`GetPort` so semantics compose in one document #16
- Node annotations: registered `AnnotationType` identities with a closed set of value kinds, `SetAnnotationCommand` and `AddNodeCommand.Annotations`; edit-time metadata the owning semantics consumes at compilation, never read by evaluators #9
- State `Trigger` and `Priority` annotations replace `TransitionPayload`; every transition type waits for its trigger annotation, so custom transitions (timed, guarded) combine with triggers without re-implementing them #15
- Document schema 2 with node annotations; schema 1 documents still load and State migrates version 1 transitions, lifting their payload into annotations #9 #15
