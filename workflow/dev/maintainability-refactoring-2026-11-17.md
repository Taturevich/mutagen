# Maintainability refactoring — 2026-11-17

Base: `9324db82434a5e482502dac972254efd35fb903b` (the reviewed
`fcec7a585396da9650ae39845a36e9aba2c7f3d8` was not current).
The assignment covers all three production projects, the public SDK and CLI,
mutator parity, compilation/instrumentation, coverage, execution and isolation,
resource locks, caching, reporting, tests and benchmarks. Existing untracked
benchmark artifacts were preserved. Commit and push were authorized after verification; pre-existing untracked artifacts are excluded.

## A. Architecture changes

The final ownership map and extension procedures are in
[the architecture guide](../../docs/architecture.md). The existing three
production assemblies and `MutationFramework.Sdk` public namespace are retained.
`Application/Engine.cs` coordinates phase results instead of implementing the
phases. Internal components are ordinary concrete C# types; no generic workflow,
DI container, new dependency, migration or runtime requirement was added.

## B. Maintainability improvements

Concrete ownership changes:

- [`Engine.RunAsync`](../../src/MutationFramework.Cli/Application/Engine.cs) now delegates the pipeline.
- [`MutationCompiler.CompileAsync`](../../src/MutationFramework.Cli/Compilation/MutationCompiler.cs) owns recovery; [`CompilationWorkspace.ParseProjectEvaluation`](../../src/MutationFramework.Cli/Compilation/CompilationWorkspace.cs) decodes MSBuild results.
- [`MutationDiscovery.DiscoverCandidates` and `ReindexCandidates`](../../src/MutationFramework.Cli/Mutations/MutationDiscovery.cs) share identity/index construction; [`CacheIdentity.MutatorFingerprint`](../../src/MutationFramework.Cli/Caching/CacheIdentity.cs) centralizes the unchanged selection representation.
- [`CoverageMap`](../../src/MutationFramework.Cli/Coverage/CoverageMap.cs) owns the reusable index; [`ResourceAwareScheduler.Create`](../../src/MutationFramework.Cli/Planning/ResourceAwareScheduler.cs) owns work selection/concurrency.
- [`MutationWorker.Restart`](../../src/MutationFramework.Cli/Execution/MutationWorker.cs) owns controller replacement; [`ResourceLockManager.ExecuteAsync`](../../src/MutationFramework.Cli/Execution/ResourceLockManager.cs) owns ordered scoped locks; [`TestExecutor`](../../src/MutationFramework.Cli/Execution/TestExecutor.cs) keeps host-policy semantics explicit.
- [`MutationOutcomeClassifier`](../../src/MutationFramework.Cli/Execution/MutationOutcomeClassifier.cs) centralizes typed classification; [`ArtifactCache.ReadMetadata`](../../src/MutationFramework.Cli/Caching/ArtifactCache.cs) validates the storage envelope before typed deserialization.
- [`MutationResultAggregator.Create`](../../src/MutationFramework.Cli/Reporting/MutationResultAggregator.cs) builds the completed model; [`MutationReportWriter.WriteAsync`](../../src/MutationFramework.Cli/Reporting/MutationReportWriter.cs) serializes it.

Cache key builders intentionally receive the compilation, coverage and test inputs
whose validity they attest. Those dependencies remain explicit rather than being
hidden in a general-purpose mutable run context.

| Audit concern | Resolution or intentional boundary |
| --- | --- |
| Unrelated responsibilities in Engine | Public contracts, project evaluation, source isolation, mutation discovery/schema/runtime, coverage, planning, execution, caching and reporting have explicit owners |
| Complex run methods | Compiler recovery, baseline acquisition, planning, scheduler decisions and test execution are separate methods/components; MSBuild result decoding is separate from transport/cache orchestration |
| Textual and semantic duplication | One candidate identity recipe, one runtime-index assignment, one cache mutator fingerprint, one safe-path rule, one owned-process termination operation and one worker restart owner |
| Duplicated failure aggregation | Typed platform outcomes and shared mutation aggregation replace diagnostic-prefix interpretation; batch and stopped-mutant precedence intentionally differ |
| Implicit states | Successful phase results carry their artifacts; test outcomes use one typed kind; success/skipping are derived rather than independently mutable flags |
| Mutable/shared state | Compiler owns candidate recovery; coverage owns baseline builders; executor owns result slots; each work unit has one publisher; worker counters/cursor use interlocked operations |
| Lifecycle | Worker owns controller replacement/disposal; launcher owns only its testhosts; resource registry is disposed after workers join; probe documents are disposed individually |
| Redundant abstractions/interfaces | No new interfaces or factory hierarchy; public typed mutator contracts and the method-name mutator shared by three families remain useful and compatible |
| Repeated work/allocations | Coverage index/mapping identity is built once after recovery; configuration imports are inspected once per discovery input; tests share immutable Roslyn metadata references |
| Hidden coupling | Executor no longer fingerprints compiler inputs or reads raw cache manifests; coverage map has no test-platform dependency; source containment is infrastructure rather than a workspace dependency |
| Magic configuration | Existing timeout, two-second termination grace and bounded recovery semantics are retained and documented; no new configuration surface was introduced |
| Naming | Files and source folders follow ownership; typed compilation/execution contexts replace loose argument bundles; result-slot indexes include rejected as well as executable mutants |
| Dead/compatibility code | Unused console-text test parser and unconsumed timeout/test-body flags are removed; the old engine/fixture/operator implementations are removed after migration |
| Experimental code | No obsolete production experiment was confirmed; explicit performance workloads and historical artifacts retain their documented purpose |
| Test duplication | One assembly-dependency test is subsumed by the stronger architecture suite; all other original test methods/assertions are retained |
| Expensive test infrastructure | Eleven behavior suites replace the monolith; reusable immutable references and repository-root helpers remove repeated setup; categories distinguish feedback levels; expensive benchmarks remain explicit |

