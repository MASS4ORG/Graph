# Graph.Generator.Tests

TUnit compiler tests for the incremental registration generator. Runs Roslyn over fixture source, checks generator/compiler diagnostics and verifies that valid emitted modules and helpers compile. Roslyn is a private test dependency.

Fixtures cover explicit/missing/duplicate identities, rename conflicts, node-local port keys, malformed attributes, conversion shapes, declaration/storage mismatches and migration versions. Valid cases cover value/reference storage, JSON and single-value payload serializers, required dataflow features, semantics bindings and global/qualified module namespaces. A negative compilation case proves that incompatible typed port connections fail to compile.

Run from the repository root:

```sh
dotnet test --project Graph.Generator.Tests
```

Compiler fixtures complement [runtime sample equivalence](../Graph.Samples.Tests/README.md) and the [NativeAOT smoke app](../../Samples/Graph.AotSmoke/README.md). See the [generator README](../../Graph.Generator/README.md) for declarations and generated APIs.
