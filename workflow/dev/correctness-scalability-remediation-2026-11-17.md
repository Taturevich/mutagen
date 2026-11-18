# Correctness and scalability remediation ? 2026-11-17?18

Baseline: `a8c408d064a817dd93dbd6958bff813a791a4faa`, verified against freshly fetched `origin/main` before implementation. The existing architecture, .NET targets, operators, CLI and SDK entrypoints were retained. No package dependency or service was added.

## A. Fixed findings

| Finding and root cause | Implementation and principal files | Regression evidence |
| --- | --- | --- |
| Statement replacements independently wrapped the same original statement, duplicating execution. | `Mutations/MutationSchemaBuilder.cs` composes removal and replacement candidates into one ordered mutually exclusive dispatch. Nested schemas remain in the final original branch. `MutationRuntimeSource.cs` rejects conflicting site activations using compact per-project constant arrays. `MutationDiscovery.cs` explicitly rejects extension statements whose wrapping changes escaping scope/lifetime. | `InstrumentationTests`: compiled side-effect/async/throw/directive fixtures, original-versus-inactive comparison, actual extension session, and compiled runtime rejection with 2 and 10,000 candidates all passed. |
| Trusted-platform references did not represent the evaluated project. | New `Compilation/ProjectSemanticCompilation.cs` reads successful actual MSBuild Csc inputs and generator output. `CompilationWorkspace.cs`, `CompilationModels.cs`, `MutationDiscovery.cs` and `MutationWorkspace.cs` preserve per-project/framework ownership, references, immutable snapshots, linked-owner copies and diagnostic mappings. `BuildInputs.cs` and `CompilationArtifactCache.cs` fingerprint relevant compiler dependencies. | `CompilationWorkspaceTests` and `CachingTests`: package/project/custom-operator binding, nullable/language/symbol options, generated types, linked owners, actual compilation/execution, target-framework ownership, concurrency and dependency-only invalidation passed. |
| Stack text was used as test lifecycle and kill evidence. | `Execution/TestPlatform/TestResultClassifier.cs`, `TestPlatformSession.cs`, `TestExecutor.cs` and `MutationOutcomeClassifier.cs` use exact selected structured results, passing baseline and activated-site execution evidence. Started, completed, skipped, deliberately unstarted, canceled and missing cases remain distinct; infrastructure interruption cannot become a kill. | `TestPlatformTests`, `OutcomeClassificationTests` and `ExecutionTests`: assertion/application/helper/async/empty-stack/parameterized/setup/teardown failures, crash, missing result, cancellation, timeout and confirmed early termination passed. Relevant real fixtures ran under PerTest, PerMutant and PerBatch. |
| A hash-set entry was allocated for every conflicting mutant pair. | `Planning/MutationPlanner.cs` schedules with actual test/batch memberships and first-free bounds. Sparse inverted bitsets, grouped identical coverage sets and one reused buffer count exact conflicts. The old Int32 field remains, with additive Int64 `ExactConflictEdges` and explicit overflow reporting. | `SchedulingTests`: random pairwise oracle, deterministic safe batches, 10,000 dense mutants, 100,000 dense mutants, 1,000-test coverage and a sparse hub passed. Repeated dense/disjoint/mixed benchmarks passed. |
| Syntax traversal repeatedly scanned all candidates and return statements. | `MutationSchemaBuilder.cs` creates frozen per-tree span/application indexes, per-file candidate lookup and one return-entry probe index. Traversal reuses ordered arrays without candidate-list scans or nested whole-tree return searches. | Real Roslyn rewriting at 100/1,000/10,000 candidates, across one file, 20 files, shared nodes and nested expressions. Three measurements per pattern; normalized output matches the original implementation in every case. |
| A second report pass selected writes by existing files and could overwrite unrequested formats. | `Reporting/MutationReportWriter.cs` validates formats, serializes selected contents once, stages all selected files in their destination directories and publishes each through an atomic overwrite rename. `MutationResultAggregator.cs` retains report/counter compatibility. | `ReportingTests`: selected formats, existing unselected files and timestamps, invalid configuration, cancellation, serialization failure, existing selected-file replacement and temporary-file cleanup passed. |

