import { describe, expect, it } from 'vitest';
import { indexDocument, keyOf, nodeAt, type Step } from './document';
import {
  addTransition,
  addTransitionRefusal,
  arrowShape,
  asListNodes,
  asPastedSteps,
  canvasPositions,
  detachStep,
  incomingCount,
  lastTaken,
  moveTransition,
  moveTransitionRefusal,
  removeTransition,
  setLayout,
  setStart,
  setStartRefusal,
  stepSize,
  updateTransition,
  withGraphSchema,
} from './graph';
import { prepareForPaste } from './placement';
import { catalog } from './test-support';
import type { ActivityDescriptor, ExecutionEvent, JsonObject } from './types';

// G-2 (graph-workflows-plan.md): the pure flowchart edits behind the canvas, the list view and the Transitions editor.

const graphCatalog = new Map<string, ActivityDescriptor>(
  [...catalog, { type: 'Core.Flowchart', displayName: 'Flowchart', category: 'Control Flow', allowsChildren: true, childLayout: 'Graph' as const, properties: [], slots: [] }].map((a) => [a.type, a]),
);

const log = (id: string, extra: JsonObject = {}): JsonObject => ({ id, type: 'Core.Log', properties: { message: `'${id}'` }, ...extra });

const flowchart = (): JsonObject => ({
  schemaVersion: '1.1',
  id: 'w',
  name: 'W',
  version: '1.0.0',
  root: {
    id: 'flow',
    type: 'Core.Flowchart',
    children: [
      log('a', { layout: { x: 40, y: 40, note: 'kept' }, transitions: [{ to: 'b', when: 'n > 1', label: 'more', 'x-note': 1 }, { to: 'c' }] }),
      log('b', { transitions: [{ to: 'c' }] }),
      log('c', { transitions: [{ to: 'a', when: 'n < 3' }] }),
    ],
  },
});

const step = (i: number): Step[] => [{ children: i }];
const transitions = (document: JsonObject, i: number) => nodeAt(document, step(i)).transitions;

describe('transitions', () => {
  it('adds a transition to a sibling, refusing other nodes, unknown targets and a second unconditional arrow', () => {
    const document = flowchart();
    expect(addTransitionRefusal(document, step(1), 'a', graphCatalog)).toBeUndefined();
    expect(addTransitionRefusal(document, step(1), 'flow', graphCatalog)).toBe("'flow' is not a step of the same flowchart.");
    expect(addTransitionRefusal(document, step(1), 'c', graphCatalog)).toBe('b already goes to c without a condition.');
    expect(addTransitionRefusal(document, [], 'a', graphCatalog)).toBe('Only a step of a flowchart can have transitions.');

    const added = addTransition(document, step(1), 'a');
    expect(transitions(added, 1)).toEqual([{ to: 'c' }, { to: 'a' }]);
    expect(keyOf(nodeAt(added, step(1)))).toBe(keyOf(nodeAt(document, step(1))));
  });

  it('edits target, condition and label; an empty condition or label is removed; unknown fields stay', () => {
    const document = flowchart();
    const edited = updateTransition(document, step(0), 0, { when: '', label: 'go', to: 'c' });
    expect(transitions(edited, 0)).toEqual([{ to: 'c', label: 'go', 'x-note': 1 }, { to: 'c' }]);
    expect(transitions(updateTransition(document, step(0), 1, { to: '' }), 0)).toEqual([{ to: 'b', when: 'n > 1', label: 'more', 'x-note': 1 }, { to: '' }]);
  });

  it('removes transitions (the field goes with the last one) and reorders them', () => {
    const document = flowchart();
    expect(transitions(removeTransition(document, step(0), 0), 0)).toEqual([{ to: 'c' }]);
    expect('transitions' in nodeAt(removeTransition(document, step(1), 0), step(1))).toBe(false);

    expect((transitions(moveTransition(document, step(0), 0, 1), 0) as JsonObject[]).map((t) => t.to)).toEqual(['c', 'b']);
    expect(moveTransitionRefusal(nodeAt(document, step(0)), 0, -1)).toBe('This transition is already checked first.');
    expect(moveTransitionRefusal(nodeAt(document, step(0)), 1, 1)).toBe('This transition is already checked last.');
  });
});

describe('steps', () => {
  it('places a step at whole pixels, never below 0, keeping other layout fields', () => {
    const document = flowchart();
    expect(nodeAt(setLayout(document, step(0), 100.6, -5), step(0)).layout).toEqual({ x: 101, y: 0, note: 'kept' });
    expect(nodeAt(setLayout(document, step(1), 7, 8), step(1)).layout).toEqual({ x: 7, y: 8 });
  });

  it('makes a step the start step (first child), the others keeping their order', () => {
    const document = flowchart();
    expect(setStartRefusal(document, step(0), graphCatalog)).toBe('This is already the start step.');
    expect(setStartRefusal(document, step(2), graphCatalog)).toBeUndefined();

    const { document: started, path } = setStart(document, step(2));
    expect(path).toEqual(step(0));
    expect((nodeAt(started, []).children as JsonObject[]).map((c) => c.id)).toEqual(['c', 'a', 'b']);
  });

  it('detaches a step: the transitions to it and its own go, keys stay', () => {
    const document = flowchart();
    expect(incomingCount(document, step(2))).toBe(2);
    const keyOfA = keyOf(nodeAt(document, step(0)));

    const detached = detachStep(document, step(2));
    expect(transitions(detached, 0)).toEqual([{ to: 'b', when: 'n > 1', label: 'more', 'x-note': 1 }]);
    expect('transitions' in nodeAt(detached, step(1))).toBe(false);
    expect('transitions' in nodeAt(detached, step(2))).toBe(false);
    expect(keyOf(nodeAt(detached, step(0)))).toBe(keyOfA);
  });
});

