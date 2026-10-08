import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { memoryDrafts } from './drafts';
import { serialize } from './document';
import { memoryPreferences } from './preferences';
import { Studio, zoomLimits } from './studio';
import { FakeApi, FakeEventSource, immediately, settle } from './test-support';
import type { JsonObject } from './types';

// UX-3 (studio-ux-plan.md): the designer as cards — summaries, the selected card's editors and menu, collapse and
// expand (also by keyboard), zoom.

const workflow: JsonObject = {
  schemaVersion: '1.0',
  id: 'w',
  name: 'Cards',
  version: '1',
  root: {
    id: 'main',
    type: 'Core.Sequence',
    children: [
      { id: 'greet', type: 'Core.Log', properties: { message: "'Hello'", level: 'Warning' } },
      { id: 'check', type: 'Core.If', properties: { condition: 'true' }, slots: { then: { id: 'inside', type: 'Core.Log', properties: { message: "'in'" } } } },
      { id: 'last', type: 'Core.Log', properties: { message: "'bye'" } },
    ],
  },
};

async function renderStudio() {
  const api = new FakeApi();
  api.files.set('cards.json', { text: serialize(workflow), etag: 1 });
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { drafts: memoryDrafts(), preferences: memoryPreferences(), validateDelayMs: undefined });
  render(<App studio={studio} />);
  await act(settle);
  await act(async () => {
    await studio.open('cards.json');
  });
  return { studio, api, state: () => studio.store.get() };
}

const item = (id: string) => document.querySelector<HTMLElement>(`[role="treeitem"][data-node-id="${id}"]`)!;
const card = (id: string) => item(id).querySelector<HTMLElement>(':scope > .node')!;
const visibleIds = () => [...document.querySelectorAll<HTMLElement>('.designer [role="treeitem"][data-node-id]')].map((e) => e.dataset.nodeId);

beforeEach(() => {
  FakeEventSource.instances = [];
});

afterEach(cleanup);