The planner, resource-group union-find, atomic artifact storage and independent
coverage collector were already cohesive and are retained. Specialized Roslyn
rewrites intentionally remain specialized to preserve C# semantics. Repeated
manifest checks against destination and staging roots are intentional safety
checks, not interchangeable duplicate calls. Concrete VSTest integration remains
the sole supported adapter; no speculative abstraction was introduced.

### Intentional correctness fixes

1. `TestPlatformPaths.SelectSdk` compares numeric SDK versions. Ordinal descending
   path order could select installed SDK 7 ahead of SDK 11. Exact project pins
   remain exact; a stable release is preferred to its prerelease counterpart.
2. Recovered compile errors retain result slots after their candidates are removed
   from executable discovery. Previously both planning and execution indexed those
   results through the remaining-candidate dictionary and could throw after a
   successful recovery. Focused tests cover both empty executable work and a real
   invalid extension replacement alongside a surviving valid candidate, including
   preserved discovery IDs and execution of the retained mutation.
3. Metadata cache reads previously trusted any deserializable raw payload in phases
   without a separate checksum. A version-1 storage envelope verifies its content
   identity and SHA-256 payload checksum before any typed payload is accepted.
   Tests cover valid-JSON tampering, wrong identity, unknown schema, malformed JSON
   and legacy raw metadata, followed by safe replacement under the same key.
   Existing coverage/result-specific integrity checks remain independently tested.

4. The large benchmark now sets/restores `MUTAGEN_LARGE_SQLITE_ROOT`, matching
   the fixture's actual environment contract. The previous `MUTATION_...` spelling
   silently left it using the shared default directory. No production algorithm changed.

No operator transformation, runtime probe template, scheduling algorithm,
isolation policy, report status/schema or phase payload schema was intentionally
changed. The metadata envelope is an explicit safety-driven storage transition:
unversioned legacy metadata is safely recomputed on access, while logical indexes,
content keys and content-addressed blobs remain compatible. Existing implementation
MVID safeguards remain part of cache validity; no class names were added to keys.

## C. Test-suite improvements

- Eleven coherent behavior suites replace the original integration monolith.
- All original assertions remain except one duplicate assembly-dependency test,
  covered by the stronger architecture suite. Shared root/fixture helpers and
  immutable Roslyn metadata references remove repeated setup.
- `Unit`, `Component`, `EndToEnd` and explicit `Performance` categories distinguish
  feedback levels without adding test projects. New cases cover typed outcomes,
  SDK ordering, both compilation-recovery paths and cache metadata transitions.
- Normal solution cases: 318 before (317 passed, one source gate affected by the
  concurrent file migration), 339 after (all passed). Engine cases increased from
  95 to 116: one duplicate removed and 22 regression cases added.

## D. Quantitative changes

| Metric | Before | After |
| --- | --- | --- |
| Engine source | 204,425 bytes | 4,142 bytes; orchestration only |
| Largest framework production source | Engine.cs, 204,425 bytes | MutationSchemaBuilder.cs, 27,733 bytes |
| Largest test source | MutationEngineIntegrationTests.cs, 325,606 bytes | MethodConformanceTests.cs, 61,130 bytes |
| Production projects | 3 | 3 |
| Normal solution tests | 318 | 339 |
| Engine suite elapsed | 18 min 04 s, source-gate caveat above | 17 min 41 s, 116/116 passed |

File sizes describe navigation; the substantive improvements are phase ownership,
explicit data flow, centralized invariants and removal of competing implementations.

