# ADR-0036: The WPF Studio is archived, not deleted (W10, Phase 5 completion)

- Status: Accepted
- Date: 2026-10-08
- Phase: Web Studio W10 (Phase 5 completion)
- Amends: [ADR-0021](0021-web-first-studio-and-wpf-removal.md) ("One PR deletes `MyRPA.Studio`, `MyRPA.Studio.Core` and
  their tests"); completes [ADR-0035](0035-web-studio-wpf-exit-review.md)

## Context
ADR-0021 planned to delete the WPF Studio once the Web Studio met the exit criteria. The W9 evidence is in
[web-studio-parity.md](../architecture/web-studio-parity.md). On 2026-10-08 the owner decided: "Finish the phase 5 first
but keep the WPF studio in archive."

## Decision
1. **Archive instead of delete.** `MyRPA.Studio`, `MyRPA.Studio.Core`, `MyRPA.Studio.Tests`, `MyRPA.Studio.Core.Tests` and
   the WPF Studio document (`studio.md`) move to `archive/wpf-studio/` with their history (`git mv`). The tag
   `wpf-studio-final` marks the last commit where they are in the solution, build and pass their tests; the archive
   README explains how to build and run them from that tag.
2. **Not built, not tested, not maintained.** The archive is outside the solution, the CI and every folder the
   architecture tests scan (`src`, `tests`, `plugins`, `samples`). Nothing may reference it. It is never a design
   constraint, and it is not updated when the rest of the code changes.
3. **Everything else in ADR-0021's W10 applies:** the WPF exemption is removed from the architecture rules (no project
   may use WPF or Windows Forms or target a Windows-only framework: `PlatformNeutralityTests.NoProjectInTheRepository_UsesDesktopUi`),
   `CommunityToolkit.Mvvm` is removed from the central package versions, the CI no longer mentions Windows-only Studio
   tests, and the PRD, overview, ADR index and `CLAUDE.md` describe the Web Studio as the Studio.
4. **The corpus reference is frozen.** `tests/corpus/expected/locations.json` (written by the archived
   `CorpusParityTests`) stays as the reference the Web Studio's `corpus.test.ts` is checked against. Adding a corpus file
   now needs its expected locations written by hand (or regenerated from the tag).

## Exit review record (ADR-0021 criterion 14)
| Item | State on 2026-10-08 |
|---|---|
| Criteria 1–13, automated evidence | Met: parity document §1–§2 |
| Manual script | Steps 1–11 run in a browser by `npm run manual` (all pass, three findings fixed); the owner ran steps 1–3 by hand. Not done by a person: the browser's own leave prompt (step 11) and the screen-reader pass (step 12) |
| Known differences (§3) | Not decided individually; the proposed dispositions stand until the owner changes them |
| CI | Web Studio jobs (unit, build, smoke, accessibility, performance) green on Linux and Windows; the .NET test job must be green before W10 is merged |
| Owner sign-off | The owner's instruction above, with the open items listed here |

## Consequences
- Phase 5 is complete when this change is merged with CI green; the next phase (6) still needs explicit authorization.
- Reviving the WPF Studio means checking out `wpf-studio-final`, not restoring the archive into the current solution.
