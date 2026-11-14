# Visual Baseline Updates

Use this procedure only when a UI change intentionally requires Playwright baseline updates. The workflow dispatches a GitHub Actions run and replaces local baseline images.

## Preconditions

- Finish the product code changes and headed baseline updates first. Inspect headed output locally and verify the UI is stable before dispatch.
- The Ubuntu visual-baselines workflow must exist on `main`.
- Start from updated `main`; create a branch named `assets/<bugfix-or-feature-name>` using lowercase letters, digits, and hyphens.

## Procedure

1. Commit all code and headed baseline changes on the assets branch, then push that exact branch.
2. Run `visual-baselines.cmd`. It rejects `main`, dirty worktrees, invalid branch names, missing remote branches, and remote commits that differ from local `HEAD`.
3. Wait for the workflow to succeed, download the one-day artifact, and replace local `headless` baseline PNGs.
4. Inspect the downloaded snapshots. Commit them on the assets branch.
5. Return to `main` and port the complete assets-branch history with a fast-forward merge when possible. Do not hand-copy individual commits or omit part of the branch history.
6. Push `main` only when the task explicitly authorizes it.

Authentication uses `GH_TOKEN` or `GITHUB_TOKEN`, then an authenticated GitHub CLI token, then the existing `github.com` credential from Git Credential Manager. Keep tokens in memory; never print them or write them to repository files.

## Report

Report the workflow result, updated snapshots, verification performed, and final branch and commit state.
