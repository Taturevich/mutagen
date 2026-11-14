# Change log

## 2026-11-14
- Migrated all four solution projects to `net11.0`, pinned the SDK to 11.0.100, and updated the mutation engine's test assembly lookup for `net11.0`.
- Updated direct NuGet dependencies to current stable releases: Microsoft.CodeAnalysis.CSharp 5.12.0, Microsoft.NET.Test.Sdk 18.12.0, NUnit 5.0.0, and NUnit3TestAdapter 6.3.1.
- Verified restore, a warning-free solution build, and all 8 sample tests on .NET 11. Ran three four-worker mutation samples and three four-session Stryker.NET 5.1.0 runs on the same sample. Both reported 75% mutation score. MutationFramework produced 13 candidates (9 killed, 3 survived, 1 uncovered); Stryker produced 17 (12 killed, 3 survived, 1 ignored, 1 uncovered).
- Median elapsed time was 40.755 s for MutationFramework (range 39.944–41.094 s) and 26.845 s for Stryker (range 26.819–27.209 s), with Stryker 34.1% faster in this small sample. Candidate counts and mutation operators differ, so the equal scores do not imply equivalent coverage or a general performance ranking. Per-run reports are under `artifacts/benchmark/`.

## 2026-11-14
- Started a standalone .NET 10 mutation framework with a Roslyn-based CLI/SDK and an NUnit sample solution.
- Implemented a real Roslyn schema rewrite, process-scoped activation and coverage probes, method-isolated NUnit/VSTest baseline collection, bounded mutant execution, JSON/HTML reports, CLI, SDK surface, and sample projects.
- Verified the sample's actual run at 13 mutants: 9 killed, 3 survived, and 1 uncovered; the original eight NUnit cases pass. Parameterized selection widens to the containing NUnit method.
