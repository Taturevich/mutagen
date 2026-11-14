# Code Maintainability Skill

Use for changes that risk unclear ownership, unnecessary coupling, duplicated rules, or hard-to-follow control flow. Keep small fixes local; do not turn this checklist into a reason for unrelated refactoring.

## Review

- Is the behavior owned by the layer that can explain and verify it?
- Does the change add a dependency or abstraction that reduces real duplication or isolates a volatile boundary?
- Are branching, state, and side effects still easy to trace?
- Can the changed behavior be verified through the repository's documented commands?
- Does the change create avoidable edits across unrelated areas?

Use evidence from this change, such as repeated rules, new collaborators, mixed responsibilities, or missing verification. Do not use file counts, method length, or complexity metrics as automatic quality targets.

Refactor only when it makes the requested behavior clearer or safer. Record significant architecture decisions in `workflow/CHANGELOG.md`.