Paths in the table are relative to `src/MutationFramework.Cli/`; suites are in `tst/MutationFramework.EngineTests/`. Shared fixtures are in `TestWorkspace.cs`. Additional edits across existing C# files apply the requested repository formatting.

Actual E2E verification exposed a redirected-output wait that could outlive its parent process. `Infrastructure/ProcessRunner.cs` now applies the deadline to process exit and both stream drains, disables reusable tool-owned MSBuild workers, and terminates owned process trees on timeout/cancellation. Both inherited-pipe regressions passed. This does not change testhost isolation policies.

## B. Mutation correctness

The custom extension emits `Calls += 10` and `Calls += 100` for the same side-effecting invocation. Direct tests compile both the original and instrumented programs, invoke them and assert exact values.

| Activation | Observed Calls for one invocation |
| --- | ---: |
| None | 1 |
| Replacement A | 10 |
| Replacement B | 100 |
| Unrelated mutant | 1 |

Two-site methods produce 2 when inactive and 11/101 when one site is replaced. Async, nested-block and conditional-directive methods preserve the same single-execution behavior. The inactive throwing statement increments once and throws the same exception type as the original; either replacement suppresses that original branch. Conflicting activations at one site throw explicitly. The real extension-plus-removal session completed with 5/5 mutants killed and its inactive baseline passed. Existing expression, statement, method, pattern, regex and parity conformance suites passed.

## C. Compilation correctness

The provider restores graph roots, builds projects once in dependency order, obtains `CscCommandLineArgs` from the successful compiler task and captures compiler-emitted generated source. Roslyn command-line parsing preserves compilation/parse options, assembly identity, nullable context, conditional symbols, aliases and reference metadata. Source/reference snapshots are taken under existing graph locks; one lazy compilation supplies semantic models to all mutators. Its binding diagnostics must agree with the successful actual project build. Individual mutators do not construct compilations.

Project identity, target framework, project-reference graph, source ownership, actual source trees, generated trees and metadata references remain attached to their owning context. Linked files in two projects receive distinct models and rewritten copies. Immediate-parent framework selection is retained for every graph node and additional root. Build-only generator references participate in compilation/cache inputs without becoming mutation roots. The main semantic E2E produced 7 mutants: 6 killed and 1 uncovered; linked-owner recovery produced one CompileError and one killed mutant.

Persistent artifacts contain reconstructable compiler arguments, generated text and content fingerprints, not Roslyn runtime objects. Semantic artifacts use `semantic-compilation-v3`, dependency locators `semantic-input-dependencies-v1`, project evaluation `project-evaluation-v5`, and candidate discovery `mutation-discovery-v2`; existing engine/mutator identities invalidate affected schema and result artifacts. Project-reference and external-DLL type changes both invalidated semantic and candidate caches.

## D. Performance

Environment: Windows 10 Pro 10.0.19045, SDK 11.0.100, same machine and fixture sources. End-to-end measurements ran separately from correctness tests. Each implementation has three runs per workload. Small/medium first runs use a cold content cache; warm medians use the next two runs. The large benchmark first discovers tests to configure resource locks, so its first measured execution is discovery-prewarmed and schema-cold, not fully cold. Timing is machine-specific; sessions were separate rather than alternated.

| Workload | Mutants | First measured wall time, before ? after (s) | Warm median before ? after (s) | Warm change |
| --- | ---: | ---: | ---: | ---: |
| small | 34 | 32.006 ? 34.749 | 19.195 ? 12.518 | -34.8% |
| medium | 206 | 28.189 ? 34.868 | 15.093 ? 12.727 | -15.7% |
| large | 2000 | 95.870 ? 61.969 | 64.156 ? 39.636 | -38.2% |

