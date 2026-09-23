# ADR-0001: Record architecture decisions in `docs/adr/`

- Status: Accepted
- Date: 2026-09-23
- Phase: 1

## Context
PRD §28–29 suggests `docs/decisions/ADR-00N-*.md`. The Phase 1 authorization explicitly requires `docs/adr/`.
Decisions must be traceable to evidence (Phase 0 research) and survive across phases.

## Decision
- ADRs live in `docs/adr/NNNN-kebab-title.md`, numbered sequentially, never renumbered.
- Each ADR has: Status, Date, Phase, Context, Decision, Alternatives, Consequences.
- Superseding an ADR: add a new ADR and mark the old one `Superseded by ADR-XXXX`.
- Reference Phase 0 evidence with research codes (R#/D#/N# from `docs/research/openrpa-analysis.md`).

## Alternatives
- `docs/decisions/` as in the PRD — rejected only because the newer, explicit instruction names `docs/adr/`.

## Consequences
- The PRD §28 folder name differs from the repository; `docs/README.md` points here.
