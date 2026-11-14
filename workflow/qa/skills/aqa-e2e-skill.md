# AQA E2E

Use for agreed browser end-to-end verification of user flows and acceptance behavior. Run `e2e.cmd` only when requested or in the agreed QA scope. Performance scenarios use the separate performance-testing skill.

## Scope

Before running, identify the target use cases, environment/base URL, and likely regression areas. Use the project's C# Playwright tests and real application path where possible. Prefer headed execution for visual, redirect, timing, or interaction checks. Follow `tst/VeronikaStudio.UiTests/AGENTS.md` for locator conventions.

Cover the new primary path, one relevant unhappy path when business rules require it, and nearby flows that share changed code or UI. Validate user-visible outcomes against the agreed test cases.

## Classify failures before retrying

- **Environment/connectivity:** the base URL is unreachable, startup errors persist, or the app cannot load. Stop broad execution and capture the URL, status/browser error, and timestamp.
- **Product behavior:** the app is reachable but an acceptance rule or visible outcome is wrong. Capture the failing step and evidence, then check relevant adjacent flows.
- **Test issue:** selector, wait, or test-data assumptions are stale while product behavior is correct. Fix the test and rerun the affected scope.
- **Unknown:** preserve enough evidence to distinguish the cause before retrying.

Do not spend a full-suite run repeating an obvious shared environment failure. Do not treat an unrun required E2E check as a pass.

## Procedure and report

1. Read the target test cases and confirm the environment is reachable.
2. Run the agreed primary flow and targeted regression coverage using the repo-owned command.
3. Classify failures before deciding whether to retry; rerun only affected scope when practical.
4. Report cases covered, regressions checked, failures and their classification, acceptance status, and any skipped required check.
