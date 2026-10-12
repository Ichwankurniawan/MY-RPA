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

## Amendment (owner, 2026-10-12): the "Atlas" colour scheme

The owner found the lime-on-navy look generic ("AI-generated") and chose a refined scheme after reviewing interactive
previews (Atlas E′, refined).
- **Neutrals:** warm graphite in dark (`#141517` background, `#1b1c1f` panel) and off-white paper in light (`#f4f4f2`).
- **Brand:** cobalt. Light accent `#2f5bd8`; dark accent `#6e94ff`. The primary button is `#2f5bd8` (light) or `#3b6cf6`
  (dark), with white text. The title bar is a solid cobalt `#1d3f9e` with white text in both themes.
- **Logo:** the same mark (L and play triangle) in cobalt. It is drawn white with a light-blue facet on the header. The
  favicon matches.
- **Family hues** (`--hue-*`) are used only for icons and markers, never for fills or buttons:
  - blue: control flow, Workflows;
  - violet: data, Deployments;
  - teal: browser, Monitoring;
  - gold: files, Robots;
  - pink: email and AI;
  - indigo: database, Admin;
  - sky: scripting and HTTP.

  Orange is deliberately absent, so the Studio is not mistaken for another RPA brand.
- **Status** is shown as a dot with plain text. Green means succeeded, red failed, cobalt running or busy, gold
  interrupted, offline or paused, and grey queued or cancelled. Status colours never mean a family.
- **Shapes:** corners are 4 px (6 px for large surfaces); there are no glows. A selected card has a cobalt left edge.
  The rail shows grey icons, and the current page shows its hue with a 2 px bar.
- **Typeface:** IBM Plex Sans (OFL-1.1), bundled through `@fontsource/ibm-plex-sans`, so the CSP still allows only
  `'self'`.
- **Contrast:** checked by `npm run a11y`, with no serious or critical violations in either theme.

## Amendment (owner, 2026-10-12): Laconi Automation, with Studio as its workflow builder

The owner: "Let's call the web portal Laconi Automation. Studio is meant only for the workflow build. Deployment also
starts with project selection. Workflows/Studio start with project selection/creation." Decisions (with the owner's
choices of the recommended options):

- **The portal is Laconi Automation** (header wordmark "laconi Automation", page title, Home). **Studio** is its
  workflow builder: the only place with the editing toolbar, the workflow picker and the designer.
- **Rail:** Home · Studio · Deployments · Monitoring · Robots · Agent tools · Admin (each still only when it works).
  **Studio replaces Projects and Workflows:** it starts with the projects (choose one, or New project…), then the
  chosen project's workspace. The rail's Studio returns to where Studio was; the title bar shows **Studio / project**
  in Studio and the section's name elsewhere.
- **Laconi Automation opens on Home** (the project used last is preselected for Home's lists; `--open` still opens its
  file in Studio). **Home is a portal overview** from real data only: the projects (into Studio), the preselected
  project's workflows, today's jobs and Runners with links (when the Server runs the orchestrator), this session's
  runs, and the platform (activities, plugins, agent tools).
- **Deployments starts with the projects** (a process is a project, ADR-0049 amendment): each card says whether it is
  published and deployed, its schedules and last job; choosing one shows its publishing (entry points of that
  project's saved files), versions, Run now, schedules and executions. Processes of projects no longer on the Server
  are listed apart and can still be deployed and run.
- Code names (`MyRPA.*`, `myrpa`, `MyRPA.Server`, the `web/studio` folder) are unchanged.
