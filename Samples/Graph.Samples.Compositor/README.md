# Graph.Samples.Compositor

Headless video-compositor contract sample with number and frame values. References only the core and dataflow packages; it has no engine or interpreter dependency.

`CompositorLibrary.Register(registry)` supplies the hand-written oracle registration; `CreateDataflow()` binds storage and behaviors. `CompositionExample.Build(data, registry)` combines timeline time, a scrolling ramp, a sine pulse, a fade envelope and a frame blend. Its result is a declared frame output variable.

`CompositionRenderer.RenderAsync` accepts any `ICompiledGraph`, renders frame by frame, reports progress and observes cancellation between frames. Evaluation requires `Timeline` and `RenderSettings` features; missing features throw instead of supplying defaults. Frame-producing nodes reuse their previous output buffers when dimensions match.

`FrameComparer` provides content equality for `DifferentialOptions.Comparers`. Domain `Frame` instances keep ordinary object equality. Payloads preserve their JSON member names (`speed`, `frequency`, `duration`).

[GeneratedDeclarations.cs](GeneratedDeclarations.cs) defines `Generated.CompositorModule` with the same identities, ports, defaults, payload formats and feature requirements. It demonstrates generated reference-output reuse with static delegates. See [compositor tests](../Graph.Samples.Tests/CompositorSampleTests.cs) for frame content, persistence, cancellation, missing features and reuse, and [generated-registration tests](../Graph.Samples.Tests/GeneratedRegistrationTests.cs) for comparison against the hand-written oracle.