Three warm samples per revision follow one excluded warmup. Mutation counts,
classifications, identities, coverage and unsupported results matched in every run.

| Workload | Mutants | Median wall before | Median wall after | Change | Sampled peak before/after |
| --- | ---: | ---: | ---: | ---: | ---: |
| small | 34 | 12.061 s | 11.369 s | -5.7% | 647.7/580.4 MiB |
| medium | 206 | 12.212 s | 12.726 s | +4.2% | 695.1/629.2 MiB |
| large | 2,000 | 48.170 s | 48.675 s | +1.0% | 356.7/277.2 MiB |

No workload exceeded the 5% median wall-time investigation threshold. Memory is
a sampled aggregate process-tree working set, not an exact peak or allocation count.
The large repeat finished before the user-authorized stop could take effect; no
additional large-run benchmark was started afterward. The pinned Stryker 5.0.0
comparison matched all 2,000 large mutations with zero identity, outcome or coverage
differences. Phase timings, counters, testhost/test execution counts, cache reuse,
worker busy/idle time, sample values and microbenchmark allocations are retained in
[the portable evidence artifact](../../benchmarks/results/maintainability-refactoring-2026-11-17.json).

## E. Verification

Two independent read-only reviews found no new regressions. The first also
identified the preexisting compile-recovery defect above. Reviews compared the
extracted flows against the base, checked the generated runtime template for exact
equality and checked retained operator/test bodies. Review does not substitute
for executable checks.

Recorded checks during implementation:

- Initial restore passed; initial build passed with zero warnings/errors.
- Initial engine run: 94 passed, one source-based parity gate failed because its
  original fixture path was moved while the old assembly was still running. It is
  not reported as a clean baseline suite. Sample/benchmark fixture projects passed
  223 tests. The new parity gate scans the grouped suites.
- Focused conformance/parity/architecture/scheduling: 34 passed.
- Focused outcome/coverage/cache-identity/registry checks: 23 passed.
- Recovered compilation, outcome and new architecture checks: 17 passed,
  including real Run/Plan compilation recovery.

The baseline CLI binary was copied before the first successful refactor build.
Before/after measurements run that preserved binary and the candidate against
the same unchanged sample projects, options and machine. One cold warmup is
excluded per revision; three warm samples per revision alternate order. The
comparison hashes mutation IDs/site IDs, source/replacement, classifications,
covering/killing tests, per-test site associations, coverage statuses and unsupported
rows. Child-process working sets are sampled at 100 ms; they are observations,
not exact allocation measurements. NuGet access for isolated restores required
an approved unsandboxed retry. Raw reports/commands are under
`artifacts/refactoring/measurements/`.

Final successful verification:

```powershell
dotnet restore MutationFramework.slnx
dotnet build MutationFramework.slnx --no-restore
dotnet test MutationFramework.slnx --no-build --no-restore --logger "trx;LogFilePrefix=final" --logger "console;verbosity=normal" --results-directory artifacts/refactoring/final-tests-corrected
dotnet format MutationFramework.slnx --verify-no-changes --no-restore
git diff --check
dotnet run --project samples/SdkSample --no-build --no-restore
dotnet src\MutationFramework.Cli\bin\Debug\net11.0\MutationFramework.Cli.dll report artifacts\refactoring\final-measurements\small-candidate-3\mutation-report.json
```

- Restore/build passed; build had zero warnings/errors. All 339 normal tests passed.
- Three explicitly selected coverage microbenchmarks passed, with seven timing/
  allocation samples per internal variant and matching behavior/coverage checksums.
  Pre-existing benchmark artifact files were backed up and restored; new results
  were copied into the evidence artifact and local refactoring artifacts.
- SDK sample completed: 34 mutants, 25 killed, 8 survived, one without coverage.
  Repeated CLI samples completed during the benchmark matrix; the `report` command
  roundtripped the final JSON exactly. Both JSON and HTML were generated.
- Exported SDK/CLI signatures (547 entries) and mutator signatures (96 entries)
  match the preserved baseline. The emitted runtime source matches byte for byte.
- The first final run found one fixture still inspecting the old metadata shape;
  it was corrected without weakening inner path/integrity assertions, passed its
  focused rerun, and the entire corrected solution run then passed cleanly.
- Three independent reviewers found no new regressions; cache-fixture changes were
  re-reviewed. Original sample/benchmark source and collector source are unchanged.
- `dotnet sln MutationFramework.slnx list` passed. Architecture/history/evidence
  documents are included as solution items. No dependencies or projects were added.

## F. Remaining issues

No confirmed in-scope maintainability or correctness issue remains unresolved.
The measurement limitations above are retained explicitly. No new large benchmark
was started after the user's skip instruction; the already-running comparison had
finished before cancellation was processed. Remote CI results are not claimed by
these local verification records.
