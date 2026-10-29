# Graph.AotSmoke

NativeAOT acceptance executable for generated registration. Explicitly registers the generated shader, FSM and compositor modules, builds each sample (the guard through its typed generated builders), saves and reloads its document, and compiles the loaded graph with the reference interpreter. It then raises a typed trigger on the reloaded guard and checks the transition. JSON reflection is disabled by the project, so missing source-generated metadata fails the check.

Run these commands from the repository root for Linux x64:

```sh
dotnet publish Tests/Graph.AotSmoke/Graph.AotSmoke.csproj -c Release -f net10.0 -r linux-x64 -p:PublishAot=true -p:NoLocalPackages=true -m:4 -nodeReuse:false -o artifacts/aot-smoke
./artifacts/aot-smoke/Graph.AotSmoke
```

Successful execution prints that all three generated samples registered, persisted and compiled with reflection disabled; failure throws and returns a nonzero exit status. The [CI workflow](../../.github/workflows/build-and-test.yml) runs the same check after the normal suite. This executable complements [compiler tests](../../Tests/Graph.Generator.Tests/README.md) and [runtime differential tests](../../Tests/Graph.Samples.Tests/README.md).
