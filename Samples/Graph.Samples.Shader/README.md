# Graph.Samples.Shader

Headless material-graph contract sample. References the core and dataflow packages; the interpreter is supplied by the host/tests. Values include scalars, colours, UV coordinates and texture references.

`ShaderLibrary.Register(registry)` supplies the hand-written oracle registration; `CreateDataflow()` binds its behavior and storage. `MaterialExample.Build(data, registry)` constructs a graph with exposed tint, intensity and texture inputs, texture sampling, colour arithmetic/interpolation and a scalar-to-colour conversion. It declares albedo as an output-scope variable rather than exposing an internal port to the host.

`MaterialRenderer.RenderAsync` takes any `ICompiledGraph`, instances and the albedo variable identity. It supplies the required `Fragment` feature while shading. Parameter subgraphs cache per instance; fragment-dependent work is volatile. Constant node types declare default payloads, and colour values preserve their explicit array JSON format.

[GeneratedDeclarations.cs](GeneratedDeclarations.cs) expresses the same persistent identities, port order, defaults and serializers through [the registration generator](../../Graph.Generator/README.md). `Generated.ShaderModule` creates the corresponding registry entries and dataflow semantics. The original registration remains independent so equivalence is testable.

See [shader tests](../../Tests/Graph.Samples.Tests/ShaderSampleTests.cs) for expected pixels, caching, persistence and missing-feature failures, and [generated-registration tests](../../Tests/Graph.Samples.Tests/GeneratedRegistrationTests.cs) for differential execution and typed builders/input handles.

[Assets/material.graph.json](Assets/material.graph.json) is the sample saved as an asset. `Generated.MaterialGraph` compiles it at build time into a generated backend (`MaterialGraph.Backend`); [generated-backend tests](../../Tests/Graph.Samples.Tests/GeneratedBackendTests.cs) compare it with the interpreter.