The greater-than-5% cold small/medium increases were investigated: actual compiler-context capture and binding cost 6,987/6,721 ms, and project-evaluation/context preparation rose from 5,372/4,978 ms to 10,270/10,400 ms. Cold instrumented build time also rose by 2.6?2.8 s. Warm caching avoids the compiler rebuild and candidate rebinding. All-run and warm wall-time medians improved; there is no unexplained median end-to-end regression. These measurements do not imply that semantic correctness is free on cold runs.

Warm phase medians in ms (before ? after). Semantic analysis overlaps project preparation and mutation generation; it must not be added to those phases. Discovery is test discovery.

| Phase | Small | Medium | SQLite |
| --- | ---: | ---: | ---: |
| Project/context preparation | 346.0 ? 620.5 | 220.0 ? 605.5 | 282.5 ? 631.0 |
| Candidate generation | 103.0 ? 43.0 | 39.5 ? 32.0 | 98.5 ? 74.5 |
| Semantic analysis | n/a ? 371.5 | n/a ? 378.0 | n/a ? 386.0 |
| Test discovery | 4401.0 ? 2082.5 | 2810.0 ? 2177.0 | 3139.0 ? 1898.0 |
| Schema rewriting | 460.5 ? 287.0 | 438.0 ? 273.5 | 4130.5 ? 479.0 |
| Instrumented compilation | 4505.0 ? 2299.0 | 3434.0 ? 2314.5 | 4781.0 ? 2924.0 |
| Baseline execution | 1293.5 ? 967.0 | 1088.0 ? 967.5 | 3367.0 ? 2123.5 |
| Coverage mapping | 55.0 ? 37.5 | 265.0 ? 207.5 | 360.0 ? 252.0 |
| Planning | 30.5 ? 28.5 | 27.5 ? 29.0 | 91.5 ? 36.0 |
| Mutation execution | 6589.0 ? 5327.0 | 5676.0 ? 5361.0 | 43724.0 ? 29692.0 |
| Reporting | 93.5 ? 79.0 | 93.0 ? 64.5 | 145.5 ? 96.0 |

Actual mutation test executions remain 54/609/2,000 per run. Testhost starts remain 13 for small, 10?11 for medium, and 22 for SQLite. The SQLite resource lock deliberately limits effective workers to 1 despite 4 requested workers. Raw reports retain worker busy/idle durations, requested/effective concurrency and all per-run counters.

Warm sampled aggregate working-set medians, before ? after: small 484.8 ? 498.7 MiB; medium 517.7 ? 532.2 MiB; SQLite 251.3 ? 265.3 MiB. Actual compiler snapshots add memory; there is no quadratic planner storage. End-to-end allocated-byte counts were not measured; microbenchmarks measure allocations directly.

Planner: at 1,000 dense mutants the median fell from 1,333.5 to 5.24 ms and allocations from 27.16 MB to 1.51 MB. The optimized 10,000-dense median is 88.25 ms, about 15.14 MB allocated, 10,000 safe batches and exactly 49,995,000 conflicts. Disjoint/mixed 10,000 medians are 109.73/80.23 ms with 20.88/10.93 MB allocated. Small disjoint plans have additional sparse-index overhead (1,000: 2.32 ? 9.79 ms); this is a measured millisecond-scale tradeoff, not a hidden speedup claim. The full-suite 100,000-dense regression passed in 443 ms with 4,999,950,000 exact edges.

The original combined 10,000 stress run was terminated after 3,855.8 seconds and 2.42 GB process peak. Concurrent baseline verification ran then, so it is only a noncompletion/resource bound, not a precise planner timing. The separate original rewriter benchmark completed all scales and patterns with three runs each.

| Rewrite at 10,000 candidates | Before median (ms) | After median (ms) | Before allocations (MB) | After allocations (MB) |
| --- | ---: | ---: | ---: | ---: |
| one-file | 73132.31 | 838.49 | 162.46 | 224.18 |
| many-files | 4380.48 | 861.36 | 168.93 | 222.84 |
| same-node | 36233.85 | 510.13 | 98.72 | 130.46 |
| nested | 64139.42 | 811.68 | 147.45 | 201.06 |

