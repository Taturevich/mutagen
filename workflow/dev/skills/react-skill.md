# React Skill

Use for React UI, hooks, client-side state, or interaction changes.

## Project guidance

- Follow the existing React and TanStack Query patterns. Keep server state in the query layer; do not copy it into component state without a clear reason.
- Put local state in the nearest owner shared by the components that need it. Derive render values when possible.
- Call hooks unconditionally at the top level. Use effects to synchronize with external systems; do not use them to derive render data.
- Keep components and custom hooks focused on one interaction or responsibility.
- Preserve relevant keyboard, focus, validation, loading, error, theme, and responsive behavior.
- For shared controls and styling, load the repo-local `ui-consistency` skill.
