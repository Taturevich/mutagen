# Code Review Skill

Use for the independent review required before major pushes, especially for large features, architecture, deployment, authentication, payment, or sensitive data changes.

## Isolation

- Use a new reviewer subagent with `fork_turns="none"`; do not use the implementer or a reviewer who already discussed the change.
- Provide the requested outcome, acceptance criteria, base and proposed revisions (or exact diff), relevant repository constraints, and verification already run.
- Ask the reviewer to inspect the change and surrounding code independently, verify claims against source and tests, make no edits, and report actionable findings.
- After material fixes, request another review from a fresh reviewer.

## Review focus

Check correctness against the requested behavior, security and authorization boundaries, state transitions, data and deployment impact, maintainability, and whether verification covers the changed contract. Treat uncertainty as a finding when it affects safety or correctness. Do not prescribe broad equality assertions when a focused assertion proves the contract.

## Output

For each finding, give severity, location, evidence, impact, and a recommended fix. If there are no findings, state that and list material verification gaps or residual risks. Do not provide a generic approval in place of evidence.