All 12 scale/pattern combinations have equivalent normalized syntax output. Indexes increase allocated bytes by roughly 32?38% at 10,000 while sharply reducing CPU cost; this limitation is measured explicitly. Process peak for the original rewrite benchmark was about 355 MiB and the optimized combined microbenchmark about 348 MiB; peaks are cumulative per process, not isolated per rewrite. Each measured rewrite parses one tree per file (1 or 20); normalization parsing happens after timing/allocation capture.

Raw evidence: `benchmarks/results/remediation-{baseline-bounded,baseline-rewrite,optimized}.json`, `remediation-{baseline,optimized}-workloads.json`, `remediation-{baseline,optimized}-mutagen-large-workers-4-per-batch.json`, `remediation-baseline-stress-limit.json` and consolidated `remediation-comparison.json`. All individual phase timings, counts, digests, samples and medians are retained.

## E. Compatibility

- Public CLI switches, SDK entrypoints, target frameworks, default safe execution mode, compile-once schemata, caching and resource locks remain available. PerTest, PerMutant, PerBatch and PerResourceGroup verification passed.
- Report schema remains 4. Int32 conflict statistics retain their constructor/API; overflow is explicitly signaled alongside the exact additive Int64 count. Requested JSON/HTML formats alone are touched. Publication is atomic per file, not a multi-file transaction.
- Report timing measures HTML-row preparation and main JSON serialization; it excludes final timing metadata, HTML header/file I/O, final flush and publication. JSON, HTML and returned metadata agree without a second main serialization.
- Small/medium/large result digests match the original implementation across equivalent runs. Stable assembly/framework/path/span/replacement identity is preserved for unchanged candidates. Previously conflated linked owners now have the correct owning identity.
- Pinned Stryker.NET 5.0.0 conformance passed. Fresh reports match all 28 comparable non-removal small mutants, all 206 medium mutants and all 2,000 SQLite mutants, with zero missing identities, outcome differences or coverage-association differences. No operator was removed.
- Full SDK template EditorConfig rules were copied with explicit repository overrides. IDE0055 now fails builds; the scratch proof produced six expected formatting errors. `dotnet format` applied repository formatting. `.gitattributes` aligns LF checkout behavior while keeping Windows command scripts CRLF. Generated schema copies suppress only IDE0055 for their mapped source files because original line layout is required; original project and repository builds remain enforced.

## F. Executed verification

Original-revision baseline verification passed all 116 normal engine tests (24 m 40 s) and the 223 sample/benchmark fixture tests. The initial sandboxed whole-solution attempt stalled; its outside-sandbox engine rerun supplies the baseline evidence.

