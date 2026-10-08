# ADR-0038: Browser selectors — final format, strategies and generation (Phase 6)

- Status: Accepted (owner, 2026-10-08: "proceed to phase 6"; strategies: browser ones now, Automation ID in Phase 7)
- Date: 2026-10-08
- Phase: 6 (Selectors & Recorder)
- Amends: [ADR-0017](0017-browser-automation-provider.md) ("Selectors (provisional; the final format is Phase 6)")

## Context
Phase 4 shipped a provisional selector text (`css=`, `xpath=`, `text=`, `role=…|name`, steps joined by ` >> `), parsed
into the SDK `Selector` (provider + steps of a strategy and a value). The SDK already names seven strategies (Css,
XPath, Text, Role, Accessibility, Attributes, AutomationId). Phase 6 (PRD §6.1–6.2, 6.5) needs the final format, the
missing strategies, and selectors generated from real elements that do not depend on coordinates.

## Decision

### 1. The selector text (the `selector` property of `Browser.*` activities)
Everything that was valid in Phase 4 keeps its meaning. Steps are joined by ` >> `; each searches within the previous
match. Element operations stay strict: exactly one match.

| Syntax | Strategy | Meaning |
|---|---|---|
| `css=…` (or no prefix) | Css | CSS selector. |
| `xpath=…` (or text starting with `/` or `(`) | XPath | XPath. |
| `text=Sign in` | Text | Elements containing the text (case-insensitive, whitespace-normalized). |
| `text="Sign in"` (new) | Text | Elements whose whole text is exactly `Sign in`. |
| `role=button` / `role=button\|Sign in` | Role | ARIA role, optionally with the exact accessible name. |
| `label=Email` (new) | Accessibility | Form controls labelled exactly `Email` (`<label>`, `aria-label`, `aria-labelledby`). |
| `attr=name=email` (new) | Attributes | Elements whose attribute `name` equals `email` exactly. |
| `testid=submit` (new) | Attributes | Shorthand for `attr=data-testid=submit`. |

- `Automation ID` is a Windows UI Automation concept: it arrives with Windows automation (Phase 7). The browser provider
  rejects it with `InvalidSelector`; there is no placeholder.
- Values cannot contain ` >> ` (as before). `attr=` names are HTML attribute names (`[A-Za-z_:][-A-Za-z0-9_:.]*`).

### 2. Resolution (PRD §6.2)
Selector → provider (`Browser.Playwright`) → candidate elements (one Playwright locator per step, scoped by the previous
one) → match (exactly one, otherwise `ElementNotFound` / `AmbiguousMatch`) → automation element. Unchanged from Phase 4
apart from the new strategies.

### 3. Generation (used by the recorder, ADR-0039)
For an element the user acted on, candidates are produced in this order and kept only if they match **exactly one**
element on the page at that moment:
1. `testid=` (`data-testid`, `data-test`, `data-qa` as `attr=`);
2. `role=<role>|<accessible name>` when the element has a role and a non-empty name;
3. `label=` for labelled form controls;
4. `attr=` for stable attributes: `name`, `placeholder`, `aria-label`, `title`, `alt`, `href` (links), `id` when it does
   not look generated (no long digit runs or hashes);
5. `text="…"` (exact, short visible text);
6. a short CSS path (`css=`), then XPath as the last resort.

The first unique candidate becomes the activity's selector; up to two more are kept as alternatives for the user to
choose in the recorder (they are not saved in the workflow: there is one selector per activity, and no automatic
"healing" in Phase 6). Coordinates are never a selector.

## Consequences
- Existing workflows and samples keep working; `browser-automation.md` documents the final syntax.
- The `BrowserSelectors` parser gains three prefixes and an exact-text form; tests cover parsing, formatting,
  resolution against real pages, and rejections.
- Generation runs inside a page, so it exists only in recorder sessions (ADR-0039).
