# Graph build harness

NUKE entry point for the repository's shared build, test, coverage and release targets. Invoke it through `build.sh` from the repository root:

```sh
./build.sh TestReport --configuration Release --verbosity minimal
```

[Build.Project.cs](Build.Project.cs) owns project-specific settings and hooks; the remaining shared build files follow the sibling-library workflow. Tests produce merged coverage in `coverage/coverage.xml` and a report in `coverage/report/`. The [CI workflow](../.github/workflows/build-and-test.yml) also executes the [NativeAOT smoke app](../Graph.AotSmoke/README.md).

See the [repository overview](../README.md) for packages and samples. This project is build infrastructure and is excluded from the solution's ordinary library build.
