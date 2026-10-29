# Graph.Interpreter.Tests

TUnit tests for the reference backend, live editing and differential harness. Exercises mixed semantics through `ICompiledGraph`, typed outputs and variable writes, required host features, zero-allocation instance evaluation, and session recompile/restart behavior.

Differential cases compare the interpreter with deliberately perturbed candidates, proving detection of differing values, missing observations and incompatible feature sets. Type-specific comparers and floating-point tolerance are tested independently of domain-object equality.

Run from the repository root:

```sh
dotnet test --project Graph.Interpreter.Tests
```

See the [interpreter README](../../Graph.Interpreter/README.md) for backend/session usage and [sample tests](../Graph.Samples.Tests/README.md) for generated-registration equivalence over domain graphs.
