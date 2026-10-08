// W9 corpus parity (ADR-0035). The shared corpus (tests/corpus) was validated by the WPF Studio's DraftValidator in
// CorpusParityTests (archive/wpf-studio since W10, ADR-0036), which recorded where WPF places every diagnostic in
// tests/corpus/expected/locations.json: now the frozen reference.
// Here the Web Studio opens the same files and must place the same diagnostics on the same node, property, row or the
// workflow, and must write every file it can edit back unchanged.
import { readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { indexDocument, openWorkflow, serialize, setDisplayName } from './document';
import type { Diagnostic, JsonObject } from './types';
import { diagnosticTarget, nodeJsonPath } from './workflowData';

interface WpfLocation {
  readonly kind: 'node' | 'row' | 'workflow';
  readonly nodePath?: string;
  readonly nodeId?: string | null;
  readonly property?: string | null;
  readonly list?: string;
  readonly index?: number;
}

interface GoldenFile {
  readonly file: string;
  readonly readable: boolean;
  readonly diagnostics?: readonly (Diagnostic & { readonly wpf: WpfLocation })[];
}

// Vitest runs in web/studio (jsdom's import.meta.url is not a file URL).
const corpus = resolve(process.cwd(), '../../tests/corpus');
const golden = JSON.parse(readFileSync(join(corpus, 'expected', 'locations.json'), 'utf8')) as GoldenFile[];
const text = (file: string) => readFileSync(join(corpus, file), 'utf8');

/** Files only one Studio opens; every other corpus file opens in both. Recorded in docs/architecture/web-studio-parity.md. */
const wpfCannotOpen = ['diag-metadata.json']; // `"version": 7`: WPF's draft model needs a string; the Web Studio shows MYRPA1004.
const webCannotOpen = ['known-comments.json']; // JSON comments and trailing commas: JSON.parse rejects them (WPF drops them on save).

describe('corpus parity with the WPF DraftValidator', () => {
  it('covers every corpus file', () => {
    const files = readdirSync(corpus).filter((name) => name.endsWith('.json')).sort();
    expect(golden.map((entry) => entry.file)).toEqual(files);
    expect(golden.filter((entry) => !entry.readable).map((entry) => entry.file)).toEqual(wpfCannotOpen);
  });

  for (const entry of golden.filter((e) => e.readable)) {
    it(`places every diagnostic of ${entry.file} where WPF does`, () => {
      const opened = openWorkflow(text(entry.file));
      if (webCannotOpen.includes(entry.file)) {
        expect(opened.ok).toBe(false);
        return;
      }

      if (!opened.ok) {
        throw new Error(opened.error);
      }

      const index = indexDocument(opened.document);
      for (const diagnostic of entry.diagnostics ?? []) {
        const wpf = diagnostic.wpf;
        const target = diagnosticTarget(diagnostic, opened.document);
        const where = `${diagnostic.code} at ${diagnostic.path}`;
        if (wpf.kind === 'node') {
          const node = target.kind === 'node' && target.key !== undefined ? index.byKey.get(target.key) : undefined;
          expect({ where, path: node && nodeJsonPath(node.path), nodeId: target.kind === 'node' ? (target.nodeId ?? '') : undefined, property: target.kind === 'node' ? (target.property ?? null) : undefined }).toEqual({
            where,
            path: wpf.nodePath,
            nodeId: wpf.nodeId ?? '',
            property: wpf.property ?? null,
          });
        } else if (wpf.kind === 'row') {
          expect({ where, ...target }).toEqual({ where, kind: 'row', list: wpf.list, index: wpf.index });
        } else {
          expect({ where, kind: target.kind }).toEqual({ where, kind: 'workflow' });
        }
      }
    });
  }

  it('compares every diagnostic code the WPF Studio can report', () => {
    const codes = new Set(golden.flatMap((entry) => (entry.diagnostics ?? []).map((d) => d.code)));
    // The 26 codes CorpusParityTests requires (every MYRPA10xx code but 1001–1004, which WPF cannot report on an open file).
    expect(codes.size).toBeGreaterThanOrEqual(26);
  });
});

describe('corpus round trip', () => {
  const files = readdirSync(corpus).filter((name) => name.endsWith('.json') && !webCannotOpen.includes(name));

  for (const file of files) {
    it(`writes ${file} back unchanged, or opens it read-only`, () => {
      const original = text(file);
      const opened = openWorkflow(original);
      if (!opened.ok) {
        throw new Error(opened.error);
      }

      if (file.includes('lossy')) {
        expect(opened.readOnlyReason).toBeDefined();
        return;
      }

      expect(opened.readOnlyReason).toBeUndefined();
      // Same values, same key order, unknown fields kept (formatting is the Studio's own: two-space JSON).
      expect(JSON.stringify(JSON.parse(serialize(opened.document)))).toBe(JSON.stringify(JSON.parse(original)));
    });
  }

  it('keeps unknown fields at every level through an edit', () => {
    const opened = openWorkflow(text('roundtrip-valid.json'));
    if (!opened.ok) {
      throw new Error(opened.error);
    }

    const edited = JSON.parse(serialize(setDisplayName(opened.document, [{ children: 0 }], 'Count'))) as JsonObject;
    const root = edited.root as JsonObject;
    expect(edited['x-top-note']).toBe('unknown fields are kept');
    expect(root['x-node-note']).toEqual({ kept: [1, 2, { deep: true }] });
    expect(((edited.arguments as JsonObject[])[2])['x-arg-note']).toBe(1);
    expect(((root.children as JsonObject[])[0]).displayName).toBe('Count');
  });
});
