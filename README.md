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

The CLI also accepts `discover`, `plan`, and `report`. `discover` evaluates mutation candidates and uses structured VSTest discovery without executing tests. `plan` builds the instrumented projects and collects baseline coverage, but does not execute mutants. `run` executes the plan, and `report` summarizes an existing JSON report without rerunning tests. The engine uses the supported VSTest TranslationLayer API and exact discovered test case IDs. A persistent controller is kept per bounded worker. `--test-host-isolation per-test|per-mutant|per-batch|per-resource-group` selects a fresh host per test, one fresh host per mutant with its covered tests grouped in one VSTest run (the default, canceled after the first attributable failure), one shared host for a batch of coverage-disjoint mutants, or serialized batches split across declared independent resource groups. Per-mutant mode follows the normal grouped test-run model; when tests declare resource locks, supported NUnit cases are serialized in the shared mutant host while the full lock set is held. Executors that cannot enforce serialization fall back to per-test hosts. Per-batch mode requires `--execution-mode batched`, complete per-test coverage, and an explicit caller assertion that tests in each batch remain equivalent in one process. Per-resource-group mode also requires batched execution, complete coverage, a lock for every selected test, and an explicit caller assertion that lock-disjoint groups are process-isolation equivalent. Tests connected by overlapping locks stay in one group and run serially in one host; groups run in separate hosts, while mutants covered across groups remain in a single locked work unit. Both batched policies require the NUnit VSTest executor so Mutagen can enforce in-host serialization. Resource locks are held for the full execution unit. Reuse policies leave fixture, static state, database, and external-service reset to the test suite. `--execution-mode isolated|batched|auto` chooses singleton work units, coverage-conflict batches, or the safest available plan; SDK callers set `MutationOptions.ExecutionMode`. `--mutators id[,id]` selects registered mutators; SDK callers set `MutationOptions.Mutators`. `--cache DIR` selects content-addressed caching for project evaluation, mutation discovery, compilation outputs, test discovery, coverage maps, and (opt-in) validated mutation results; SDK callers set `MutationOptions.CacheDirectory`. `--mutation-result-cache-scope KEY` enables conservative result reuse when its external-state attestation remains valid. `--resource-locks testId=>lock[,lock];testId=>lock` serializes tests that declare the same external resource key; use the test IDs returned by `discover`. `--project` selects the primary production project, `--projects` adds production roots, and `--tests` accepts one or more test projects. Other options include `--framework`, `--workers`, `--output`, `--report`, `--timeout`, `--filter`, `--include`, and `--exclude`. CLI exit codes are 0 completed, 2 invalid configuration, 3 baseline failure, 4 infrastructure/engine error, and 5 canceled/incomplete.

The output directory contains `mutation-report.json` and `index.html`. JSON reports use `ReportSchemaVersion` 4 and contain per-test coverage status and covered site IDs, stable mutation IDs, test identities, execution profiles, the executable plan and responsible-mutant mapping, phase timings, process/execution counters, diagnostics, supported-mutant outcomes, and a separate `UnsupportedMutations` list with explicit reasons. The checked-in sample produced 27 executable candidates in the recorded Stryker comparison: 19 killed, 7 survived, 1 without coverage. The multi-project conformance fixture processes 229 candidates across two production and two test projects. Unsupported entries are excluded from the mutation score. Missing and corrupt probe artifacts fail coverage collection instead of being interpreted as empty coverage.

## Architecture and verification

See [the architecture guide](docs/architecture.md) for component ownership, phase data flow, isolation policies, cache validity, and extension instructions. Historical Stryker comparisons and workload measurements are preserved in [benchmark history](docs/benchmarks.md). The pinned operator inventory is [stryker-net-5.0.0-parity.json](benchmarks/stryker-net-5.0.0-parity.json).

## Incremental execution and cache safety

`--cache DIR` stores content-addressed artifacts independently for MSBuild project evaluation, mutation discovery, mutation-enabled compilation outputs, test discovery, per-test coverage maps, and (opt-in) validated mutation results. Cache keys include the evaluated production and test project graphs, configuration and source inputs, restored package assets, relevant test assemblies and runtime dependencies, selected target framework and test filter, environment inputs, and engine, mutator, and instrumentation versions. Cached output manifests are checked for safe paths and content integrity before reuse. Unresolved wildcard imports or missing dependency assets bypass project-related reuse, and cache hits, misses, invalidations, and bypasses appear in diagnostics.

Metadata artifacts use a version-1 integrity envelope. Legacy unversioned metadata is safely recomputed on access; content keys, logical indexes and hashed blobs retain their formats. Reports remain schema version 4. The compatibility policy is documented in the architecture guide.

Validated `Killed` and `Survived` results can be reused only when `--mutation-result-cache-scope KEY` is supplied, execution is isolated per mutant with per-test host isolation, baseline coverage is complete, and no resource locks are declared. The key includes the mutation identity and source/replacement, associated test identities and coverage mapping, project graph, test binaries and dependencies, package assets, environment, engine and instrumentation versions, and the caller's scope key. Baseline tests and coverage still run on every invocation. The scope key is the caller's attestation that external state affecting test behavior has not changed; change it when a database snapshot, service state, secret/configuration input, or other untracked dependency changes. Mutagen does not reset fixtures, databases, or external services. Unsafe or inconclusive cases bypass result reuse and execute the affected mutant. Leave the option unset to always execute every covered mutant.

## Supported path

- .NET 11, Roslyn mutation schemata, NUnit/VSTest, multiple production/test projects and transitive project references.
- MSBuild-evaluated language versions and preprocessor symbols; explicit framework selection for multi-target projects.
- Explicit strongly typed mutator registration, semantic exclusions with diagnostics, and specialized safe coverage instrumentation.
- Exact parameterized test identities, per-test coverage, bounded workers, resource locks and all four documented host isolation policies.
- Content-addressed artifact caching, conservative opt-in result reuse, and schema-4 JSON/HTML reports.
- Linked sources are materialized in the isolated workspace. Unsupported external build imports fail explicitly. The engine does not reset external services, databases, fixtures or static state.

Run correctness checks with:

`powershell
dotnet restore MutationFramework.slnx
dotnet build MutationFramework.slnx --no-restore
dotnet test MutationFramework.slnx --no-build --no-restore
dotnet format MutationFramework.slnx --verify-no-changes --no-restore
`

Use --filter TestCategory=Unit, TestCategory=Component, or TestCategory=EndToEnd on the engine test project for focused checks. Explicit benchmarks remain opt-in; reproduction commands and limitations are in the benchmark history.
