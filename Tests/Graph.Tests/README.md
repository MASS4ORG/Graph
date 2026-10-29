# Graph.Tests

Core model and contract tests using TUnit. Covers stable identity and rename chains, registry conflicts, port compatibility, reversible/atomic edits, diagnostics, subgraphs, instance storage, required host features and deterministic JSON persistence.

Persistence cases include version migrations, unknown data preservation, annotations and schema compatibility. Group tests cover nesting, ungrouping, cycle rejection, membership on node removal, collapse as layout-only state, deterministic round trips and warnings for inconsistent saved groups. Core tests exercise the backend contracts without referencing a GUI or engine.

Run from the repository root:

```sh
dotnet test --project Graph.Tests
```

Use the [repository test target](../../README.md#build-and-test) for Release coverage across all projects. API usage belongs in the [core README](../../Graph/README.md).
