// W4A: structural commands, undo/redo and dirty state through the Studio (fake server).

import { beforeEach, describe, expect, it } from 'vitest';
import { indexDocument, openWorkflow } from './document';
import { deleteRefusalOf, insertRefusal, isDirty, maxUndo, moveRefusalOf, Studio } from './studio';
import { FakeApi, FakeEventSource, catalog } from './test-support';
import type { JsonObject } from './types';

const message = catalog.find((a) => a.type === 'Core.Log')!.properties[0];

async function openHelloWorld(api = new FakeApi()) {
  const studio = new Studio(api, (url) => new FakeEventSource(url));
  await studio.connect();
  await studio.open('hello-world.json');
  return { studio, api, state: () => studio.store.get() };
}

const keyOfId = (studio: Studio, id: string) => indexDocument(studio.store.get().document!).byNodeId.get(id)!;
const ids = (studio: Studio) => indexDocument(studio.store.get().document!).entries.map((e) => e.node.id);
const selectedId = (studio: Studio) => indexDocument(studio.store.get().document!).byKey.get(studio.store.get().selectedKey!)?.node.id;
const propertiesOf = (studio: Studio, id: string) => indexDocument(studio.store.get().document!).entries.find((e) => e.node.id === id)!.node.properties;

beforeEach(() => {
  FakeEventSource.instances = [];
});

describe('structural commands', () => {
  it('insert after the selection, select the new node, mark dirty', async () => {
    const { studio, state } = await openHelloWorld();
    studio.select(keyOfId(studio, 'build-greeting'));

    studio.insertActivity('Core.Log');

    expect(ids(studio)).toEqual(['main', 'build-greeting', 'log-1', 'log-greeting']);
    expect(selectedId(studio)).toBe('log-1');
    expect(isDirty(state())).toBe(true);
    expect(state().undo.at(-1)?.label).toBe('Insert Log');
  });

  it('insert at the end of a selected Sequence', async () => {
    const { studio } = await openHelloWorld();

    studio.insertActivity('Core.Assign');

    expect(ids(studio)).toEqual(['main', 'build-greeting', 'log-greeting', 'assign-1']);
  });

  it('delete only the selected node and select its next sibling', async () => {
    const { studio } = await openHelloWorld();
    studio.select(keyOfId(studio, 'build-greeting'));

    studio.deleteSelected();

    expect(ids(studio)).toEqual(['main', 'log-greeting']);
    expect(selectedId(studio)).toBe('log-greeting');
  });

  it('refuse to delete or move the root, with the reason, and record nothing', async () => {
    const { studio, state } = await openHelloWorld();

    expect(deleteRefusalOf(state())).toMatch(/root/);
    studio.deleteSelected();
    studio.moveSelected(-1);

    expect(state().message).toMatch(/Cannot move: The root activity cannot be moved/);
    expect(ids(studio)).toEqual(['main', 'build-greeting', 'log-greeting']);
    expect(state().undo).toHaveLength(0);
    expect(isDirty(state())).toBe(false);
  });

  it('move the selection up and down within its list; it stays selected', async () => {
    const { studio, state } = await openHelloWorld();
    studio.select(keyOfId(studio, 'log-greeting'));
    expect(moveRefusalOf(state(), 1)).toMatch(/already last/);

    studio.moveSelected(-1);

    expect(ids(studio)).toEqual(['main', 'log-greeting', 'build-greeting']);
    expect(selectedId(studio)).toBe('log-greeting');
    expect(moveRefusalOf(state(), -1)).toMatch(/already first/);
  });

  it('are refused on read-only files', async () => {
    const api = new FakeApi();
    api.files.set('lossy.json', { text: '{ "root": { "id": "main", "type": "Core.Sequence", "children": [ { "id": "d", "type": "Core.Delay", "properties": { "milliseconds": 1.50 } } ] } }', etag: 1 });
    const studio = new Studio(api, (url) => new FakeEventSource(url));
    await studio.connect();
    await studio.open('lossy.json');

    studio.insertActivity('Core.Log');

    expect(insertRefusal(studio.store.get())).toMatch(/read-only/);
    expect(studio.store.get().undo).toHaveLength(0);
  });
});

