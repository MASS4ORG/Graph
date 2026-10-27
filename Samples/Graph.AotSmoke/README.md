# Graph.AotSmoke

NativeAOT acceptance executable for generated registration. Explicitly registers the generated shader, FSM and compositor modules, builds each sample, saves and reloads its document, and compiles the loaded graph with the reference interpreter. JSON reflection is disabled by the project, so missing source-generated metadata fails the check.

Run these commands from the repository root for Linux x64:

```sh
dotnet publish Graph.AotSmoke/Graph.AotSmoke.csproj -c Release -f net10.0 -r linux-x64 -p:PublishAot=true -p:NoLocalPackages=true -m:4 -nodeReuse:false -o artifacts/aot-smoke
./artifacts/aot-smoke/Graph.AotSmoke
```

Successful execution prints that all three generated samples registered, persisted and compiled with reflection disabled; failure throws and returns a nonzero exit status. The [CI workflow](../.github/workflows/build-and-test.yml) runs the same check after the normal suite. This executable complements [compiler tests](../Graph.Generator.Tests/README.md) and [runtime differential tests](../Graph.Samples.Tests/README.md).
