## UI Test Locator Rules

Use `data-testid` as the automation id for UI tests.

Rules:
- Locate actionable UI elements with `Page.GetByTestId(...)` or a scoped `locator.GetByTestId(...)`.
- Do not use CSS class selectors, text selectors, role selectors, label selectors, `Nth(...)`, or `First` to find elements for normal UI flow tests.
- If a required element has no automation id, add a `data-testid` to the app markup first, then use that id in the test.
- Automation ids must be unique within the rendered page state.
- For repeated or dynamic elements, generate stable ids from domain keys such as service ids, entry ids, appointment ids, or a deterministic slug of a unique test value. Examples: `booking-service-{service.id}`, `appointment-card-{client-slug}`, `manual-reservation-service-{service.id}`.
- Do not assert styling through ordinary flow tests. Styling assertions belong only in tests whose purpose is visual/theme/styling behavior, and those tests must still locate the target element by automation id.
- Copy/localization assertions may assert text after the element is located by automation id. Root document state assertions such as `html` language or theme may use direct document evaluation or the root locator.
- Select inputs should be found by automation id. When waiting for options, inspect the found select element rather than locating option elements separately.
- Prefer real user-like interaction for interacting with elements like mouse pointers hovering over programmatic clicks. Use `locator.Click()` or `locator.Hover()` rather than `locator.Evaluate("el => el.click()")` or `locator.Evaluate("el => el.dispatchEvent(new MouseEvent('click', { bubbles: true }))")`.
