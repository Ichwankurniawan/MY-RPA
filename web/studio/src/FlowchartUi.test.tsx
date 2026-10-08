import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { nodeAt } from './document';
import { Studio } from './studio';
import { catalog, FakeApi, FakeEventSource, immediately, settle } from './test-support';
import type { ExecutionEvent, JsonObject } from './types';

// G-2 (graph-workflows-plan.md, ADR-0037): the flowchart canvas, its list view, the Transitions editor, the schema raise,
// opening a step, and the transition last taken in a run.

class GraphApi extends FakeApi {
  override async activities() {
    return [
      ...catalog,
      {
        type: 'Core.Flowchart',
        displayName: 'Flowchart',
        category: 'Control Flow',
        allowsChildren: true,
        childLayout: 'Graph' as const,
        properties: [{ name: 'maxSteps', kind: 'Expression' as const, required: false, allowedValues: [], scopeSlots: [] }],
        slots: [],
      },
      { type: 'Core.Decision', displayName: 'Decision', category: 'Control Flow', allowsChildren: false, properties: [], slots: [] },
    ];
  }
}

const log = (id: string, extra: object = {}) => ({ id, type: 'Core.Log', properties: { message: `'${id}'` }, ...extra });

const flowJson = JSON.stringify({
  schemaVersion: '1.1',
  id: 'flow',
  name: 'Flow',
  version: '1.0.0',
  root: {
    id: 'retry',
    type: 'Core.Flowchart',
    children: [
      log('attempt', { layout: { x: 40, y: 40 }, transitions: [{ to: 'check' }] }),
      { id: 'check', type: 'Core.Decision', layout: { x: 40, y: 200 }, transitions: [{ to: 'done', when: 'true', label: 'yes' }, { to: 'block' }] },
      { id: 'block', type: 'Core.Sequence', layout: { x: 340, y: 200 }, children: [log('inner')], transitions: [{ to: 'attempt' }] },
      log('done', { layout: { x: 40, y: 360 } }),
    ],
  },
});

const treeJson = JSON.stringify({ schemaVersion: '1.0', id: 'tree', name: 'Tree', version: '1.0.0', root: { id: 'main', type: 'Core.Sequence', children: [log('a')] } });

async function renderStudio(path = 'flow.json') {
  const api = new GraphApi();
  api.files.set('flow.json', { text: flowJson, etag: 1 });
  api.files.set('tree.json', { text: treeJson, etag: 1 });
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { validateDelayMs: undefined });
  render(<App studio={studio} />);
  await act(settle);
  await act(async () => {
    await studio.open(path);
  });
  return { studio, api, state: () => studio.store.get() };
}

const properties = () => within(screen.getByRole('complementary', { name: 'Properties' }));
const item = (id: string) => document.querySelector<HTMLElement>(`[role="treeitem"][data-node-id="${id}"]`)!;
const card = (id: string) => item(id).querySelector<HTMLElement>(':scope > .node')!;
const arrows = () => [...document.querySelectorAll<SVGGElement>('.flow-arrows g.arrow')].map((g) => g.dataset.arrow);
const step = (state: () => ReturnType<Studio['store']['get']>, i: number): JsonObject => nodeAt(state().document!, [{ children: i }]);
const select = (id: string) => act(async () => fireEvent.click(item(id)));

/** A pointer gesture on the canvas (jsdom has no layout: positions are the client coordinates at zoom 1). */
async function gesture(from: Element, to: { x: number; y: number }, start = { x: 100, y: 100 }) {
  const surface = document.querySelector<HTMLElement>('.flow-surface')!;
  await act(async () => {
    fireEvent(from, new MouseEvent('pointerdown', { bubbles: true, button: 0, clientX: start.x, clientY: start.y }));
    fireEvent(surface, new MouseEvent('pointermove', { bubbles: true, clientX: to.x, clientY: to.y }));
    fireEvent(surface, new MouseEvent('pointerup', { bubbles: true, clientX: to.x, clientY: to.y }));
  });
}

