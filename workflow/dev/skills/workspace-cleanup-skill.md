# Workspace Cleanup

Use when the repository has unexpectedly large generated output or the user requests cleanup. Recover disk space without deleting source, configuration, workflow records, secrets, or user-authored changes.

## Safety and procedure

- Measure repository and candidate directory sizes before deleting anything.
- Delete only known reproducible build outputs and caches. Never delete `.git`, source/project/lock files, workflow records, `.github`, deployment files, or user-authored notes.
- Use an explicit candidate list. Resolve every target and confirm it is inside the repository root before removal. Use literal paths; do not use broad recursive wildcards.
- If a directory's ownership or reproducibility is unclear, report it instead of deleting it.
- After cleanup, remeasure and inspect `git status --short` to confirm source and user changes remain intact.

Known candidates, when present and confirmed generated:

- `.dotnet`, `.npm-cache`, `artifacts`, `scripts/tooling/target`
- `src/VeronikaStudio.Client/node_modules` and `dist`
- `src/*/bin` and `src/*/obj`
- `tst/*/bin` and `tst/*/obj`
- `src/VeronikaStudio.Api/artifacts`

Report approximate size before and after, largest removed paths, suspicious paths left in place, and generated dependency/output directories that remain absent. Mention that repository commands may recreate removed outputs.
