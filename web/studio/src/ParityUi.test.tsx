import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { memoryDrafts } from './drafts';
import { indexDocument, serialize } from './document';
import { Studio } from './studio';
import { catalog, FakeApi, FakeEventSource, immediately, settle } from './test-support';
import type { ActivityDescriptor, JsonObject } from './types';

/** The Properties panel (UX-3: the selected card has the same editors inline). */
const properties = () => within(screen.getByRole('complementary', { name: 'Properties' }));


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
      { id: 'check', type: 'Core.If', properties: { condition: 'true' } },
      { id: 'pick', type: 'Core.Switch', properties: { expression: 'x' } },
      { id: 'b', type: 'Core.Log', properties: { message: "'b'" } },
    ],
  },
};

async function renderStudio() {
  const api = new FakeApi();
  catalog.push(switchActivity);
  api.files.set('w.json', { text: serialize(workflow), etag: 1 });
  api.files.set('other.json', { text: serialize({ ...workflow, id: 'other' }), etag: 1 });
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { drafts: memoryDrafts(), validateDelayMs: undefined });
  render(<App studio={studio} />);
  await act(settle);
  await act(async () => {
    await studio.open('w.json');
  });
  return { studio, api, state: () => studio.store.get() };
}

const item = (id: string) => document.querySelector<HTMLElement>(`[role="treeitem"][data-node-id="${id}"]`)!;
const card = (id: string) => item(id).querySelector<HTMLElement>(':scope > .node')!;
const ids = (document: JsonObject | undefined) => indexDocument(document!).entries.map((e) => e.node.id).join(',');
const insert = (name: string) => fireEvent.click(screen.getByRole('button', { name }));

/** A clipboard event as the browser fires it on Ctrl+C / X / V (jsdom has no ClipboardEvent constructor). */
function clipboard(type: 'copy' | 'cut' | 'paste', text = ''): { event: Event; text: () => string } {
  let data = text;
  const event = new Event(type, { bubbles: true, cancelable: true });
  Object.defineProperty(event, 'clipboardData', { value: { setData: (_: string, value: string) => (data = value), getData: () => data } });
  return { event, text: () => data };
}

/** A pointer drag from one element to another (jsdom has no layout: elementsFromPoint is pointed at `over`). */
function drag(from: HTMLElement, over: HTMLElement, y = 10) {
  if (!('elementsFromPoint' in document)) {
    Object.defineProperty(document, 'elementsFromPoint', { value: () => [], configurable: true, writable: true });
  }

  const spy = vi.spyOn(document, 'elementsFromPoint').mockReturnValue([over]);
  fireEvent(from, new MouseEvent('pointerdown', { bubbles: true, button: 0, clientX: 0, clientY: 0 }));
  fireEvent(document, new MouseEvent('pointermove', { bubbles: true, clientX: 0, clientY: y }));
  fireEvent(document, new MouseEvent('pointerup', { bubbles: true, clientX: 0, clientY: y }));
  spy.mockRestore();
}

beforeEach(() => {
  FakeEventSource.instances = [];
});

afterEach(() => {
  cleanup();
  catalog.splice(catalog.indexOf(switchActivity), 1);
});

describe('Inserting into slots', () => {
  it('inserts into a selected container’s first empty slot, and into a picked empty slot', async () => {
    const { state } = await renderStudio();

    fireEvent.click(item('check'));
    insert('Insert Log (Core.Log)');
    expect(indexDocument(state().document!).byKey.get(state().selectedKey!)!.path.at(-1)).toEqual({ slot: 'then' });

    fireEvent.click(within(card('check')).getByRole('button', { name: 'else: empty' }));
    expect(within(card('check')).getByRole('button', { name: 'else: empty', pressed: true })).toBeTruthy();
    insert('Insert Assign (Core.Assign)');
    expect(ids(state().document)).toBe('main,a,check,log-1,assign-1,pick,b');
    expect(state().insertTarget).toBeUndefined();
  });

  it('adds a named case to a Switch through its case zone', async () => {
    const { state } = await renderStudio();
    const zone = within(card('pick'));

    expect(zone.getByRole('button', { name: 'Pick' })).toHaveProperty('disabled', true);
    fireEvent.change(zone.getByLabelText('New case value'), { target: { value: 'gold' } });
    fireEvent.click(zone.getByRole('button', { name: 'Pick' }));
    insert('Insert Log (Core.Log)');

    const pick = indexDocument(state().document!).byKey.get(indexDocument(state().document!).byNodeId.get('pick')!)!.node;
    expect(pick.slots).toEqual({ 'case:gold': { id: 'log-1', type: 'Core.Log' } });
  });
});

