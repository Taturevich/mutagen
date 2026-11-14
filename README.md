# MutationFramework

A .NET 11 mutation-testing MVP using Roslyn-generated mutation schemata and NUnit through the supported `dotnet test`/VSTest interface. The CLI and SDK call the same engine. Source projects are copied into a temporary session before rewriting; source files in the checkout are not modified.

## Run the sample

```powershell
dotnet restore MutationFramework.slnx
dotnet build MutationFramework.slnx --no-restore
dotnet test samples/SampleApplication.Tests/SampleApplication.Tests.csproj --no-build --no-restore
dotnet run --project src/MutationFramework.Cli -- run --project samples/SampleApplication/SampleApplication.csproj --tests samples/SampleApplication.Tests/SampleApplication.Tests.csproj --workers 4 --output mutation-results --report json,html
dotnet run --project samples/SdkSample
```

The CLI also accepts `discover`, `plan`, and `report` subcommands. `discover` currently validates/prints the project path; `plan` executes the engine and prints a plan summary. `report` pretty-prints an existing JSON report. Options include `--project`, `--tests`, `--workers`, `--output`, `--report`, `--timeout`, `--filter`, `--include`, and `--exclude`. CLI exit codes are 0 completed, 2 invalid configuration, 3 baseline failure, 4 infrastructure/engine error, and 5 canceled/incomplete.

The output directory contains `mutation-report.json` and `index.html`. The JSON report includes per-test covered site IDs and per-mutant outcomes. The sample currently produces 13 generated candidates: 9 killed, 3 survived, 1 without coverage. The score excludes the uncovered mutant.

## Compare with Stryker.NET

The checked-in sample can be run through Stryker.NET 5.1.0 for a local comparison. Install the tool from the repository root:

```powershell
dotnet tool install dotnet-stryker --tool-path .tools/stryker --version 5.1.0
```

Then run it from `samples/SampleApplication.Tests`:

```powershell
..\..\.tools\stryker\dotnet-stryker.exe --reporter Json --reporter ClearText --concurrency 4 --output ..\..\artifacts\benchmark\stryker --skip-version-check
```

The framework's sample invocation is shown above. For repeated timing comparisons, use the same machine and concurrency, give each run a separate output directory, and compare multiple runs. Candidate sets and supported operators differ between tools, so mutation scores are not a direct measure of equivalent coverage. The .NET 11 sample comparison is recorded in `workflow/CHANGELOG.md`.

## Current supported path and limitations

- The execution path is a single target framework project using `net11.0`, one production project, and one NUnit/VSTest test project. Project compile inputs are evaluated with MSBuild. The implementation does not yet reconstruct every evaluated compiler property or the full referenced project graph.
- Supported working transformations are arithmetic `+ -> -`, `- -> +`, `* -> /`; comparisons `> -> >=`, `> -> <`, `>= -> >`, `< -> <=`; equality `== -> !=`, `!= -> ==`; logical `&& -> ||`, `|| -> &&`; and boolean literals. Semantic validation for overloaded/string operators and expression trees is not implemented yet.
- Probe instrumentation wraps supported binary expressions and boolean literals in an identity call. Each selected test method is run in a separate test process for coverage and each mutant is activated in a separate process. Parameterized NUnit cases are widened to their containing test method because the adapter does not provide a reliable exact filter for each displayed parameter case; this widening is recorded in report diagnostics.
- `workers` bounds concurrent mutation processes. Each execution has unique TRX/probe paths. Mutation switching compiles the instrumented production project once per run.
- The framework does not yet include automated unit tests for its own planning/reporting internals, disjoint-coverage batching, retry policy, retry-safe external fixtures, Mutation Testing Elements schema conformance, or a multi-worker benchmark matrix. The recorded Stryker comparison uses one small sample and does not establish a general performance ranking.
