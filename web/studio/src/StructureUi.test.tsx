// W4A: the structural editing controls in the UI.

import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { Studio } from './studio';
import { FakeApi, FakeEventSource, settle } from './test-support';

async function renderStudio(api = new FakeApi()) {
  const studio = new Studio(api, (url) => new FakeEventSource(url));
  render(<App studio={studio} />);
  await act(settle);
  fireEvent.change(screen.getByRole('combobox', { name: 'Workflow' }), { target: { value: 'hello-world.json' } });
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name: 'Open' }));
    await settle();
  });
  return { studio, api };
}

const treeItem = (nodeId: string) => document.querySelector<HTMLElement>(`[role="treeitem"][data-node-id="${nodeId}"]`)!;
const treeIds = () => within(screen.getByRole('tree')).getAllByRole('treeitem').map((item) => item.dataset.nodeId);
const editButton = (name: string) => within(screen.getByRole('toolbar', { name: 'Edit' })).getByRole('button', { name }) as HTMLButtonElement;
const selectedId = () => document.querySelector<HTMLElement>('[role="treeitem"][aria-selected="true"]')?.dataset.nodeId;

beforeEach(() => {
  FakeEventSource.instances = [];
});

afterEach(cleanup);

describe('structural editing UI', () => {
  it('enables only the commands that apply to the selection', async () => {
    await renderStudio();

    expect(editButton('Undo').disabled).toBe(true);
    expect(editButton('Redo').disabled).toBe(true);
    expect(editButton('Delete').disabled).toBe(true);
    expect(editButton('Delete').title).toMatch(/root activity cannot be deleted/);
    expect(editButton('Move up').disabled).toBe(true);

    fireEvent.click(treeItem('log-greeting'));

    expect(editButton('Delete').disabled).toBe(false);
    expect(editButton('Move up').disabled).toBe(false);
    expect(editButton('Move down').disabled).toBe(true);
    expect(editButton('Move down').title).toMatch(/already last/);
  });

  it('inserts a catalog activity after the selection, selects it, and edits its property', async () => {
    await renderStudio();
    fireEvent.click(treeItem('build-greeting'));

    fireEvent.click(screen.getByRole('button', { name: 'Insert Log (Core.Log)' }));

    expect(treeIds()).toEqual(['main', 'build-greeting', 'log-1', 'log-greeting']);
    expect(selectedId()).toBe('log-1');
    expect(screen.getByTestId('node-type').textContent).toBe('Core.Log');
    fireEvent.change(screen.getByLabelText(/^message/), { target: { value: "'new'" } });
    expect((screen.getByLabelText(/^message/) as HTMLInputElement).value).toBe("'new'");
    expect(screen.getByTestId('document-title').textContent).toBe('hello-world.json •');
    expect(editButton('Undo').title).toBe('Undo: Edit message (Ctrl+Z)');
  });

  it('disables insertion where there is no list, with the reason', async () => {
    const api = new FakeApi();
    api.files.set('if.json', { text: JSON.stringify({ schemaVersion: '1.0', root: { id: 'if', type: 'Core.If', slots: { then: { id: 't', type: 'Core.Log' } } } }), etag: 1 });
    const studio = new Studio(api, (url) => new FakeEventSource(url));
    render(<App studio={studio} />);
    await act(async () => {
      await settle();
      await studio.open('if.json');
    });

    fireEvent.click(treeItem('t'));

    expect((screen.getByRole('button', { name: 'Insert Log (Core.Log)' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText(/fills the slot 'then'/)).toBeTruthy();
  });

  it('deletes with the button or the Delete key and keeps focus in the tree', async () => {
    await renderStudio();
    fireEvent.click(treeItem('build-greeting'));

    fireEvent.click(editButton('Delete'));
    expect(treeIds()).toEqual(['main', 'log-greeting']);
    expect(selectedId()).toBe('log-greeting');

    treeItem('log-greeting').focus();
    fireEvent.keyDown(treeItem('log-greeting'), { key: 'Delete' });
    expect(treeIds()).toEqual(['main']);
    expect(selectedId()).toBe('main');
    expect(document.activeElement).toBe(treeItem('main'));
  });

  it('moves with the buttons and Alt+Up/Down', async () => {
    await renderStudio();
    fireEvent.click(treeItem('log-greeting'));

    fireEvent.click(editButton('Move up'));
    expect(treeIds()).toEqual(['main', 'log-greeting', 'build-greeting']);

    treeItem('log-greeting').focus();
    fireEvent.keyDown(treeItem('log-greeting'), { key: 'ArrowDown', altKey: true });
    expect(treeIds()).toEqual(['main', 'build-greeting', 'log-greeting']);
    expect(selectedId()).toBe('log-greeting');
    expect(document.activeElement).toBe(treeItem('log-greeting'));
  });

  it('undoes and redoes with the buttons and Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z', async () => {
    await renderStudio();
    fireEvent.click(treeItem('log-greeting'));
    fireEvent.click(editButton('Delete'));
    expect(editButton('Undo').disabled).toBe(false);

    fireEvent.click(editButton('Undo'));
    expect(treeIds()).toEqual(['main', 'build-greeting', 'log-greeting']);
    expect(screen.getByTestId('document-title').textContent).toBe('hello-world.json');
    expect(editButton('Redo').disabled).toBe(false);

    fireEvent.keyDown(window, { key: 'y', ctrlKey: true });
    expect(treeIds()).toEqual(['main', 'build-greeting']);
    fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
    expect(treeIds()).toEqual(['main', 'build-greeting', 'log-greeting']);
    fireEvent.keyDown(window, { key: 'Z', ctrlKey: true, shiftKey: true });
    expect(treeIds()).toEqual(['main', 'build-greeting']);
    expect(screen.getByRole('status').textContent).toBe('Redid: Delete log-greeting.');
  });

  it('saves the structural edits through the server and clears the dirty marker', async () => {
    const { api } = await renderStudio();
    fireEvent.click(treeItem('build-greeting'));
    fireEvent.click(screen.getByRole('button', { name: 'Insert Assign (Core.Assign)' }));

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await settle();
    });

    expect(JSON.parse(api.saves[0].text).root.children.map((c: { id: string }) => c.id)).toEqual(['build-greeting', 'assign-1', 'log-greeting']);
    expect(screen.getByTestId('document-title').textContent).toBe('hello-world.json');
  });
});