beforeEach(() => {
  FakeEventSource.instances = [];
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('Flowchart canvas', () => {
  it('shows the steps as positioned tree items with the start step marked, and the transitions as labelled arrows', async () => {
    await renderStudio();

    expect([...document.querySelectorAll('.flow-step')].map((e) => (e as HTMLElement).dataset.nodeId)).toEqual(['attempt', 'check', 'block', 'done']);
    expect(item('attempt').getAttribute('role')).toBe('treeitem');
    expect(item('attempt').style.left).toBe('40px');
    expect(item('check').style.top).toBe('200px');
    expect(within(item('attempt')).getByText('start')).toBeTruthy();
    expect(arrows()).toHaveLength(4);
    expect([...document.querySelectorAll('.arrow-label')].map((t) => t.textContent)).toEqual(['yes']);
    // The contents of a step are not shown on the canvas: Open shows them.
    expect(item('inner')).toBeNull();
    expect(screen.getByRole('button', { name: 'Open Sequence' })).toBeTruthy();
  });

  it('edits transitions in Properties: condition, label, target, order, remove and add, each one undo step', async () => {
    const { studio, state } = await renderStudio();
    await select('check');
    const first = () => within(properties().getByRole('group', { name: /^Transition 1/ }));

    fireEvent.change(first().getByLabelText('Condition'), { target: { value: 'attempts >= 3' } });
    fireEvent.change(first().getByLabelText('Label'), { target: { value: '' } });
    expect(step(state, 1).transitions).toEqual([{ to: 'done', when: 'attempts >= 3' }, { to: 'block' }]);

    fireEvent.click(properties().getByRole('button', { name: 'Check transition 1 later' }));
    expect((step(state, 1).transitions as JsonObject[]).map((t) => t.to)).toEqual(['block', 'done']);

    fireEvent.change(first().getByLabelText('Go to'), { target: { value: 'attempt' } });
    fireEvent.click(properties().getByRole('button', { name: 'Remove transition 2' }));
    expect(step(state, 1).transitions).toEqual([{ to: 'attempt' }]);

    fireEvent.change(properties().getByLabelText('Add a transition to'), { target: { value: 'done' } });
    fireEvent.click(properties().getByRole('button', { name: 'Add transition' }));
    expect(step(state, 1).transitions).toEqual([{ to: 'attempt' }, { to: 'done' }]);
    expect(arrows()).toHaveLength(4);

    // A second arrow to the same step without a condition is refused, with the reason.
    fireEvent.change(properties().getByLabelText('Add a transition to'), { target: { value: 'done' } });
    expect((properties().getByRole('button', { name: 'Add transition' }) as HTMLButtonElement).title).toBe('check already goes to done without a condition.');

    // Six edits, six undo steps.
    expect(state().undo).toHaveLength(6);
    for (let i = 0; i < 6; i++) {
      act(() => studio.undo());
    }

    expect(step(state, 1).transitions).toEqual([{ to: 'done', when: 'true', label: 'yes' }, { to: 'block' }]);
  });

  it('makes a step the start step', async () => {
    const { state } = await renderStudio();
    await select('done');

    fireEvent.click(properties().getByRole('button', { name: 'Set as start step' }));

    expect(step(state, 0).id).toBe('done');
    expect(within(item('done')).getByText('start')).toBeTruthy();
  });

  it('deleting a step also removes the transitions that went to it', async () => {
    const { studio, state } = await renderStudio();
    await select('block');

    act(() => studio.deleteSelected());

    expect(step(state, 1).transitions).toEqual([{ to: 'done', when: 'true', label: 'yes' }]);
    expect(arrows()).toHaveLength(2);
  });

  it('moves a step by dragging its card, and connects two steps by dragging from the handle', async () => {
    const { state } = await renderStudio();

    await gesture(card('done'), { x: 300, y: 260 });
    expect(step(state, 3).layout).toEqual({ x: 240, y: 520 });

    if (!('elementsFromPoint' in document)) {
      Object.defineProperty(document, 'elementsFromPoint', { value: () => [], configurable: true, writable: true });
    }

    vi.spyOn(document, 'elementsFromPoint').mockReturnValue([card('attempt')]);
    await gesture(item('done').querySelector('.connect-handle')!, { x: 60, y: 60 });
    expect(step(state, 3).transitions).toEqual([{ to: 'attempt' }]);
    expect(arrows()).toHaveLength(5);
  });

  it('clicking an arrow selects its step and focuses that transition in Properties', async () => {
    const { state } = await renderStudio();
    const hit = document.querySelector<SVGPathElement>('[data-arrow$=":1"] .arrow-hit')!;

    await act(async () => fireEvent.click(hit));

    expect(state().selectedKey).toBe(item('check').dataset.key);
    expect(document.activeElement).toBe(within(properties().getByRole('group', { name: /^Transition 2/ })).getByLabelText('Go to'));
  });

  it('a list view shows the steps as cards with their transitions, reachable by keyboard', async () => {
    const { state } = await renderStudio();
    fireEvent.click(screen.getByRole('button', { name: 'List view' }));

    expect(document.querySelector('.flow-canvas')).toBeNull();
    expect(item('check').querySelector('.transitions-summary')!.textContent).toBe('→ done when true→ block');
    expect(item('inner')).toBeTruthy();

    await select('attempt');
    fireEvent.keyDown(item('attempt'), { key: 'ArrowDown' });
    expect(state().selectedKey).toBe(item('check').dataset.key);
  });

  it('opens a container step in the designer, and the whole workflow comes back', async () => {
    const { studio, state } = await renderStudio();
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Open Sequence' })));

    expect(screen.getByText(/a flowchart step/)).toBeTruthy();
    expect(item('inner')).toBeTruthy();
    expect(item('attempt')).toBeNull();

    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Whole workflow' })));
    expect(item('attempt')).toBeTruthy();

    // Selecting a node inside a step (for example from Problems) opens the step.
    act(() => studio.selectNodeId('inner'));
    expect(state().designerScope).toBe(item('block').dataset.key);
  });

  it('highlights the transition last taken while a run of this file is shown', async () => {
    await renderStudio();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Run' }));
      await settle();
    });
    const event = (sequence: number, nodeId: string): ExecutionEvent => ({ runId: 'run-1', time: '2026-10-08T10:00:00Z', executionId: 'e1', sequence, kind: 'node.started', nodeId });
    await act(async () => {
      [event(1, 'retry'), event(2, 'attempt'), event(3, 'check'), event(4, 'block')].forEach((e) => FakeEventSource.instances.at(-1)!.emit(e));
      await settle();
    });

    expect([...document.querySelectorAll<SVGGElement>('g.arrow.taken')].map((g) => g.dataset.arrow)).toEqual([`${item('check').dataset.key}:1`]);
  });

  it('inserting a Flowchart into a 1.0 workflow raises it to 1.1 in the same undo step', async () => {
    const { studio, state } = await renderStudio('tree.json');
    await select('main');

    fireEvent.click(screen.getByRole('button', { name: 'Insert Flowchart (Core.Flowchart)' }));
    expect(state().document!.schemaVersion).toBe('1.1');
    expect(screen.getByRole('button', { name: 'Empty flowchart: insert the start step here' })).toBeTruthy();

    act(() => studio.undo());
    expect(state().document!.schemaVersion).toBe('1.0');
  });
});