describe('Cards', () => {
  it('shows a one-line summary of each card, and the selected card its own editors and actions', async () => {
    await renderStudio();

    expect(card('greet').querySelector('.summary')?.textContent).toBe("message: 'Hello' · level: Warning");
    expect(within(card('greet')).queryByRole('textbox')).toBeNull();

    fireEvent.click(card('greet'));

    expect(card('greet').querySelector('.summary')).toBeNull();
    expect((within(card('greet')).getByLabelText(/^message/) as HTMLInputElement).value).toBe("'Hello'");
    expect(within(card('greet')).getByRole('button', { name: 'Actions for Log' })).toBeTruthy();
    expect(within(card('last')).queryByRole('button', { name: /^Actions/ })).toBeNull();
  });

  it('edits from the card; the Properties panel shows the same value, and Delete while typing deletes text, not the activity', async () => {
    const { state } = await renderStudio();
    fireEvent.click(card('greet'));
    const field = within(card('greet')).getByLabelText(/^message/) as HTMLInputElement;
    field.focus();

    fireEvent.change(field, { target: { value: "'Hi'" } });
    fireEvent.keyDown(field, { key: 'Delete' });
    fireEvent.keyDown(field, { key: 'ArrowDown' });

    const panel = within(screen.getByRole('complementary', { name: 'Properties' }));
    expect((panel.getByLabelText(/^message/) as HTMLInputElement).value).toBe("'Hi'");
    expect(visibleIds()).toContain('greet');
    expect(state().selectedKey).toBe(item('greet').dataset.key);
  });

  it('names each tree item from its content, which includes its title (WCAG 2.5.3: the visible text)', async () => {
    await renderStudio();

    expect(screen.getByRole('treeitem', { name: /^If Core\.If #check/ })).toBe(item('check'));
  });

  it('runs a card action from its menu with the keyboard, and Escape returns to the button', async () => {
    await renderStudio();
    fireEvent.click(card('last'));
    const button = within(card('last')).getByRole('button', { name: 'Actions for Log' });

    fireEvent.click(button);
    const menu = screen.getByRole('menu', { name: 'Actions for Log' });
    expect(within(menu).getAllByRole('menuitem').map((m) => m.textContent)).toEqual(['Cut', 'Copy', 'Paste', 'Delete', 'Move up', 'Move down']);
    expect(within(menu).getByRole('menuitem', { name: 'Move down' }).getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(within(menu).getByRole('menuitem', { name: 'Cut' }));

    fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
    expect(screen.queryByRole('menu')).toBeNull();
    expect(document.activeElement).toBe(button);

    fireEvent.click(button);
    fireEvent.click(within(screen.getByRole('menu')).getByRole('menuitem', { name: 'Move up' }));
    expect(visibleIds()).toEqual(['main', 'greet', 'last', 'check', 'inside']);
  });
});

describe('Collapse and expand', () => {
  it('collapses a container with its toggle, and the selection is never hidden', async () => {
    const { studio, state } = await renderStudio();

    fireEvent.click(within(card('check')).getByRole('button', { name: 'Collapse If' }));

    expect(visibleIds()).toEqual(['main', 'greet', 'check', 'last']);
    expect(item('check').getAttribute('aria-expanded')).toBe('false');
    expect(card('check').querySelector('.collapsed-note')?.textContent).toBe('1 activity inside (collapsed)');

    act(() => studio.selectNodeId('inside'));
    expect(visibleIds()).toContain('inside');
    expect(state().collapsed.size).toBe(0);
  });

  it('collapses and expands everything below the workflow', async () => {
    await renderStudio();

    fireEvent.click(screen.getByRole('button', { name: 'Collapse all' }));
    expect(visibleIds()).toEqual(['main', 'greet', 'check', 'last']);
    fireEvent.click(screen.getByRole('button', { name: 'Expand all' }));
    expect(visibleIds()).toEqual(['main', 'greet', 'check', 'inside', 'last']);
  });

  it('collapses with ArrowLeft, expands with ArrowRight, and arrow navigation skips hidden activities', async () => {
    const { state } = await renderStudio();
    fireEvent.click(card('check'));
    item('check').focus();

    fireEvent.keyDown(item('check'), { key: 'ArrowLeft' });
    expect(visibleIds()).not.toContain('inside');
    fireEvent.keyDown(item('check'), { key: 'ArrowDown' });
    expect(state().selectedKey).toBe(item('last').dataset.key);

    fireEvent.keyDown(item('last'), { key: 'ArrowUp' });
    fireEvent.keyDown(item('check'), { key: 'ArrowRight' });
    expect(visibleIds()).toContain('inside');
  });
});

describe('Zoom', () => {
  // jsdom ignores the CSS zoom property; the smoke test checks it in Chromium.
  it('zooms with the buttons and the keyboard, within limits', async () => {
    const { state } = await renderStudio();
    const zoom = within(screen.getByRole('toolbar', { name: 'Zoom' }));
    const tree = screen.getByRole('tree', { name: 'Workflow' });

    fireEvent.click(zoom.getByRole('button', { name: 'Zoom in' }));
    expect(state().zoom).toBe(1.1);
    expect(zoom.getByRole('button', { name: '110%, reset zoom' })).toBeTruthy();

    fireEvent.keyDown(tree, { key: '-', ctrlKey: true });
    fireEvent.keyDown(tree, { key: '-', ctrlKey: true });
    expect(state().zoom).toBe(0.9);
    fireEvent.keyDown(tree, { key: '0', ctrlKey: true });
    expect(state().zoom).toBe(1);

    for (let i = 0; i < 30; i++) {
      fireEvent.click(zoom.getByRole('button', { name: 'Zoom in' }));
    }
    expect(state().zoom).toBe(zoomLimits.max);
    expect((zoom.getByRole('button', { name: 'Zoom in' }) as HTMLButtonElement).disabled).toBe(true);
  });
});
