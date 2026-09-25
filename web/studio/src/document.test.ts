import { describe, expect, it } from 'vitest';
import { editability, findLossyNumber, indexDocument, keyOf, openWorkflow, serialize, setDisplayName, setProperty } from './document';
import { catalog, helloWorld } from './test-support';
import type { JsonObject, PropertyDescriptor } from './types';

const open = (text: string) => {
  const result = openWorkflow(text);
  if (!result.ok) {
    throw new Error(result.error);
  }

  return result;
};

const property = (type: string, name: string): PropertyDescriptor => catalog.find((a) => a.type === type)!.properties.find((p) => p.name === name)!;

describe('openWorkflow', () => {
  it('loads the v1.0 JSON as the document and indexes its nodes in order', () => {
    const { document, readOnlyReason } = open(helloWorld);
    const index = indexDocument(document);

    expect(readOnlyReason).toBeUndefined();
    expect(index.entries.map((e) => e.node.id)).toEqual(['main', 'build-greeting', 'log-greeting']);
    expect(index.entries.map((e) => e.depth)).toEqual([0, 1, 1]);
    expect(new Set(index.entries.map((e) => e.key)).size).toBe(3);
    expect(index.byNodeId.get('log-greeting')).toBe(index.entries[2].key);
  });

  it('includes slot nodes with their slot names', () => {
    const { document } = open(
      JSON.stringify({ schemaVersion: '1.0', root: { id: 'if', type: 'Core.If', slots: { then: { id: 't', type: 'Core.Log' }, else: { id: 'e', type: 'Core.Log' } } } }),
    );

    expect(indexDocument(document).entries.map((e) => [e.node.id, e.slot])).toEqual([
      ['if', undefined],
      ['t', 'then'],
      ['e', 'else'],
    ]);
  });

  it('saves the same JSON it opened, without client keys', () => {
    const { document } = open(helloWorld);
    indexDocument(document);

    const saved = serialize(document);

    expect(JSON.parse(saved)).toEqual(JSON.parse(helloWorld));
    expect(saved).not.toMatch(/"(key|__key|_key)"/);
  });

  it.each(['1.0', '1e3', '-0', '12345678901234567890', '0.10'])('opens files with the number %s read-only instead of rewriting it', (number) => {
    const result = open(`{ "schemaVersion": "1.0", "variables": [ { "name": "x", "type": "Decimal", "default": ${number} } ] }`);

    expect(result.readOnlyReason).toContain(number);
  });

  it('does not mistake numbers inside strings or ordinary numbers for lossy ones', () => {
    expect(findLossyNumber('{ "a": "1.0 and 1e3", "b": 42, "c": -3.25, "d": 0 }')).toBeUndefined();
  });

  it('opens objects with numeric keys read-only (JavaScript would reorder them)', () => {
    expect(open('{ "root": { "id": "a", "properties": { "arguments": { "b": "1", "2": "x" } } } }').readOnlyReason).toBeDefined();
  });

  it('refuses what it cannot parse', () => {
    expect(openWorkflow('{ "a": 1, // comment\n }').ok).toBe(false);
    expect(openWorkflow('[1, 2]').ok).toBe(false);
  });
});

describe('edits', () => {
  it('replace only the path to the edited node, keep its key and leave the original untouched', () => {
    const { document } = open(helloWorld);
    const before = indexDocument(document);
    const [rootEntry, assign, log] = before.entries;

    const edited = setProperty(document, log.path, property('Core.Log', 'message'), "'changed'");
    const after = indexDocument(edited);

    expect(after.entries[2].node.properties).toEqual({ message: "'changed'" });
    expect(after.entries[2].key).toBe(log.key);
    expect(after.entries[0].key).toBe(rootEntry.key);
    expect(after.entries[0].node).not.toBe(rootEntry.node);
    expect(after.entries[1].node).toBe(assign.node);
    expect((before.entries[2].node.properties as JsonObject).message).toBe('greeting');
  });

  it('preserve unknown fields everywhere', () => {
    const text = JSON.stringify({ schemaVersion: '1.0', 'x-doc': { a: [1, 2] }, root: { id: 'l', type: 'Core.Log', 'x-node': true, properties: { message: 'm', 'x-prop': null } } });
    const { document } = open(text);

    const edited = setProperty(document, [], property('Core.Log', 'message'), "'n'");

    expect(JSON.parse(serialize(edited))).toEqual({ schemaVersion: '1.0', 'x-doc': { a: [1, 2] }, root: { id: 'l', type: 'Core.Log', 'x-node': true, properties: { message: "'n'", 'x-prop': null } } });
  });

  it('remove an optional property set to empty, but keep an empty required one for validation', () => {
    const { document } = open('{ "root": { "id": "l", "type": "Core.Log", "properties": { "message": "m", "level": "Warning" } } }');

    const cleared = setProperty(setProperty(document, [], property('Core.Log', 'level'), ''), [], property('Core.Log', 'message'), '');

    expect((cleared.root as JsonObject).properties).toEqual({ message: '' });
  });

  it('set and remove the display name', () => {
    const { document } = open(helloWorld);
    const named = setDisplayName(document, [], 'Main flow');

    expect((named.root as JsonObject).displayName).toBe('Main flow');
    expect('displayName' in (setDisplayName(named, [], '').root as JsonObject)).toBe(false);
  });

  it('keep keys stable across many edits of the same node', () => {
    let { document } = open(helloWorld);
    const key = indexDocument(document).entries[2].key;
    for (const text of ['a', 'ab', 'abc']) {
      document = setProperty(document, indexDocument(document).byKey.get(key)!.path, property('Core.Log', 'message'), text);
    }

    expect(keyOf(indexDocument(document).entries[2].node)).toBe(key);
  });
});

describe('editability', () => {
  it('edits strings of text-like kinds and shows literals and maps read-only', () => {
    const message = property('Core.Log', 'message');

    expect(editability(message, 'greeting')).toEqual({ editable: true, text: 'greeting' });
    expect(editability(message, undefined)).toEqual({ editable: true, text: '' });
    expect(editability(message, 42).editable).toBe(false);
    expect(editability(message, null).editable).toBe(false);
    expect(editability({ ...message, kind: 'ExpressionMap' }, { a: '1' }).editable).toBe(false);
  });
});
