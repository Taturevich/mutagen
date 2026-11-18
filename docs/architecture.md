# Mutagen architecture

Mutagen has three production projects. The CLI assembly also exposes the SDK in
`MutationFramework.Sdk`; this preserves the existing reference and public API.
The Roslyn-only mutator assembly and the VSTest coverage collector have no project
dependency on the engine. No new framework, plugin container, or service locator
is involved in a run.

## Components and direction

| Area in `src/MutationFramework.Cli` | Responsibility |
| --- | --- |
| `Sdk` | Public options, immutable report contracts, SDK entrypoints, option validation |
| `Application` | Run/plan orchestration and the distinct standalone-discovery lifecycle |
| `Compilation` | Evaluated project graphs, framework selection, build inputs, isolated compilation and bounded candidate recovery |
| `Mutations` | Semantic candidate discovery, stable identities, schema rewriting, generated runtime source |
| `Coverage` | Baseline acquisition, probe validation, per-test runtime-to-stable-site mapping |
| `Planning` | Coverage conflicts, work items, isolation eligibility, resource groups, bounded worker scheduling |
| `Execution` | Worker ownership, policy-specific execution, scoped resource locks, typed outcome classification |
| `Execution/TestPlatform` | VSTest sessions, native cases/results, run settings, owned testhost processes, SDK selection |
| `Caching` | Content-addressed storage and phase-specific identity, integrity and reuse policies |
| `Reporting` | Completed-result aggregation and independent JSON/HTML serialization |
| `Infrastructure` | Child-process invocation, safe path containment and failure types |

`Application/Engine.cs` coordinates the run. Operators do not reference this
assembly. Reporting receives completed data and does not invoke compilation,
planning or test sessions. The scheduler produces work units and concurrency;
the executor publishes results through bounded workers; `TestExecutor` invokes
the selected testhost policy. Architecture tests enforce these boundaries in
addition to the project/assembly dependency checks.

## Discovery and compilation

1. Validate options and resolve production and test project graphs through MSBuild.
2. Retain each source's assembly identity, target framework, language version and
   preprocessor symbols in `MutationCompilationContext`.
3. `MutationDiscovery` asks the explicit mutator registry for descriptions and
   applies semantic/schema safety checks. Generated files participate in binding
   but are excluded from mutation.
4. Supported and unsupported candidates share the same identity construction:
   assembly, framework, relative source path, span start and replacement token.
   Site identity uses span length instead of the token. Runtime indices are a
   separate, deterministic projection of sorted stable IDs.
5. `MutationWorkspace` copies the evaluated project tree, materializes external
   linked source, stages configuration and delegates rewriting to
   `MutationSchemaBuilder`. Original source is never rewritten.
6. `MutationCompiler` restores/builds the clone. An attributable compiler error
   removes the implicated candidates, retains their `CompileError` results and
   rebuilds within the existing two-recovery-pass limit. Remaining candidates
   receive runtime indices through the same routine used during discovery.

The schema rewriter deliberately retains specialized handling for statements,
short-circuit expressions, checked contexts, expression trees, ref-like results
and extension replacements. The generated runtime is a separate source template;
its low-allocation hit recording and activation protocol remain unchanged.

Standalone `discover` refreshes original test outputs under compilation locks and
discovers identities without creating an instrumented workspace or running tests.
It does not restore private cached outputs over shared build directories.

## Coverage

`CoverageCollector` owns baseline controller sessions and joins all baseline
workers before returning `BaselineCoverage`. Supported serialized NUnit runs use
the independent in-process collector for exact per-test attribution. Other runs
use test-specific probe files. Missing, unreadable and corrupt artifacts remain
distinct failures; they never mean empty coverage.

`CoverageMap` is constructed after compilation recovery has settled the candidate
set. It owns one runtime-to-stable-site index and the canonical mapping identity
for the whole baseline. Probe JSON documents are disposed as they are parsed.
Only the deterministic projection is cached: every baseline test and its probe
validation still run. The runtime/collector reflection names are intentionally a
small cross-assembly protocol so instrumented assemblies need no engine reference.

## Planning, execution and ownership

`MutationPlanning` turns baseline associations into work items and preclassified
results. `MutationPlanner` builds deterministic coverage-conflict batches.
`Auto` intentionally retains isolated scheduling: coverage independence alone
does not establish safe shared-process execution.

`ResourceAwareScheduler` removes cached work, partitions resource-group batches
and bounds workers. `MutationExecutor` owns the result slots and the atomic worker
cursor. Each work unit has a single publisher. Compile-rejected results retain
slots although they are absent from the executable candidate list.

