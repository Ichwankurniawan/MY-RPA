import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { memoryDrafts } from './drafts';
import { serialize } from './document';
import { Studio } from './studio';
import { FakeApi, FakeEventSource, immediately, settle } from './test-support';
import type { JsonObject } from './types';

const workflow: JsonObject = {
  schemaVersion: '1.0',
  id: 'w',
  name: 'W',
  version: '1.0.0',
  arguments: [{ name: 'who', direction: 'In', type: 'String' }],
  variables: [{ name: 'message', type: 'String' }],
  root: {
    id: 'main',
    type: 'Core.Sequence',
    children: [
      { id: 'set', type: 'Core.Assign', properties: { to: 'message', value: "'Hi'" } },
      { id: 'log-1', type: 'Core.Log', properties: { message: 500 } },
      { id: 'call', type: 'Core.InvokeWorkflow', properties: { workflow: 'child.json', arguments: { who: 'who' } } },
      { id: 'odd', type: 'Vendor.Unknown', properties: { setting: 42 } },
    ],
  },
};

async function renderStudio(api = new FakeApi()) {
  api.files.set('w.json', { text: serialize(workflow), etag: 1 });
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { drafts: memoryDrafts(), validateDelayMs: undefined });
  render(<App studio={studio} />);
  await act(settle);
  fireEvent.change(screen.getByRole('combobox', { name: 'Workflow' }), { target: { value: 'w.json' } });
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name: 'Open' }));
    await settle();
  });
  return { studio, api, state: () => studio.store.get() };
}

const node = (id: string) => document.querySelector<HTMLElement>(`[role="treeitem"][data-node-id="${id}"]`)!;
const properties = () => screen.getByRole('complementary', { name: 'Properties' });
const title = () => screen.getByTestId('document-title').textContent;

beforeEach(() => {
  FakeEventSource.instances = [];
});

afterEach(cleanup);