| Command/check | Actual result |
| --- | --- |
| `dotnet restore MutationFramework.slnx` | Passed; final restore up to date. |
| `dotnet build MutationFramework.slnx --no-restore` | Passed, zero warnings/errors; final recorded build 13.64 s. |
| `dotnet test MutationFramework.slnx --no-build --no-restore --logger "trx;LogFileName=final-verification.trx" --logger "console;verbosity=normal" --results-directory artifacts/remediation/final-verification --blame-hang-timeout 5m` | Passed: 156 engine + 18 small + 101 medium + 100 SQLite + 4 isolation = 379 tests. Engine duration 36 m 35 s. Explicit benchmarks were intentionally run separately. |
| `dotnet test MutationFramework.slnx --no-build --no-restore --filter "FullyQualifiedName!~MutationFramework.EngineTests" --logger "trx;LogFilePrefix=final-fixtures" --results-directory artifacts/remediation/final-fixtures` | Passed again after session recovery: 4 + 101 + 18 + 100 = 223, with separate TRX evidence; engine intentionally excluded. |
| Engine test filter `SchedulingTests\|ReportingTests\|OutcomeClassificationTests`, `--no-build --no-restore`, final-focused TRX | Passed 34/34, including the subsequently added thousand-test/sparse-hub regression; no production changes followed the full run. |
| Final-preflight engine filter: timeout, framework ownership/rejection, runtime activation, dependency invalidation and outcome classification | Passed 20/20. |
| Baseline harness filter `MeasurePlanningAndRewriting`, max 1,000, label baseline-bounded | Passed; three runs per planner/rewrite pattern. |
| Same baseline harness filter, max 10,000, operations rewrite, label baseline-rewrite | Passed; 9 m 40 s total. |
| Current engine filter `MeasurePlanningAndRewriting`, label optimized | Passed; full 10,000 scale, 41 s total. |
| Filter `SmallAndMediumWorkloadsAreMeasuredAcrossRepeatedRuns\|LargeSqliteWorkloadIsMeasuredAcrossRepeatedRuns`, label optimized, large isolation per-batch | Passed 2/2 explicit tests, nine real mutation runs, 4 m 47 s. Equivalent original-binary baseline runs also passed (6 m 23 s). |
| Filter `MeasuredWorkloadsMatchPinnedStrykerExecutableMutants`, label optimized | Passed 2/2 against freshly produced reports. SQLite differential also passed inside its benchmark. |
| `dotnet run --project samples/SdkSample` | Passed, exit 0: 34 mutants, 25 killed, 8 survived, 1 uncovered. |
| `dotnet format MutationFramework.slnx --verify-no-changes --no-restore` | Passed, exit 0; no changes required. |
| `git -c safe.directory=F:/mutagen diff --check` | Passed. |
| Solution file-reference validation | Passed; documentation and benchmark artifacts included in the actual `MutationFramework.slnx`. |

The repository-owned .cmd entrypoints named in the supplied generic instructions do not exist in this checkout; the explicitly requested direct dotnet commands were used. Some earlier sandboxed nested builds/tests failed or stalled; successful real-process reruns and their TRX files are recorded above. The first intermediate full run had two fixture failures (baseline marker reacting to newly enabled mutation coverage, and a setup-inclusive test watchdog); both were corrected before the successful full run. The production 8-second busy-test timeout was retained, and its regression now measures active mutation execution/cleanup rather than project setup. No failed or interrupted run is presented as passing.

Independent read-only reviews covered compilation ownership/cache boundaries, schema composition, process cleanup and the final framework/runtime/lifecycle changes. Their actionable findings were fixed; the final scoped review reported no actionable correctness findings.

## G. Remaining limitations

- One project requiring two different target frameworks in the same session is rejected explicitly. Use a compatible explicit framework or separate target runs; it is never silently rebound to the first root.
- A custom build that does not expose a reproducible successful Csc invocation fails explicitly. Arbitrary source-generator reads of undeclared external inputs cannot be fingerprinted; tracked generator assemblies, additional files, analyzer configuration and generated compile inputs are captured. Existing unsafe-input cache bypasses remain.
- VSTest/NUnit provide no portable reliable setup/body/teardown phase. Exact selected result plus activated-site execution evidence is used; insufficient evidence remains Inconclusive. Started-without-result cancellation has focused structured unit coverage; adapter ActiveTests reporting remains adapter-dependent and unknown starts are not fabricated.
- A descendant that already outlived an exited external parent cannot always be safely identified for termination. The runner bounds pipe drains, avoids reusable tool-owned build workers, and cleans up owned live process trees. The regression fixture cleans its deliberately orphaned child.
- Filesystem atomic publication covers each file individually. Hardware durability/fsync and multi-file transactional publication are not claimed.

`dotnet-dump` and `dotnet-stack` were installed only under local `artifacts/remediation-tools/` to inspect the stalled inherited-output wait. They are diagnostic artifacts, not project dependencies or runtime requirements. Existing unrelated artifacts were preserved.
