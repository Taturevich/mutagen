# .NET Backend Guidance

Use for ASP.NET Core, C#, dependency injection, configuration, persistence, architecture, and deployment changes. Follow root `AGENTS.md` for repository commands and SQLite constraints.

## Design

- Keep dependencies explicit and minimal; use constructor injection and strongly typed options for related configuration.
- Keep domain rules, persistence, and HTTP delivery responsibilities in their owning layers.
- Keep I/O async end to end. Avoid blocking calls and unnecessary `Task.Run`.
- Split a service or method when a change adds unrelated responsibilities or makes a business path hard to trace. Do not refactor solely to reduce file or method length.
- Prefer local architectural improvements; avoid unrelated refactors.

## Review checklist

- Are dependencies and configuration owned by the right layer?
- Are side effects, transaction boundaries, and async flow clear?
- Does the change preserve the repository's database concurrency constraints?
- Does it update required tooling, deployment, and rollback paths?
- Can the behavior be verified through the documented repository commands?

## Tests

Assert the behavior contract with value-based checks. For authorization or persistence changes, cover the visible result and resulting state, not just that a request returned.