Each `MutationWorker` owns one concrete VSTest controller. It disposes that
controller before replacement and accounts for initialization/restarts in one
place. A controller is not concurrently used by unrelated execution units.
Cancellation joins workers before the resource-lock registry is disposed.

| Isolation | Testhost lifetime and resource scope |
| --- | --- |
| `PerTest` | Fresh host for each selected test; its declared locks span that invocation |
| `PerMutant` | Fresh host for one mutant's covered tests; all locks span the grouped run; attributable failure may bail remaining tests |
| `PerBatch` | One serialized host for a coverage-disjoint batch; explicit caller equivalence attestation and complete coverage required |
| `PerResourceGroup` | Serialized hosts for lock-connected groups; cross-group mutants remain in one locked work unit; explicit equivalence attestation required |

Executors unable to enforce lock-bearing grouped serialization retain the
existing per-test fallback. `ResourceLockManager` acquires unique keys in ordinal
order, releases in reverse order in `finally`, and honors cancellation while
waiting. Tests, fixtures and external services remain responsible for their own
state reset.

`ProcessTestHostLauncher` tracks only processes launched for its execution. It
requests normal cancellation, then force-terminates those process trees after the
two-second grace period. Advisory VSTest cancellation is deliberately best effort;
the independent termination path remains responsible for enforcing the timeout.

`TestFailureKind` carries internal failure meaning separately from diagnostic
text. `MutationOutcomeClassifier` shares aggregation while preserving the policy
difference: an uncertain responsible test makes a complete batch inconclusive,
whereas a deliberately stopped mutant can already be killed by an attributable
failure. Existing serialized statuses and diagnostic messages are preserved.

## Cache validity and reports

`ArtifactCache` owns atomic writes, cross-process locks, indexes and hashed blobs.
`CompilationArtifactCache`, discovery, coverage and `MutationResultCache` own
their respective validity rules. Cache reads retain schema/identity, safe-path,
manifest and blob-integrity checks. Restore stages a complete private output tree
before publishing it. Both destination and staging containment checks are
intentional: an absolute path safe in one root can escape the other root.

Metadata storage now uses an explicit version-1 envelope containing the content
key, payload and SHA-256 payload checksum. This closes a verified gap where valid
JSON could be changed without detection in metadata phases lacking their own
checksum. Legacy unversioned metadata is retained on disk but safely recomputed
on access; it cannot be promoted without verification. Unknown envelope versions
also trigger recomputation. Phase payloads, logical indexes, content keys and
content-addressed blobs keep their existing formats. Payload checksums protect
the stored representation; they do not replace canonical input-key construction.

Identity recipes retain source/build/dependency/environment inputs and existing
engine/mutator assembly MVIDs. Rebuilding an implementation conservatively changes
those implementation identities under the existing policy; no class names or new
source-file paths were added as cache inputs. Removing that safeguard would weaken
implementation-change invalidation, including external mutator registrations.

Validated mutation-result reuse remains opt-in, requires the caller's external
state scope key, complete coverage, isolated per-test execution and no resource
locks, and accepts only validated killed/survived results. Baseline execution is
never bypassed. `ValidatedMutationResults` owns eligible keys/restored outcomes;
the executor does not construct fingerprints or interpret raw manifests.

`MutationResultAggregator` constructs one deterministic report model with counts,
profiles, timing and execution counters. `MutationReportWriter` serializes that
completed model as schema-4 JSON and HTML, retaining HTML encoding. Only selected
formats are staged in the destination directory and each selected file is published
by an atomic overwrite rename. Cancellation/serialization failures preserve previous
reports; unselected files are untouched. Publication is atomic per file, not a
transaction across JSON and HTML. Each report field/HTML row is serialized once.
Reporting timing ends after HTML row preparation and serialization of the main JSON
properties, before final timing metadata, HTML header/file I/O, flushing and renames.
The JSON and HTML metadata and returned model use the same finalized values.

## Extending and verifying

To add a mutator, implement `IMutator<TSyntax>` in the mutator project, describe
applicability/semantics and replacement metadata, register it explicitly in
`MutatorRegistry.CreateDefault`, add focused conformance tests and update the
pinned parity metadata when applicable. SDK extensions can supply a registry.
Do not change compilation, coverage, scheduling or reporting for an ordinary
expression/statement replacement. New specialized schema semantics require their
own compilation/activation regression before widening supported contexts.