describe('undo and redo', () => {
  it('undo an insert back to the saved (clean) document; redo restores the same version', async () => {
    const { studio, state } = await openHelloWorld();
    const saved = state().saved;
    studio.insertActivity('Core.Log');
    const inserted = state().document;

    studio.undo();
    expect(state().document).toBe(saved);
    expect(isDirty(state())).toBe(false);
    expect(selectedId(studio)).toBe('main');

    studio.redo();
    expect(state().document).toBe(inserted);
    expect(selectedId(studio)).toBe('log-1');
    expect(isDirty(state())).toBe(true);
  });

  it('undo a delete restores the node and its selection', async () => {
    const { studio } = await openHelloWorld();
    const key = keyOfId(studio, 'build-greeting');
    studio.select(key);
    studio.deleteSelected();

    studio.undo();

    expect(ids(studio)).toEqual(['main', 'build-greeting', 'log-greeting']);
    expect(studio.store.get().selectedKey).toBe(key);
  });

  it('undo a move', async () => {
    const { studio } = await openHelloWorld();
    studio.select(keyOfId(studio, 'log-greeting'));
    studio.moveSelected(-1);

    studio.undo();

    expect(ids(studio)).toEqual(['main', 'build-greeting', 'log-greeting']);
    expect(selectedId(studio)).toBe('log-greeting');
  });

  it('typing in one property is one step; undo and redo it', async () => {
    const { studio, state } = await openHelloWorld();
    const key = keyOfId(studio, 'log-greeting');
    studio.select(key);
    for (const text of ['g', 'gr', 'gre']) {
      studio.editProperty(key, message, text);
    }

    expect(state().undo).toHaveLength(1);
    studio.undo();
    expect(propertiesOf(studio, 'log-greeting')).toEqual({ message: 'greeting' });
    expect(isDirty(state())).toBe(false);
    studio.redo();
    expect(propertiesOf(studio, 'log-greeting')).toEqual({ message: 'gre' });
  });

  it('selecting another node or undoing ends a typing group', async () => {
    const { studio, state } = await openHelloWorld();
    const logKey = keyOfId(studio, 'log-greeting');
    studio.select(logKey);
    studio.editProperty(logKey, message, 'a');
    studio.select(keyOfId(studio, 'main'));
    studio.select(logKey);
    studio.editProperty(logKey, message, 'ab');

    expect(state().undo).toHaveLength(2);
  });

  it('walks back and forth through a mixed structural and property history', async () => {
    const { studio, state } = await openHelloWorld();
    const saved = state().saved;
    studio.select(keyOfId(studio, 'build-greeting'));
    studio.insertActivity('Core.Log');
    const log1 = state().selectedKey!;
    studio.editProperty(log1, message, "'inserted'");
    studio.moveSelected(-1);
    studio.select(keyOfId(studio, 'log-greeting'));
    studio.deleteSelected();
    const final = state().document;
    expect(ids(studio)).toEqual(['main', 'log-1', 'build-greeting']);
    expect(state().undo.map((e) => e.label)).toEqual(['Insert Log', 'Edit message', 'Move log-1 up', 'Delete log-greeting']);

    for (let i = 0; i < 4; i++) {
      studio.undo();
    }
    expect(state().document).toBe(saved);
    expect(isDirty(state())).toBe(false);
    studio.undo();
    expect(state().document).toBe(saved);

    for (let i = 0; i < 4; i++) {
      studio.redo();
    }
    expect(state().document).toBe(final);
    expect(propertiesOf(studio, 'log-1')).toEqual({ message: "'inserted'" });
    expect(state().redo).toHaveLength(0);
  });

  it('a new edit after undo discards the redo branch', async () => {
    const { studio, state } = await openHelloWorld();
    studio.insertActivity('Core.Log');
    studio.undo();
    expect(state().redo).toHaveLength(1);

    studio.insertActivity('Core.Assign');

    expect(state().redo).toHaveLength(0);
    studio.redo();
    expect(ids(studio)).toEqual(['main', 'build-greeting', 'log-greeting', 'assign-1']);
  });

  it('keeps the dirty state right across save, undo and redo', async () => {
    const { studio, state } = await openHelloWorld();
    studio.insertActivity('Core.Log');
    await studio.save();
    expect(isDirty(state())).toBe(false);

    studio.undo();
    expect(isDirty(state())).toBe(true);
    studio.redo();
    expect(isDirty(state())).toBe(false);
  });

  it(`keeps at most ${maxUndo} steps`, async () => {
    const { studio, state } = await openHelloWorld();
    for (let i = 0; i < maxUndo + 5; i++) {
      studio.select(keyOfId(studio, 'main'));
      studio.insertActivity('Core.Log');
    }

    expect(state().undo).toHaveLength(maxUndo);
  });

  it('opening a file starts a new history', async () => {
    const { studio, state } = await openHelloWorld();
    studio.insertActivity('Core.Log');

    await studio.open('hello-world.json');

    expect(state().undo).toHaveLength(0);
    expect(state().redo).toHaveLength(0);
  });
});

describe('save and reload', () => {
  it('reproduces the edited structure from the saved file, without client keys', async () => {
    const { studio, api, state } = await openHelloWorld();
    studio.select(keyOfId(studio, 'build-greeting'));
    studio.insertActivity('Core.Log');
    studio.editProperty(state().selectedKey!, message, "'between'");
    studio.moveSelected(-1);
    await studio.save();

    const text = api.files.get('hello-world.json')!.text;
    await studio.open('hello-world.json');

    expect(ids(studio)).toEqual(['main', 'log-1', 'build-greeting', 'log-greeting']);
    expect(propertiesOf(studio, 'log-1')).toEqual({ message: "'between'" });
    const reopened = openWorkflow(text);
    expect(reopened.ok && (reopened.document.root as JsonObject).children).toEqual([
      { id: 'log-1', type: 'Core.Log', properties: { message: "'between'" } },
      { id: 'build-greeting', type: 'Core.Assign', properties: { to: 'greeting', value: "'Hello, ' + userName + '!'" } },
      { id: 'log-greeting', type: 'Core.Log', properties: { message: 'greeting' } },
    ]);
  });
});
