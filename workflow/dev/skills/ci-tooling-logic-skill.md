# CI Tooling Logic Skill

Use this skill when changing GitHub Actions workflows, deploy routing, release automation, path filters, retry/deferral behavior, or any CI/CD decision logic.

## Primary Rule

Keep workflow YAML thin. Put nontrivial decisions in repo-owned tooling, preferably `scripts/tooling/crates/veronika-tool`.

## What Belongs In YAML

- Event declarations.
- Job dependency wiring.
- Secret and environment variable mapping.
- Calling repo-owned commands.
- Passing command output through `GITHUB_OUTPUT`.

## What Does Not Belong In YAML

- Large shell functions.
- Path-filter reimplementations.
- Retry/deferral state machines.
- GitHub API parsing beyond installing tools or passing through command output.
- Business or deployment policy decisions.

## Rust Tooling Expectations

- Add provider-specific CI commands under a focused namespace such as `veronika-tool github-actions ...`.
- Keep pure policy logic testable without GitHub or network access.
- Isolate GitHub-specific calls behind a small adapter type or module.
- Keep normal build/test/publish/deploy commands protected by source-fingerprint validation.
- Allow fingerprint bypass only for narrow CI-routing commands that must run specifically to detect stale released tooling.
- Emit simple `key=value` lines when workflow YAML needs step outputs.

## GitHub Dependency Boundary

Use GitHub Actions and `gh` as transport, not as the owner of deployment policy.

Preferred shape:

```text
workflow YAML
  -> installs released tooling
  -> calls veronika-tool github-actions deploy-gate
     -> pure path/policy matcher
     -> small GitHub adapter for commit file lookup
  -> gates jobs from emitted outputs
```

Avoid spreading GitHub API queries, `jq`, path matching, and retry decisions across multiple YAML steps.

## Deploy Deferral Rule

When deploy uses released tooling:

- A push deploy may defer if the released tool is stale for the commit.
- A tooling-release completion may resume deploy when the range from the last successful application deployment to the released commit contains deploy-relevant files, including changes from earlier deferred pushes.
- Tooling-only commits may publish tooling, but must not build/package/deploy business application code.
- The deploy-relevant path rules must live in one tested Rust function, not in both YAML and shell.

## Verification

For local verification, use repository entrypoints such as `build.cmd` and `test.cmd`; do not invoke the underlying PowerShell, shell, Cargo, or `dotnet` commands directly. Review the workflow diff for inline policy logic and record deployment behavior changes in `workflow/CHANGELOG.md`.

When analyzing an impact report, trace the actual workflow event and base/head range, including application changes skipped or deferred by earlier runs. Compare changed paths and per-suite planned/fallback modes with executed test-result files. Assess whether the work was correctly isolated separately from whether the full run provided safe validation; do not justify a broad fallback as a good isolation result.