describe('schema version', () => {
  it('raises 1.0 to 1.1 when the workflow uses a flowchart, transitions or a position; never otherwise', () => {
    const tree: JsonObject = { schemaVersion: '1.0', id: 'w', name: 'W', version: '1', root: { id: 'main', type: 'Core.Sequence', children: [log('a')] } };
    expect(withGraphSchema(tree, graphCatalog)).toBe(tree);
    expect(withGraphSchema({ ...tree, root: { id: 'f', type: 'Core.Flowchart' } }, graphCatalog).schemaVersion).toBe('1.1');
    expect(withGraphSchema({ ...tree, root: { ...(tree.root as JsonObject), layout: { x: 1, y: 2 } } }, graphCatalog).schemaVersion).toBe('1.1');
    const raised = withGraphSchema({ ...tree, root: { id: 'f', type: 'Core.Flowchart' } }, graphCatalog);
    expect(Object.keys(raised)[0]).toBe('schemaVersion');
    const current = flowchart();
    expect(withGraphSchema(current, graphCatalog)).toBe(current);
  });
});

describe('paste', () => {
  it('renames pasted steps and points their transitions at the copies; arrows to steps not copied are dropped', () => {
    const document = flowchart();
    const [a, b] = nodeAt(document, []).children as JsonObject[];
    const pasted = prepareForPaste([a, b], document);

    expect(pasted.map((n) => n.id)).toEqual(['log-1', 'log-2']);
    expect(pasted[0].transitions).toEqual([{ to: 'log-2', when: 'n > 1', label: 'more', 'x-note': 1 }]);
    expect('transitions' in pasted[1]).toBe(false);
    expect(asPastedSteps(pasted)[0].layout).toEqual({ x: 64, y: 64, note: 'kept' });
    expect(asListNodes(pasted).every((n) => !('transitions' in n) && !('layout' in n))).toBe(true);
  });
});

describe('canvas', () => {
  it('uses each step position, and places the others in rows by their distance from the start step', () => {
    const document: JsonObject = {
      root: {
        id: 'f',
        type: 'Core.Flowchart',
        children: [log('s', { transitions: [{ to: 'x' }, { to: 'y' }] }), log('x', { layout: { x: 500, y: 9 } }), log('y'), log('lost')],
      },
    };
    const graph = nodeAt(document, []);
    const steps = graph.children as JsonObject[];
    const positions = canvasPositions(graph);

    expect(positions.get(keyOf(steps[0]))).toEqual({ x: 40, y: 40 });
    expect(positions.get(keyOf(steps[1]))).toEqual({ x: 500, y: 9 });
    expect(positions.get(keyOf(steps[2]))).toEqual({ x: 40, y: 190 });
    expect(positions.get(keyOf(steps[3]))).toEqual({ x: 40, y: 340 });
    expect(canvasPositions(graph)).toBe(positions);
  });

  it('draws arrows down, back up around the side, sideways and as a loop', () => {
    const { width: w, height: h } = stepSize;
    expect(arrowShape({ x: 0, y: 0 }, { x: 0, y: 200 }, false).d).toBe(`M${w / 2} ${h} C${w / 2} ${h + (200 - h) / 2} ${w / 2} ${200 - (200 - h) / 2} ${w / 2} 200`);
    expect(arrowShape({ x: 0, y: 200 }, { x: 0, y: 0 }, false).d).toMatch(new RegExp(`^M${w} ${200 + h / 2} C${w + 80} `));
    expect(arrowShape({ x: 0, y: 0 }, { x: 400, y: 0 }, false).d).toMatch(new RegExp(`^M${w} ${h / 2} `));
    expect(arrowShape({ x: 0, y: 0 }, { x: 0, y: 0 }, true).d).toMatch(new RegExp(`^M${w} ${h / 2 - 14} C${w + 70} `));
  });

  it('finds the transition last taken from the run events of the workflow itself', () => {
    const started = (sequence: number, nodeId: string, parentExecutionId?: string): ExecutionEvent => ({ sequence, kind: 'node.started', runId: 'r', time: 't', nodeId, parentExecutionId });
    const ids = new Set(['a', 'b', 'c']);
    expect(lastTaken([started(1, 'flow'), started(2, 'a')], ids)).toBe('');
    expect(lastTaken([started(1, 'a'), started(2, 'inner'), started(3, 'b'), started(4, 'c', 'child-execution')], ids)).toBe('a\0b');
  });

  it('indexes steps like any other nodes (tree items with keys)', () => {
    const document = flowchart();
    expect(indexDocument(document).entries.map((e) => e.node.id)).toEqual(['flow', 'a', 'b', 'c']);
  });
});
