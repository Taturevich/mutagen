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

The CLI also accepts `discover`, `plan`, and `report`. `discover` evaluates mutation candidates and uses structured VSTest discovery without executing tests. `plan` builds the instrumented project and collects baseline coverage, but does not execute mutants. `run` executes the plan, and `report` summarizes an existing JSON report without rerunning tests. The engine uses the supported VSTest TranslationLayer API, exact discovered test case IDs, and a persistent controller per bounded worker; each case still runs in a fresh testhost for process isolation. `--execution-mode isolated|batched|auto` chooses singleton work units, coverage-conflict batches, or the safest available plan; SDK callers set `MutationOptions.ExecutionMode`. Options also include `--project`, `--tests` (one or more projects), `--workers`, `--output`, `--report`, `--timeout`, `--filter`, `--include`, and `--exclude`. CLI exit codes are 0 completed, 2 invalid configuration, 3 baseline failure, 4 infrastructure/engine error, and 5 canceled/incomplete.

The output directory contains `mutation-report.json` and `index.html`. Reports include per-test coverage status and covered site IDs, stable mutation IDs, test identities, the executable plan and responsible-mutant mapping, phase timings, process/execution counters, diagnostics, and per-mutant outcomes. The regression sample produces 18 candidates: 10 killed, 7 survived, 1 without coverage. The score excludes the uncovered mutant. Missing and corrupt probe artifacts fail coverage collection instead of being interpreted as empty coverage.

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

- The current path targets `net11.0`, supports one production project and multiple test projects, and uses NUnit through VSTest. Project compile inputs are evaluated with MSBuild; the full transitive project graph and unusual compiler configurations are not all reconstructed.
- Supported mutations include arithmetic, comparison, equality, logical operators, and boolean literals. Roslyn semantic checks conservatively reject unsupported cases such as user-defined/string operators, dynamic/error types, expression trees, constant contexts, explicit checked/unchecked contexts, awaited expressions, pointer/ref-like and generic capture scenarios. Whole-return expressions use allocation-free statement-entry probes; nested expression probes retain a lambda to record entry before evaluation while preserving short-circuiting and single evaluation.
- Discovery returns structured VSTest identities including exact parameterized case IDs. Baseline coverage reuses one VSTest controller and launches a fresh host for each exact test case. Mutation execution uses bounded persistent controllers, but launches a fresh testhost per case with one process-scoped mutation active. Background work escaping a case can make attribution unreliable and requires conservative handling.
- `workers` bounds mutation workers. The instrumented production project is compiled once per run. Batching groups mutants with disjoint test sets, maps every selected test to its single responsible mutant, and executes each assigned test in a fresh host with one process-scoped mutation. This reduces scheduling units but does not combine testhost runs; no mutation context is shared across tests.
- A candidate implicated by an attributable compiler diagnostic is marked `CompileError`, removed, and the remaining schema is rebuilt within a fixed retry bound. Unattributable or repeated project build failures stop the run. Engine integration tests cover structured identities, semantic exclusions, unique runtime IDs, probe artifact statuses, coverage, planning, failure attribution, statement-entry probes, and isolated/batched/auto outcome equivalence.
- The 27-run mode matrix and scale comparison are in `benchmarks/execution-mode-matrix.json`; the same 13-mutant before/after comparison is in `benchmarks/original-fixture-current-engine.json`. Peak memory was not captured. Batches reduce work units but do not reduce testhost starts; measurements show no repeatable wall-time gain, so `auto` selects isolated scheduling.
