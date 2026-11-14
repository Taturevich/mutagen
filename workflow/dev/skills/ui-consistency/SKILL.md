---
name: ui-consistency
description: Change or audit shared React controls, design tokens, CSS cascade, responsive/theme consistency, or duplicated styling. Skip for behavior-only UI changes that do not affect shared visuals.
---

# UI Consistency

Use this skill when a task changes shared styling or controls, or explicitly requests a UI consistency audit. For behavior-only UI changes, use the React guidance and load this skill only if shared visual behavior is affected. Keep changes aligned with the existing application.

## Work modes

- **Feature work:** inspect the current UI system, reuse its controls and tokens, then implement the requested surface.
- **Audit/refactor:** inspect the requested UI scope for duplicated control behavior, semantic CSS duplication, theme or responsive drift, and cascade complexity. Make changes only when refactoring is requested; otherwise report evidence and recommendations.

Keep the request's scope. A consistency pass is not permission for a broad redesign or unrelated cleanup.

## Inspect the application first

1. Read relevant repository instructions and `workflow/dev/skills/react-skill.md` when component behavior or state is involved.
2. Identify the affected routes, components, and styles. Use semantic search for unfamiliar UI areas when available, then inspect source and nearby tests.
3. Treat the application's current tokens, component APIs, and CSS import/layer structure as the source of truth. In this client, start with:
   - `src/VeronikaStudio.Client/src/styles.css` for style entry points and cascade-layer order.
   - `src/VeronikaStudio.Client/src/styles/tokens.css`, `base.css`, `chrome.css`, `surfaces.css`, `responsive.css`, and `pointer-light.css` for shared design rules.
   - `src/VeronikaStudio.Client/src/components/` for shared React controls.
   - `src/VeronikaStudio.Client/src/areas/<area>/` for feature composition and area-specific styling.
4. For a shared control or shared style change, inspect affected call sites and applicable themes, shells, layouts, and interaction states. For a local feature style, inspect only its relevant route and states. Confirm a CSS variable exists before using it.

## Reuse controls

- Search for a shared component by behavior and accessibility contract, not just by its visual name. Inspect its props, states, styling, and tests before deciding it does not fit.
- Reuse the existing shared control when it covers the interaction. Compose shared primitives for a feature-specific arrangement instead of cloning their interaction or styling.
- If no shared control covers a recurring interaction, add it to the shared component layer and have the feature consume it. Keep the shared API focused on the behavior and states that are actually common; keep feature data and composition in the feature.
- Do not add a feature-local custom control that duplicates a shared control. Prefer semantic native HTML when it satisfies the interaction, and preserve keyboard, focus, validation, disabled, and error behavior.
- Do not create a shared abstraction solely because two pieces look alike. Share when behavior, meaning, and ownership are genuinely common.
- A shared component import does not prove shared styling. Search every call site for area CSS that targets the shared component's classes and compare those overrides with the shared defaults before calling the UI consistent.

## Full consistency audits

Use the following route-by-route comparison only for an explicit audit or a change to a shared component family. A local component fix does not require an unrelated application-wide inventory.

### Compare equivalent components across routes

For audits that touch a shared component family, inventory its call sites and compare the complete rendered contract route by route. Record the component and markup, all matching shared and area-level CSS, responsive/theme rules, interactive states, and any intentional variant. A shared component import is only evidence of shared behavior; it does not establish visual consistency.

Compare the full visual structure, not a checklist of a few conspicuous properties. Include outer and inner boundaries (border presence, width, color, radius, and placement), surfaces, dimensions, spacing, alignment, typography, overflow, and content density. Trace how wrappers, minimum sizes, clipping, and neighboring layout change the apparent component even when its internal rules are shared. A local rule that changes any shared visual responsibility creates a variant and needs a product reason; otherwise remove it or move the common rule to its shared owner.

Trace each meaningful interaction end to end: identify the actual hit target and semantic element, event and state handling, focus/keyboard behavior, CSS state selector, pseudo-element, stacking context, and cascade. Compare hover, focus, active, disabled, error, and reduced-motion behavior across equivalent uses. A visible effect on one nested element does not prove that the whole interactive target receives the intended feedback.

Keep content-specific formatting local only when it belongs to the data or page composition. Shared geometry, boundaries, control styling, and reusable interaction states belong to the shared component and its shared stylesheet. Apply the same whole-contract comparison to tables, buttons, fields, selectors, tabs, pagination, cards, and other repeated component families. Explicit variants are appropriate when they meet a real product need; unexplained per-route differences are not.

## Keep styling coherent

- Reuse existing design tokens and shared component classes for color, typography, borders, spacing, focus, and interaction states. Add a token only for a meaningful design concept that has multiple legitimate uses.
- Put shared control styling in the existing shared styling layer. Keep area styles focused on page composition and genuinely area-specific behavior.
- Follow the established `@layer` order and import conventions. Prefer a small, low-specificity selector that owns a rule over a deep descendant selector or a later corrective override.
- Avoid repeated declarations for the same semantic role, rules that only compensate for earlier local rules, unexplained `!important`, and selectors whose depth makes component ownership unclear. Fix the source rule where practical.
- Consolidate semantic duplicates only after checking their rendered behavior, responsive states, themes, and ownership. Similar appearance alone is not enough reason to merge distinct controls.
- Preserve intentional differences where the product needs them. Do not force unrelated screens into one layout to reduce line count.

## Refactor or audit existing UI

1. Establish a bounded scope: affected route, component family, or explicitly requested frontend-wide audit.
2. Inventory equivalent components across all relevant routes, including shared-component call sites and route styles that override them. Compare markup, complete visual boundaries and geometry, selectors, values, variants, breakpoints, themes, and tests.
3. Trace visual states through the full interaction path: semantic DOM hit target, event/state handling, keyboard and focus behavior, CSS selector, pseudo-element or state rule, stacking, and cascade. Distinguish missing shared behavior from a local override.
4. Trace conflicting declarations through the actual cascade, including layer order, selector specificity, wrappers, sizing constraints, and responsive rules. Distinguish the intended source style from a corrective override and account for how the surrounding layout changes the rendered result.
5. Recommend or make the smallest coherent refactor: reuse an existing component, introduce a shared component or token where justified, or remove stale overrides. Preserve behavior and deliberate product variation.
6. Verify affected views and states using existing tests or the repository-owned commands required by the task. Do not claim visual validation from a build alone.

For audit-only work, report actionable findings with severity, file and line, the duplicated or conflicting semantics, user impact, and a concrete refactoring direction. Include the route-by-route control inventory for shared component families in scope. Separate observed facts from hypotheses. Include areas reviewed and material coverage gaps; do not modify files unless asked.

## Sources

These principles follow the React guidance to decompose interfaces into components around responsibilities, the GOV.UK Design System guidance to start from existing components and iterate, and MDN's guidance to keep specificity manageable and use cascade layers to organize precedence:

- [Thinking in React](https://react.dev/learn/thinking-in-react)
- [GOV.UK Design System components](https://design-system.service.gov.uk/components/)
- [GOV.UK Design System community principles](https://design-system.service.gov.uk/community/community-principles/)
- [MDN: Specificity](https://developer.mozilla.org/en-US/docs/Web/CSS/Guides/Cascade/Specificity)
- [MDN: Cascade layers](https://developer.mozilla.org/en-US/docs/Learn_web_development/Core/Styling_basics/Cascade_layers)