describe('Cut, copy and paste', () => {
  it('copies with Ctrl+C and pastes with Ctrl+V after the selection, renaming the copy’s id', async () => {
    const { state } = await renderStudio();
    fireEvent.click(item('a'));

    const copy = clipboard('copy');
    window.dispatchEvent(copy.event);
    expect(copy.event.defaultPrevented).toBe(true);
    expect(JSON.parse(copy.text())).toMatchObject({ myrpaNodes: '1.0', nodes: [{ id: 'a', type: 'Core.Log' }] });

    window.dispatchEvent(clipboard('paste', copy.text()).event);
    expect(ids(state().document)).toBe('main,a,log-1,check,pick,b');
    expect(state().undo.at(-1)?.label).toBe('Paste log-1');
  });

  it('cuts and pastes into a slot (a move across containers by keyboard), as one undo step each', async () => {
    const { state } = await renderStudio();
    fireEvent.click(item('b'));

    const cut = clipboard('cut');
    window.dispatchEvent(cut.event);
    expect(ids(state().document)).toBe('main,a,check,pick');
    fireEvent.click(item('check'));
    window.dispatchEvent(clipboard('paste', cut.text()).event);

    expect(ids(state().document)).toBe('main,a,check,b,pick');
    expect(state().undo.map((u) => u.label).slice(-2)).toEqual(['Delete b', 'Paste b']);
  });

  it('pastes across documents, and leaves text fields their own clipboard', async () => {
    const { studio, state } = await renderStudio();
    fireEvent.click(item('check'));
    fireEvent.click(screen.getByRole('button', { name: 'Copy' }));
    await act(async () => {
      await studio.open('other.json');
    });
    fireEvent.click(item('b'));
    fireEvent.click(screen.getByRole('button', { name: 'Paste' }));
    expect(ids(state().document)).toBe('main,a,check,pick,b,if-1');

    fireEvent.click(item('b'));
    const input = properties().getByLabelText(/^message/);
    input.focus();
    const native = clipboard('copy');
    window.dispatchEvent(native.event);
    expect(native.event.defaultPrevented).toBe(false);
  });

  it('says what is wrong with a paste that cannot happen', async () => {
    const { state } = await renderStudio();
    fireEvent.click(item('a'));
    window.dispatchEvent(clipboard('paste', 'just text').event);
    expect(state().message).toBe('The clipboard does not hold MyRPA activities.');
  });
});

describe('Drag-and-drop', () => {
  it('moves a card to the gap after another card, and into an empty slot', async () => {
    const { state } = await renderStudio();

    drag(card('a'), card('b'));
    expect(ids(state().document)).toBe('main,check,pick,b,a');
    expect(state().undo.at(-1)?.label).toBe('Move a');

    drag(card('a'), within(card('check')).getByRole('button', { name: 'then: empty (required)' }));
    expect(ids(state().document)).toBe('main,check,a,pick,b');
  });

  it('drops a toolbox activity into a zone, and refuses a drop into the dragged node itself', async () => {
    const { state } = await renderStudio();

    drag(screen.getByRole('button', { name: 'Insert Log (Core.Log)' }), within(card('check')).getByRole('button', { name: 'else: empty' }));
    expect(ids(state().document)).toBe('main,a,check,log-1,pick,b');

    const before = state().document;
    fireEvent.click(item('check'));
    drag(card('check'), within(card('check')).getByRole('button', { name: 'then: empty (required)' }));
    expect(state().document).toBe(before);
    expect(state().message).toBe('Not dropped: An activity cannot be moved into itself.');
  });

  it('a press without movement stays a click (selection), not a drag', async () => {
    const { state } = await renderStudio();
    fireEvent(card('b'), new MouseEvent('pointerdown', { bubbles: true, button: 0, clientX: 0, clientY: 0 }));
    fireEvent(document, new MouseEvent('pointerup', { bubbles: true, clientX: 1, clientY: 1 }));
    fireEvent.click(card('b'));
    expect(indexDocument(state().document!).byKey.get(state().selectedKey!)!.node.id).toBe('b');
    expect(state().undo).toHaveLength(0);
  });
});