describe('Authoring UI', () => {
  it('edits the workflow metadata from the Workflow breadcrumb, and Undo takes it back', async () => {
    await renderStudio();

    fireEvent.click(within(screen.getByRole('navigation', { name: 'Selection' })).getByRole('button', { name: 'Workflow' }));
    fireEvent.change(within(properties()).getByLabelText('Name'), { target: { value: 'Greeter' } });
    fireEvent.change(within(properties()).getByLabelText('Description'), { target: { value: 'Says hello' } });

    expect(title()).toBe('w.json •');
    expect((within(properties()).getByLabelText('Name') as HTMLInputElement).value).toBe('Greeter');
    fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
    fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
    expect((within(properties()).getByLabelText('Name') as HTMLInputElement).value).toBe('W');
    expect(title()).toBe('w.json');
  });

  it('shows the selection as breadcrumbs, and edits the node id', async () => {
    const { state } = await renderStudio();
    fireEvent.click(node('log-1'));

    const crumbs = within(screen.getByRole('navigation', { name: 'Selection' })).getAllByRole('button').map((b) => b.textContent);
    expect(crumbs).toEqual(['Workflow', 'Sequence', 'Log']);
    fireEvent.change(within(properties()).getByLabelText('Id'), { target: { value: 'log-greeting' } });
    expect(node('log-greeting')).toBeTruthy();
    expect(state().undo.at(-1)?.label).toBe('Edit id');
  });

  it('shows literals as JSON and stores edits as expression text; suggests assignment targets', async () => {
    const { state } = await renderStudio();
    fireEvent.click(node('log-1'));
    const message = within(properties()).getByLabelText(/^message/) as HTMLInputElement;
    expect(message.value).toBe('500');
    expect(within(properties()).getByText('literal')).toBeTruthy();
    fireEvent.change(message, { target: { value: '501' } });
    expect(JSON.stringify(state().document)).toContain('"message":"501"');

    fireEvent.click(node('set'));
    const to = within(properties()).getByLabelText(/^to/) as HTMLInputElement;
    const list = document.getElementById(to.getAttribute('list')!)!;
    expect([...list.querySelectorAll('option')].map((o) => o.value)).toEqual(['message']);
  });

  it('edits map properties entry by entry; a duplicate name is refused in place', async () => {
    const { state } = await renderStudio();
    fireEvent.click(node('call'));
    const group = within(properties()).getByRole('group', { name: /^arguments/ });

    fireEvent.change(within(group).getByLabelText('Entry expression'), { target: { value: "'Ada'" } });
    fireEvent.click(within(group).getByRole('button', { name: 'Add entry' }));
    const names = within(group).getAllByLabelText('Entry name') as HTMLInputElement[];
    expect(names.map((n) => n.value)).toEqual(['who', 'name1']);
    fireEvent.change(names[1], { target: { value: 'who' } });
    expect(within(group).getByText("'who' is already an entry.")).toBeTruthy();
    fireEvent.change(names[1], { target: { value: 'greeting' } });

    const call = ((state().document!.root as JsonObject).children as JsonObject[])[2];
    expect(call.properties).toEqual({ workflow: 'child.json', arguments: { who: "'Ada'", greeting: '' } });
    fireEvent.click(within(group).getByRole('button', { name: 'Remove who' }));
    fireEvent.click(within(group).getByRole('button', { name: 'Remove greeting' }));
    expect(((state().document!.root as JsonObject).children as JsonObject[])[2].properties).toEqual({ workflow: 'child.json' });
  });

  it('edits an activity missing from the catalog as raw JSON, refusing invalid JSON without changing anything', async () => {
    const { state } = await renderStudio();
    fireEvent.click(node('odd'));
    expect(within(properties()).getByText(/not in the catalog/)).toBeTruthy();
    const setting = within(properties()).getByLabelText('setting (JSON)') as HTMLInputElement;

    fireEvent.change(setting, { target: { value: '{ broken' } });
    expect(within(properties()).getByText(/^Not valid JSON/)).toBeTruthy();
    expect(JSON.stringify(state().document)).toContain('"setting":42');
    fireEvent.change(setting, { target: { value: '[1, 2]' } });
    expect(JSON.stringify(state().document)).toContain('"setting":[1,2]');

    fireEvent.change(within(properties()).getByLabelText('New property name'), { target: { value: 'extra' } });
    fireEvent.change(within(properties()).getByLabelText('New property value (JSON)'), { target: { value: 'true' } });
    fireEvent.click(within(properties()).getByRole('button', { name: 'Add' }));
    expect(JSON.stringify(state().document)).toContain('"extra":true');
  });

  it('edits variables and arguments in their tabs; Out arguments have no required flag or default', async () => {
    const { state } = await renderStudio();

    fireEvent.click(screen.getByRole('tab', { name: 'Variables (1)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add variable' }));
    fireEvent.change(screen.getByLabelText('variable 2 name'), { target: { value: 'count' } });
    fireEvent.change(screen.getByLabelText('variable 2 type'), { target: { value: 'Int' } });
    fireEvent.change(screen.getByLabelText('variable 2 default'), { target: { value: 'three' } });
    expect(screen.getAllByText(/^Not valid JSON/).length).toBeGreaterThan(0);
    fireEvent.change(screen.getByLabelText('variable 2 default'), { target: { value: '3' } });
    expect((state().document!.variables as JsonObject[])[1]).toEqual({ name: 'count', type: 'Int', default: 3 });

    fireEvent.click(screen.getByRole('tab', { name: 'Arguments (1)' }));
    fireEvent.click(screen.getByLabelText('argument 1 required'));
    fireEvent.change(screen.getByLabelText('argument 1 default'), { target: { value: '"World"' } });
    expect((state().document!.arguments as JsonObject[])[0]).toEqual({ name: 'who', direction: 'In', type: 'String', required: true, default: 'World' });
    fireEvent.change(screen.getByLabelText('argument 1 direction'), { target: { value: 'Out' } });
    expect((state().document!.arguments as JsonObject[])[0]).toEqual({ name: 'who', direction: 'Out', type: 'String' });
    expect(screen.getByLabelText('argument 1 required')).toHaveProperty('disabled', true);
    expect(screen.getByLabelText('argument 1 default')).toHaveProperty('disabled', true);

    fireEvent.click(screen.getByRole('button', { name: 'Remove argument who' }));
    expect(state().document!.arguments).toEqual([]);
  });

  it('shows row problems on the row and goes there from the Problems list', async () => {
    const api = new FakeApi();
    api.validation = { valid: false, diagnostics: [{ code: 'MYRPA1063', severity: 'Error', message: 'Invalid type.', path: '$.variables[0].type' }] };
    await renderStudio(api);
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Validate' }));
      await settle();
    });

    expect(screen.getByRole('tab', { name: 'Problems (1)' })).toBeTruthy();
    await act(async () => {
      fireEvent.click(within(screen.getByRole('list', { name: 'Problems' })).getByRole('button', { name: /variable 1/ }));
      await settle();
    });
    expect(screen.getByRole('tab', { name: 'Variables (1)', selected: true })).toBeTruthy();
    expect(screen.getByText('MYRPA1063: Invalid type.')).toBeTruthy();
    expect(document.activeElement).toBe(screen.getByLabelText('variable 1 name'));
  });

  it('groups activities by category, collapses a category, and says when a search finds nothing', async () => {
    await renderStudio();
    const catalog = screen.getByRole('list', { name: 'Activity catalog' });

    expect([...catalog.querySelectorAll('.category-toggle')].map((b) => b.textContent)).toEqual([
      '▾ Control Flow (2)',
      '▾ Data (1)',
      '▾ Diagnostics (1)',
      '▾ Workflow (1)',
    ]);
    fireEvent.click(within(catalog).getByRole('button', { name: 'Data (1)', expanded: true }));
    expect(within(catalog).queryByRole('button', { name: 'Insert Assign (Core.Assign)' })).toBeNull();

    fireEvent.change(screen.getByLabelText('Search activities'), { target: { value: 'writes a' } }); // matches a description
    expect(within(catalog).getByRole('button', { name: 'Insert Log (Core.Log)' })).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Search activities'), { target: { value: 'nothing-like-this' } });
    expect(screen.getByText('No activity matches "nothing-like-this".')).toBeTruthy();
  });
});