VSTest is the supported adapter. Its native cases are deliberately confined to
the test-platform/execution integration. To add another backend, implement its
discovery, exact-case execution, typed failure and cancellation behavior beside
that adapter. Introduce a common session contract when that second implementation
exists, keep native handles in the adapter, and select sessions at the existing
discovery, coverage and worker creation sites. Mutation operators and conflict
planning must remain independent of the backend.

Engine tests are organized by behavior, share immutable Roslyn references and
read-only fixture paths, and use unique writable directories. `Unit`, `Component`
and `EndToEnd` categories permit focused feedback; conformance suites retain their
matrix-consumption gate. Performance benchmarks remain explicit opt-ins. See
the README for commands and `docs/benchmarks.md` for historical measurements.

## Correctness and scalability remediation (2026-11-17)

Semantic discovery uses each project's actual successful C# compiler invocation,
obtained from MSBuild's `CscCommandLineArgs`, and compiler-emitted generator output.
References retain aliases and embed-interop properties; Roslyn's command-line parser
supplies parse/compilation options, nullable settings, symbols and output identity.
Production discovery never uses runtime trusted-platform references. The synchronous
in-memory fixture API retains a caller-supplied/test compilation fallback.

Cold contexts restore graph roots and rebuild each project once in dependency order,
without recursively rebuilding its dependencies. Build-only analyzer/generator project
references participate in build/configuration/cache inputs but are not mutation roots.
The provider retains immutable source/reference snapshots under the same graph locks
used by discovery builds. Binding is lazy and shared within that context, so a validated
candidate-cache hit does not repeat binding. The reconstructed compilation must have
no binding errors against its successful actual build. Unsupported/custom compiler
invocations fail explicitly rather than falling back to incomplete runtime references.
Custom builds with untracked generated inputs continue to bypass affected cache phases.
Arbitrary generator reads of external, undeclared inputs are outside the cache contract.

Every mutation is bound to its owning project and framework. Each graph project retains
its evaluated target framework, selected from its immediate parent; additional production
roots retain their own dependency selections. A project required at two different target
frameworks in one run is rejected explicitly: select a compatible common framework or run
the targets separately. Linked files shared by
production projects get separate schema copies and parse options under each owner's
`obj/MutagenOwned`, with compiled-path aliases retained for diagnostic recovery.
Stable IDs retain assembly/framework/file/span/replacement identity; previously
conflated linked owners now receive their proper assembly identities.

Statement replacement and built-in removal use one dispatch per syntax node, with
the recursively rewritten original only in the final branch. Local declarations,
using declarations, local functions, labels, and escaping out/pattern declarations
are explicitly unsupported extension dispatch sites because wrapping changes scope
or lifetime. Runtime initialization rejects simultaneous activations at one site.
Span/application indexes and return-expression probe indexes are built once per tree.

Mutation failures require an exact selected structured VSTest result and execution
of that case's activated site, after a passing baseline. Mutation runs collect per-test
site hits in serialized NUnit execution. Helpers, async exceptions and absent stack
traces are supported; diagnostic text does not establish lifecycle or attribution.
VSTest/NUnit expose no portable reliable setup/body/teardown phase, so fixture failures
use the same site evidence rule and insufficient evidence remains inconclusive.
Run completion, adapter errors, crashes, timeouts and external cancellation remain
separate evidence. Only intentional cancellation after a confirmed kill permits bailed
remaining cases; bailed/canceled cases alone cannot imply survival. Structured ActiveTests
and completed results retain execution evidence; started cases canceled after a kill are
separate from unstarted selected cases. Mutation execution counters count observed executions,
including started cases interrupted before a result, rather than every selected case.

Planning stores actual test/batch memberships and first-free bounds, never unique
conflicting pairs. Exact edge counting uses sparse inverted 64-bit blocks and one
reused neighborhood buffer, grouping identical coverage sets. Public `ConflictEdges`
remains Int32; when the exact count exceeds it, the legacy field is capped and the
additive `ExactConflictEdges` Int64 plus `ConflictEdgesOverflow` report that transition
explicitly. HTML displays the exact count. No estimate is presented as an exact value.

Deployment requires rebuilding the existing CLI/SDK; no services, migrations or new
runtime dependencies are required. Engine/mutator MVIDs and affected artifact-version
keys invalidate changed schemas, classification and semantic inputs. Roll back by
rebuilding the previous revision; caches remain content-addressed and independently
versioned. Reports remain schema 4 with additive statistics/timing fields.
