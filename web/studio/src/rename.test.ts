import { describe, expect, it } from 'vitest';
import { keyOf, nodeAt } from './document';
import { pathSegments, renameReferences, usagesOf } from './rename';
import { catalog } from './test-support';
import type { JsonObject, NameReference } from './types';

// Rename and usages (ADR-0041, E-3): exact edits at the places the server reported.

const document: JsonObject = {
  schemaVersion: '1.1',
  id: 'w',
  name: 'W',
  version: '1',
  variables: [{ name: 'n', type: 'Int' }],
  root: {
    id: 'main',
    type: 'Core.Sequence',
    children: [
      { id: 'set', type: 'Core.Assign', properties: { to: 'n', value: 'n + len(items)' } },
      { id: 'say', type: 'Core.Log', properties: { message: "'n is ' + n" } },
      { id: 'pick', type: 'Core.Switch', properties: { expression: 'n' }, slots: { 'case:1.5': { id: 'inner', type: 'Core.Log', properties: { message: 'n' } } } },
    ],
  },
};

const ref = (path: string, start: number, length = 1, declaration = false): NameReference => ({ path, start, length, declaration });
const references = [
  ref('$.variables[0].name', 0, 1, true),
  ref('$.root.children[0].properties.to', 0),
  ref('$.root.children[0].properties.value', 0),
  ref('$.root.children[1].properties.message', 10),
  ref('$.root.children[2].slots.case:1.5.properties.message', 0),
];

describe('pathSegments', () => {
  it('follows the diagnostics’ paths through the document, also through keys with dots', () => {
    expect(pathSegments(document, '$.root.children[1].properties.message')).toEqual(['root', 'children', 1, 'properties', 'message']);
    expect(pathSegments(document, '$.root.children[2].slots.case:1.5.properties.message')).toEqual(['root', 'children', 2, 'slots', 'case:1.5', 'properties', 'message']);
    expect(pathSegments(document, '$.variables[0].name')).toEqual(['variables', 0, 'name']);
    expect(pathSegments(document, '$.root.children[9]')).toBeUndefined();
    expect(pathSegments(document, '$.nope')).toBeUndefined();
  });
});

describe('renameReferences', () => {
  it('changes exactly the reported characters: never a string’s text; other nodes stay the same objects', () => {
    const renamed = renameReferences(document, references, 'n', 'count') as JsonObject;

    expect((renamed.variables as JsonObject[])[0].name).toBe('count');
    expect(nodeAt(renamed, [{ children: 0 }]).properties).toEqual({ to: 'count', value: 'count + len(items)' });
    expect((nodeAt(renamed, [{ children: 1 }]).properties as JsonObject).message).toBe("'n is ' + count");
    expect((nodeAt(renamed, [{ children: 2 }, { slot: 'case:1.5' }]).properties as JsonObject).message).toBe('count');
    expect(keyOf(nodeAt(renamed, [{ children: 1 }]))).toBe(keyOf(nodeAt(document, [{ children: 1 }])));
    expect(renamed.root).not.toBe(document.root);
  });

  it('refuses when a reported place no longer holds the old name (the document changed)', () => {
    expect(renameReferences(document, [ref('$.root.children[1].properties.message', 0)], 'n', 'count')).toBe('The workflow changed since its uses were found; try again.');
    expect(renameReferences(document, [ref('$.root.children[7].properties.message', 0)], 'n', 'count')).toBe('The workflow changed since its uses were found; try again.');
  });
});

describe('usagesOf', () => {
  it('names the activity and what of it uses the name, leaving out the declaration', () => {
    expect(usagesOf(document, references, new Map(catalog.map((a) => [a.type, a]))).map((u) => `${u.label}: ${u.where}`)).toEqual([
      'Assign: to',
      'Assign: value',
      'Log: message',
      'Log: message',
    ]);
    expect(usagesOf(document, [ref('$.root.children[1].transitions[0].when', 0)], new Map()).map((u) => u.where)).toEqual(['transition 1']);
  });
});
