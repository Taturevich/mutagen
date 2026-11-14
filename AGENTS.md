# Repository Instructions

Use the relevant part of `workflow/workflow.md` for role ownership and change records. Read task-specific guidance from `.agents/skills/` only when its trigger applies; each router points to the canonical procedure.

## Repository commands

For repository and solution operations, use the repo-owned `.cmd` entrypoints and their documented flags. Do not call underlying `dotnet`, `npm`, Cargo, Playwright, or PowerShell commands as substitutes. Direct calls are allowed when the user requests them or when diagnosing the corresponding entrypoint.

- `restore.cmd` — restore .NET and client dependencies.
- `build.cmd` — build the solution and client, then prepare API static assets.
- `test.cmd` — run unit and integration tests; it does not run UI/E2E tests.
- `e2e.cmd` — run Playwright UI tests. Run only when requested or included in agreed QA scope; never start it implicitly in the background.
- `perf.cmd` and `perf-trace.cmd` — collect runtime counters and allocation traces.
- `publish.cmd` — create deployable API and client output.
- `dev.cmd` — start the supported local development environment.
- `runtime-config.cmd` — prepare local runtime configuration and database setup.
- `sqlite-backup.cmd` — run the supported SQLite backup workflow.

Use `scripts\tool.cmd localization` for localization catalog operations. Keep detailed flags and examples in `README.md` and command help. For GitHub Actions discovery or diagnostics, use `scripts\tool.cmd github` before writing ad hoc polling logic. The wrapper rebuilds a missing or stale local tooling binary automatically. If it still fails its source-fingerprint check, rebuild with `scripts\build-tooling.ps1 -ReleaseOnly` on Windows or `scripts/build-tooling.sh` on Linux, then retry the repo-owned command; do not switch to a separate API polling method because the binary is stale. In CI, wait for the tooling-release workflow and rerun the blocked workflow.

For CI impact analysis, trace the workflow event, selected base/head, and last successfully deployed application commit. Compare that range with the report's changed paths, suite plans/fallbacks, and actual TRX results; identify earlier deferred or skipped application changes that the immediate push range omits. Distinguish safe validation coverage from good test isolation, and do not call a full-suite fallback acceptable merely because it catches an unvalidated change. Give evidence-backed causes and concrete fixes before judging the result.

## Change constraints

- Understand the requested outcome before changing code; keep changes local and add or update tests first for behavior changes when feasible.
- New dependencies, migrations, runtime requirements, or deployment steps need repo-owned tooling in the same change.
- When adding or renaming repo-owned commands, workflows, tooling sources, or worklog artifacts, add them to `veronika_studio.slnx` and verify the solution file.
- Do not claim tests, builds, or publish steps passed unless they ran successfully. Build and publish must be warning-free.
- Significant UI or workflow changes require localization review for `en`, `cs`, `uk`, and `ru`. Edit locale catalogs only through `scripts\tool.cmd localization`.
- Significant implementation decisions belong in a dated `workflow/CHANGELOG.md` entry. Record outcomes, not raw notes.

## SQLite constraints

The app targets SQLite on a Basic 1 vCPU/1 GB droplet. Keep database concurrency small:

- At most two independent reads may run concurrently in a request path. Give each branch its own `SalonDbContext` through `IDbContextFactory<SalonDbContext>` or a factory-backed service; await them together, then continue sequentially.
- Never parallelize work on the same scoped `SalonDbContext`, `UserManager`, or `RoleManager`; do not nest database fan-out.
- Keep writes, transactions, role mutations, booking creation, checkout completion, and database-heavy background jobs sequential unless explicitly approved.
- Do not add `.AsNoTracking()`; tracking is disabled globally.

## Conditional procedures

- For intentional Playwright baseline updates, use the `visual-baselines` skill.
- UI automation locator rules are scoped to `tst/VeronikaStudio.UiTests/AGENTS.md`.
