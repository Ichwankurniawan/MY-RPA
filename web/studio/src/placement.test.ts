import { describe, expect, it } from 'vitest';
import { indexDocument, nodeAt, type Step } from './document';
import { moveTo, moveToRefusal, parseNodes, place, placeRefusal, prepareForPaste, selectionTarget, serializeNodes, slotDescriptor, type Target } from './placement';
import { catalog } from './test-support';
import type { ActivityDescriptor, JsonObject } from './types';

const switchActivity: ActivityDescriptor = {
  type: 'Core.Switch',
  displayName: 'Switch',
  category: 'Control Flow',
  allowsChildren: false,
  properties: [{ name: 'expression', kind: 'Expression', required: true, allowedValues: [], scopeSlots: [] }],
  slots: [
    { name: 'case:', required: false, prefix: true },
    { name: 'default', required: false, prefix: false },
  ],
};
const types = new Map([...catalog, switchActivity].map((a) => [a.type, a]));

const workflow: JsonObject = {
  schemaVersion: '1.0',
  id: 'w',
  name: 'W',
  version: '1',
  root: {
    id: 'main',
    type: 'Core.Sequence',
    children: [
      { id: 'a', type: 'Core.Log', properties: { message: "'a'" } },
      { id: 'check', type: 'Core.If', properties: { condition: 'true' }, slots: { then: { id: 'inner', type: 'Core.Sequence', children: [{ id: 'b', type: 'Core.Log' }] } } },
      { id: 'pick', type: 'Core.Switch', properties: { expression: 'x' }, slots: { 'case:1': { id: 'one', type: 'Core.Log' } } },
      { id: 'c', type: 'Core.Log' },
    ],
  },
};

const pathOf = (document: JsonObject, id: string): Step[] => [...indexDocument(document).byKey.get(indexDocument(document).byNodeId.get(id)!)!.path];
const ids = (document: JsonObject) => indexDocument(document).entries.map((e) => e.node.id).join(',');

describe('Placement rules', () => {
  it('knows fixed and prefix slots', () => {
    expect(slotDescriptor(switchActivity, 'default')?.name).toBe('default');
    expect(slotDescriptor(switchActivity, 'case:2')?.name).toBe('case:');
    expect(slotDescriptor(switchActivity, 'case:')).toBeUndefined();
    expect(slotDescriptor(switchActivity, 'other')).toBeUndefined();
  });

  it('allows lists of list activities and empty slots, and says why anything else is refused', () => {
    const at = (id: string, position: Target['position']): Target => ({ parentPath: pathOf(workflow, id), position });
    expect(placeRefusal(workflow, at('main', { index: 4 }), types)).toBeUndefined();
    expect(placeRefusal(workflow, at('main', { index: 5 }), types)).toBe('The position is outside the list.');
    expect(placeRefusal(workflow, at('check', { slot: 'else' }), types)).toBeUndefined();
    expect(placeRefusal(workflow, at('check', { slot: 'then' }), types)).toBe("The slot 'then' already contains an activity.");
    expect(placeRefusal(workflow, at('check', { slot: 'body' }), types)).toBe("If has no slot 'body'.");
    expect(placeRefusal(workflow, at('a', { index: 0 }), types)).toBe('Log does not contain a list of activities.');
    expect(placeRefusal(workflow, at('pick', { slot: 'case:2' }), types)).toBeUndefined();
    expect(placeRefusal(workflow, at('pick', { slot: 'case:1' }), types)).toBe("The slot 'case:1' already contains an activity.");
  });

  it('places into a list or a slot', () => {
    const node = { id: 'new', type: 'Core.Log' };
    const inSlot = place(workflow, { parentPath: pathOf(workflow, 'check'), position: { slot: 'else' } }, node);
    expect(nodeAt(inSlot.document, inSlot.path)).toBe(node);
    expect(ids(inSlot.document)).toBe('main,a,check,inner,b,new,pick,one,c');
    const inCase = place(workflow, { parentPath: pathOf(workflow, 'pick'), position: { slot: 'case:2' } }, node);
    expect(Object.keys(nodeAt(inCase.document, pathOf(workflow, 'pick')).slots as JsonObject)).toEqual(['case:1', 'case:2']);
  });
});

