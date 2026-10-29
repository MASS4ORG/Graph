# Graph.Samples.CustomNodes

A user assembly declaring its own nodes with only the core package and the `MASS4.Graph.Generator` analyzer. It references no semantics package, so the generated `ReviewModule` registers types, renames, migrations and payload serializers but binds no evaluation. [Declarations.cs](Declarations.cs) declares a reference-type note value, a versioned sticky-note node with a migration and a renamed identity, a checklist with a multiple input, and a signal. [Program.cs](Program.cs) builds a document with the typed builders, rewrites one node to its version 1 form under its old identity, and checks that reloading migrates it.

In the solution the analyzer comes from a project reference. To check the packed analyzer as an external user assembly would see it, run from the repository root:

```sh
VERSION=0.0.0-local.$(date +%s)
dotnet pack Graph.Generator/Graph.Generator.csproj -c Release -p:Version=$VERSION -o artifacts/packages
dotnet run --project Samples/Graph.Samples.CustomNodes -c Release -f net10.0 -p:UseAnalyzerPackage=true -p:AnalyzerPackageVersion=$VERSION
```

A unique version keeps NuGet from reusing a cached package. Invalid declarations fail the user build with MG5001–MG5007; see the [generator README](../../Graph.Generator/README.md).
