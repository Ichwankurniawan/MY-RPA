# ADR-0044: Laconi brand and the Studio's visual design

- Status: **Accepted** (owner, 2026-10-09: the "Laconi — Brand Identity & Studio UI Redesign" brief with its logo and
  Studio mockups: "Start implementing the brand identity and UI redesign now.").
- Builds on: ADR-0021/0028 (Web Studio), UX-1 to UX-3 (`docs/architecture/studio-ux-plan.md`: tokens, icons, cards),
  ADR-0032 (no UI or editor libraries), ADR-0042 (catalog metadata).

## Context
The product is named **Laconi** ("Design it. Run it. Lakoni.", after the Javanese *lakoni*, "do it"); the Studio
becomes **Laconi Studio**. The owner supplied a logo (a lime L joined with a play symbol) and a dark, lime-accented
Studio mockup. The mockup also shows things MyRPA does not have yet (runners, schedules, logs, deploy, branches, a
graph canvas with Start/End, property tabs) and must not be imitated with fake data or handlers.

## Decision
1. **Brand scope:**
   - The visible product name, logo, favicon and page title become Laconi / Laconi Studio.
   - Code names stay as they are: .NET namespaces and assemblies `MyRPA.*`, the `myrpa` CLI, the server executable
     `MyRPA.Server`, the `X-MyRPA-Request` header, file formats and activity type names.
   - Messages that name the server program keep calling it `MyRPA.Server`, because that is what the user runs.
   - Renaming code is a separate decision.
2. **Logo:** an SVG mark drawn for the project, used as the app icon (favicon) and in the header with the lowercase
   wordmark *laconi* and *Studio*:
   - a thick lime L whose foot rises to the tip of a play triangle;
   - a darker green facet fills the triangle.
3. **Design tokens:**
   - The existing CSS variables (UX-1) keep their names and take the brand values, with dark as the reference theme:
     - backgrounds `#0F172A`, `#07131D`, panels `#111F2B`, border `#263746`;
     - text `#F8FAFC`, muted `#94A3B8`;
     - lime `#A3E635`, cyan `#22D3EE`, error `#F87171`, warning `#FBBF24`.
   - A light theme keeps the brand with a darker lime for text on white (contrast).
   - Lime marks the primary action (Run), selection and focus; it is used sparingly.
   - Type: Inter when the system has it, otherwise the system UI font. Web fonts cannot be loaded from elsewhere
     (CSP `default-src 'self'`), and no font package is added.
4. **Shell:**
   - A brand header and a narrow navigation rail. The rail shows only working pages:
     - **Workflows**: the Studio, and the start page;
     - **Home**: real information only: the project's workflows with Open, New workflow, the server's mode and
       version, and the loaded plugins and activity counts.
   - Pages for runners, schedules, logs or settings are not shown until those features exist.
5. **Studio:**
   - Restyled, never replaced. The toolbar keeps every command, shortcut and disabled reason; the primary commands
     show labels, the editing commands show icons with their names as accessible names and tooltips.
   - The activity list adds namespace filter chips and per-activity icons, both derived from the catalog:
     - icons come from the type for built-ins, otherwise from the activity's declared side effects;
     - a new plugin needs no Studio change.
   - The card designer, the property editors and the bottom panel keep their behaviour and get the new tokens and
     spacing.

## Alternatives considered
- **Copying the mockup as is** (graph with Start/End, property tabs, runner/schedule pages, avatar, deploy): rejected.
  It would show features that do not exist and would need editor and backend work outside this change.
- **A UI kit or a design-system package:** rejected. ADR-0032 keeps the Studio free of UI libraries, and tokens in CSS
  are enough.
- **Bundling Inter as a font package:** possible later if the owner wants exact typography on every machine.

## Consequences
- The Studio's look changes everywhere through the tokens. Behaviour, accessibility names, shortcuts and the workflow
  format are unchanged, and the browser scripts and Vitest suites must keep passing.
- Future modules (runners, schedules, AI) get a place in the rail when they exist.