describe('Moving across containers', () => {
  it('moves a node into a slot, out of a slot, and into another list, keeping its object (and client key)', () => {
    const a = nodeAt(workflow, pathOf(workflow, 'a'));
    const intoElse = moveTo(workflow, pathOf(workflow, 'a'), { parentPath: pathOf(workflow, 'check'), position: { slot: 'else' } }, types);
    expect(ids(intoElse.document)).toBe('main,check,inner,b,a,pick,one,c');
    expect(nodeAt(intoElse.document, intoElse.path)).toBe(a);

    const outOfCase = moveTo(workflow, pathOf(workflow, 'one'), { parentPath: [], position: { index: 0 } }, types);
    expect(ids(outOfCase.document)).toBe('main,one,a,check,inner,b,pick,c');

    const intoInner = moveTo(workflow, pathOf(workflow, 'c'), { parentPath: pathOf(workflow, 'inner'), position: { index: 0 } }, types);
    expect(ids(intoInner.document)).toBe('main,a,check,inner,c,b,pick,one');
  });

  it('adjusts positions after removal: within one list, and a target path that passes later siblings', () => {
    const down = moveTo(workflow, pathOf(workflow, 'a'), { parentPath: [], position: { index: 3 } }, types);
    expect(ids(down.document)).toBe('main,check,inner,b,pick,one,a,c');
    // `inner` is under `check`, which shifts from index 1 to 0 when `a` (index 0) leaves the root list.
    const deeper = moveTo(workflow, pathOf(workflow, 'a'), { parentPath: pathOf(workflow, 'inner'), position: { index: 1 } }, types);
    expect(ids(deeper.document)).toBe('main,check,inner,b,a,pick,one,c');
  });

  it('refuses moving the root or into itself, and does nothing for a move to where it already is', () => {
    expect(moveToRefusal(workflow, [], { parentPath: [], position: { index: 0 } }, types)).toBe('The root activity cannot be moved.');
    expect(moveToRefusal(workflow, pathOf(workflow, 'check'), { parentPath: pathOf(workflow, 'inner'), position: { index: 0 } }, types)).toBe('An activity cannot be moved into itself.');
    expect(moveToRefusal(workflow, pathOf(workflow, 'a'), { parentPath: pathOf(workflow, 'check'), position: { slot: 'then' } }, types)).toBe("The slot 'then' already contains an activity.");
    const same = moveTo(workflow, pathOf(workflow, 'a'), { parentPath: [], position: { index: 1 } }, types);
    expect(same.document).toBe(workflow);
  });
});

describe('Where inserts and pastes go', () => {
  it('as the WPF Studio: into a selected list, else a selected container’s first empty slot, else after the selection', () => {
    expect(selectionTarget(workflow, [], types)).toEqual({ parentPath: [], position: { index: 4 } });
    expect(selectionTarget(workflow, pathOf(workflow, 'check'), types)).toEqual({ parentPath: pathOf(workflow, 'check'), position: { slot: 'else' } });
    expect(selectionTarget(workflow, pathOf(workflow, 'a'), types)).toEqual({ parentPath: [], position: { index: 1 } });
    expect(selectionTarget(workflow, pathOf(workflow, 'pick'), types)).toEqual({ parentPath: pathOf(workflow, 'pick'), position: { slot: 'default' } });
    expect(selectionTarget(workflow, pathOf(workflow, 'one'), types)).toBe("The selected activity fills the slot 'case:1'. Select a list, a container with an empty slot, or an empty slot, to insert.");
  });
});

describe('Clipboard', () => {
  it('round-trips activities as readable text in the WPF format, and rejects anything else', () => {
    const text = serializeNodes([nodeAt(workflow, pathOf(workflow, 'check'))]);
    expect(JSON.parse(text)).toMatchObject({ myrpaNodes: '1.0', nodes: [{ id: 'check', slots: { then: { id: 'inner' } } }] });
    expect(parseNodes(text)).toHaveLength(1);
    expect(parseNodes('hello')).toBeUndefined();
    expect(parseNodes('{"nodes":[{"id":"x"}]}')).toBeUndefined();
    expect(parseNodes('{"myrpaNodes":"1.0","nodes":[]}')).toBeUndefined();
  });

  it('renames pasted ids that are already used, descendants included, and keeps new ones', () => {
    const pasted = prepareForPaste([nodeAt(workflow, pathOf(workflow, 'check')), { id: 'fresh', type: 'Core.Log' }, { id: 'fresh', type: 'Core.Log' }], workflow);
    expect(pasted[0]).toMatchObject({ id: 'if-1', slots: { then: { id: 'sequence-1', children: [{ id: 'log-1' }] } } });
    expect(pasted.slice(1).map((n) => n.id)).toEqual(['fresh', 'log-2']);
  });
});
