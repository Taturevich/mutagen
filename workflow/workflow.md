# Repository Workflow

This guide records ownership and documentation expectations. Root `AGENTS.md` contains the always-relevant repository commands and safety constraints; task-specific procedures live in the matching skill. Read only the sections and skills relevant to the task.

## Ownership

- Product owns scope, user needs, acceptance criteria, and business decisions. Start here when those are unclear or the request requires a product decision.
- Dev owns implementation, debugging, refactoring, architecture, deployment, and code review.
- QA owns verification, regression analysis, test automation, and release evidence when QA is in the agreed scope.
- Small tasks may combine roles. Do not add handoffs or artifacts that do not help complete the requested work.

## Change workflow

- Make the outcome concrete enough to implement and verify. Keep changes local and assess security, architecture, deployment, rollback, and localization impact when relevant.
- Use the matching skill for specialized procedures. Use an independent reviewer for major changes and sensitive boundaries; the code-review skill defines the handoff.
- For production behavior changes, include deployment and rollback notes where needed.

## Records

- Keep stable requirements, test cases, and reusable guidance with their owning role under `workflow/product`, `workflow/qa`, or `workflow/dev`.
- Record significant outcomes once in `workflow/CHANGELOG.md`; link to supporting records instead of copying their contents.
- Do not create a worklog or change record for routine edits. Include a commit reference when a record describes a committed change.

## Verification and completion

- Run verification required by the request and agreed acceptance scope through the repo-owned commands in root `AGENTS.md`.
- Report what ran and what did not. Do not treat missing required verification as a pass.
- A change is complete when its outcome is clear, implementation and required tooling are complete, agreed verification is recorded, and material follow-ups are explicit.
