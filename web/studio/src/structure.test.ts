// W4A: structural edits of the document model (insert, delete, move) and their refusals.

import { describe, expect, it } from 'vitest';
import {
  createNode,
  deleteRefusal,
  indexDocument,
  insertNode,
  insertionPoint,
  keyOf,
  moveNode,
  moveRefusal,
  nodeAt,
  openWorkflow,
  removeNode,
  selectionAfterDelete,
  serialize,
  type Placement,
  type Step,
} from './document';
import { catalog, helloWorld } from './test-support';
import type { JsonObject } from './types';

const activities = new Map(catalog.map((a) => [a.type, a]));
const log = activities.get('Core.Log')!;

function open(text = helloWorld): JsonObject {
  const result = openWorkflow(text);
  if (!result.ok) {
    throw new Error(result.error);
  }

  return result.document;
}

const ids = (document: JsonObject) => indexDocument(document).entries.map((e) => e.node.id);
const pathOf = (document: JsonObject, id: string) => indexDocument(document).entries.find((e) => e.node.id === id)!.path;
const withSlots = JSON.stringify({
  schemaVersion: '1.0',
  root: {
    id: 'main',
    type: 'Core.Sequence',
    children: [{ id: 'if', type: 'Core.If', properties: { condition: 'true' }, slots: { then: { id: 't', type: 'Core.Log' }, else: { id: 'e', type: 'Core.Log' } } }],
  },
});

describe('insertionPoint', () => {
  it('is the end of a selected list, or right after a selected activity in a list', () => {
    const document = open();

    expect(insertionPoint(document, [], activities)).toEqual({ parentPath: [], index: 2 });
    expect(insertionPoint(document, pathOf(document, 'build-greeting'), activities)).toEqual({ parentPath: [], index: 1 });
    expect(insertionPoint(document, pathOf(document, 'log-greeting'), activities)).toEqual({ parentPath: [], index: 2 });
  });

  it('explains why nothing can be inserted at a slot activity or into a non-list root', () => {
    const document = open(withSlots);

    expect(insertionPoint(document, pathOf(document, 't'), activities)).toMatch(/slot 'then'/);
    expect(insertionPoint(open('{ "root": { "id": "l", "type": "Core.Log" } }'), [], activities)).toMatch(/does not contain a list/);
    expect(insertionPoint(open('{ "root": { "id": "x", "type": "Acme.Unknown", "children": [ { "id": "c", "type": "Core.Log" } ] } }'), [{ children: 0 }], activities)).toMatch(
      /not a registered activity/,
    );
  });
});

describe('createNode', () => {
  it('creates only the type and a unique id, like the WPF Studio (type name, lower case, -N)', () => {
    const document = open('{ "root": { "id": "main", "type": "Core.Sequence", "children": [ { "id": "log-1", "type": "Core.Log" } ] } }');

    expect(createNode(document, log)).toEqual({ id: 'log-2', type: 'Core.Log' });
    expect(createNode(open(), activities.get('Core.Sequence')!)).toEqual({ id: 'sequence-1', type: 'Core.Sequence' });
  });
});

describe('insertNode', () => {
  it('places the node, keeps every other node object, and leaves the original document unchanged', () => {
    const document = open();
    const before = indexDocument(document).entries;
    const node = createNode(document, log);

    const inserted = insertNode(document, { parentPath: [], index: 1 }, node);

    expect(ids(inserted.document)).toEqual(['main', 'build-greeting', 'log-1', 'log-greeting']);
    expect(inserted.path).toEqual([{ children: 1 }]);
    expect(nodeAt(inserted.document, inserted.path)).toBe(node);
    expect(nodeAt(inserted.document, [{ children: 0 }])).toBe(before[1].node);
    expect(nodeAt(inserted.document, [{ children: 2 }])).toBe(before[2].node);
    expect(keyOf(inserted.document.root as JsonObject)).toBe(before[0].key);
    expect(ids(document)).toEqual(['main', 'build-greeting', 'log-greeting']);
  });

  it('creates the children list of an empty Sequence', () => {
    const document = open('{ "root": { "id": "main", "type": "Core.Sequence" } }');
    const placement = insertionPoint(document, [], activities) as Placement;

    expect((insertNode(document, placement, { id: 'log-1', type: 'Core.Log' }).document.root as JsonObject).children).toEqual([{ id: 'log-1', type: 'Core.Log' }]);
  });

  it('never writes client keys into the JSON', () => {
    const document = open();
    const inserted = insertNode(document, { parentPath: [], index: 0 }, createNode(document, log)).document;
    indexDocument(inserted);

    expect(JSON.parse(serialize(inserted)).root.children[0]).toEqual({ id: 'log-1', type: 'Core.Log' });
  });
});

describe('removeNode', () => {
  it('removes only the selected node and keeps its siblings in order', () => {
    const document = open();
    const [, assign, logGreeting] = indexDocument(document).entries;

    const removed = removeNode(document, assign.path);

    expect(ids(removed)).toEqual(['main', 'log-greeting']);
    expect(nodeAt(removed, [{ children: 0 }])).toBe(logGreeting.node);
  });

  it('refuses the root with a reason', () => {
    expect(deleteRefusal([])).toMatch(/root activity cannot be deleted/);
    expect(() => removeNode(open(), [])).toThrow(/root/);
  });

  it('empties a slot by removing only that slot entry', () => {
    const document = open(withSlots);

    const removed = removeNode(document, pathOf(document, 't'));

    expect(Object.keys((nodeAt(removed, [{ children: 0 }]).slots as JsonObject))).toEqual(['else']);
  });

  it('selects the next sibling, else the previous one, else the parent', () => {
    const document = open();
    const [main, assign, logGreeting] = indexDocument(document).entries;

    expect(selectionAfterDelete(document, assign.path)).toBe(logGreeting.node);
    expect(selectionAfterDelete(document, logGreeting.path)).toBe(assign.node);
    const single = removeNode(document, assign.path);
    expect(selectionAfterDelete(single, [{ children: 0 }])).toBe(nodeAt(single, []));
    expect(keyOf(nodeAt(single, []))).toBe(main.key);
  });
});

describe('moveNode', () => {
  it('moves up and down within the list, keeping the moved node object and key', () => {
    const document = open();
    const logGreeting = indexDocument(document).entries[2];

    const up = moveNode(document, logGreeting.path, -1);
    const down = moveNode(up.document, up.path, 1);

    expect(ids(up.document)).toEqual(['main', 'log-greeting', 'build-greeting']);
    expect(up.path).toEqual([{ children: 0 }]);
    expect(nodeAt(up.document, up.path)).toBe(logGreeting.node);
    expect(indexDocument(up.document).entries[1].key).toBe(logGreeting.key);
    expect(ids(down.document)).toEqual(['main', 'build-greeting', 'log-greeting']);
  });

  it.each<[string, Step[], -1 | 1, RegExp]>([
    ['the root', [], -1, /root activity cannot be moved/],
    ['the first child up', [{ children: 0 }], -1, /already first/],
    ['the last child down', [{ children: 1 }], 1, /already last/],
  ])('refuses %s', (_name, path, delta, reason) => {
    const document = open();

    expect(moveRefusal(document, path, delta)).toMatch(reason);
    expect(() => moveNode(document, path, delta)).toThrow(reason);
  });

  it('refuses slot activities (moving between slots is not available yet)', () => {
    const document = open(withSlots);

    expect(moveRefusal(document, pathOf(document, 't'), 1)).toMatch(/slot 'then'/);
  });
});
