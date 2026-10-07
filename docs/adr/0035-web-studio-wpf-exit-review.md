# ADR-0035: Web Studio WPF exit review — corpus parity and the exit evidence (W9)

- Status: Accepted for W9; the exit review itself awaits the manual run and the owner's sign-off
- Date: 2026-10-08
- Phase: Web Studio W9
- Builds on: [ADR-0021](0021-web-first-studio-and-wpf-removal.md) (exit criteria 1–14),
  [ADR-0032](0032-web-studio-rich-authoring.md) (diagnostic locations), [ADR-0034](0034-web-studio-quality-review.md)

## Context
ADR-0021 allows deleting the WPF Studio only when the Web Studio is proven at parity, including "corpus parity with the
Phase 5 `DraftValidator`" (criteria 1 and 4), TypeScript coverage of every Studio.Core behavior group (criterion 12),
the docs and manual script migrated, a person running the script, and the owner's sign-off (criterion 14).

## Decision
1. **One shared corpus, with WPF as the reference.** `tests/corpus/*.json` holds valid files (every value kind, maps,
   slots with dotted names, unknown fields at every level), diagnostic files for every code the WPF Studio can report,
   a lossy-literal file and a file with comments. A .NET test in the WPF test project (`CorpusParityTests`) runs the
   unchanged WPF `DraftValidator` on it and records each diagnostic's location (node path and id, property, row, or
   the workflow) in `tests/corpus/expected/locations.json`; it fails when the file is stale (`MYRPA_UPDATE_CORPUS=1`
   rewrites it) or when a reachable code is missing. The Web Studio's `corpus.test.ts` reads the same file and requires
   equal locations, and that every editable corpus file round-trips unchanged. No shared .NET authoring layer is added;
   the WPF code is only read.
2. **Diagnostics are located by tree position, as in WPF.** The Web Studio used `nodeId`, which misplaced invalid,
   missing and duplicate ids, unknown fields on nodes, unknown slots and slot names containing `.properties`. It now
   applies WPF's rule to the validated document: the longest node JSON path that prefixes the diagnostic path with a
   remainder a node can have, else the longest prefix; the property follows that node's `.properties.`. Targets carry
   the node's client key; tree error marks (`errorNodeKeys`) and *go to problem* use it, so they survive later edits.
   Lookups use a per-document path map (no scan per diagnostic).
3. **Unreachable codes are stated, not faked.** MYRPA1001/1002/1004 files do not open in WPF and MYRPA1003 cannot come
   out of its draft model; they are excluded from the comparison and listed in the parity document.
4. **Evidence lives in one document.** [web-studio-parity.md](../architecture/web-studio-parity.md) maps every WPF test
   group to Web evidence, lists the known differences with proposed dispositions, holds the Web Studio manual test
   script (replacing [studio.md](../architecture/studio.md) §8) with a screen-reader pass, and the sign-off table.
5. **W10 stays blocked** until a person has run the script, CI has run on GitHub, and the owner has signed off.

## Consequences
- ADR-0021 criteria 1, 4 and 12 have automated evidence; criterion 14 has its script and checklist, not its sign-off.
- Adding a corpus file requires regenerating the golden file with the WPF Studio while it still exists; after W10 the
  golden file stays as the frozen reference and the Web test keeps checking against it.
