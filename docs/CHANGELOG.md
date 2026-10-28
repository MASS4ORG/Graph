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
- `IDataPlane`, instance array and reference slots, and `GraphDocument.GetNode`/`GetPort` so semantics compose in one document #16
