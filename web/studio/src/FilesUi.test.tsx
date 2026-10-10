import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { memoryDrafts, type DraftStore } from './drafts';
import { Studio } from './studio';
import { FakeApi, FakeEventSource, helloWorld, immediately, settle } from './test-support';

/** The Properties panel (the only place properties are edited). */
const properties = () => within(screen.getByRole('complementary', { name: 'Properties' }));


async function renderStudio(api = new FakeApi(), drafts: DraftStore = memoryDrafts()) {
  api.files.set('other.json', { text: helloWorld.replace('"hello-world"', '"other"'), etag: 1 });
  api.files.set('flows/nested.json', { text: helloWorld, etag: 1 });
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { drafts, draftDelayMs: 60_000 });
  render(<App studio={studio} />);
  await act(settle);
  // The Workflows page starts at the projects; choosing one shows its workspace with the Files tab.
  await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Open project demo' })));
  await act(settle);
  expect(screen.getByRole('tab', { name: 'Files' }).getAttribute('aria-selected')).toBe('true');
  return { studio, api };
}

const files = () => screen.getByRole('tree', { name: 'Workflow files' });
const fileItem = (path: string) => files().querySelector<HTMLElement>(`[data-path="${path}"]`)!;
const act$ = (action: () => void) =>
  act(async () => {
    action();
    await settle();
  });
const dialog = () => screen.getByRole('dialog');
const title = () => screen.getByTestId('document-title').textContent;

beforeEach(() => {
  FakeEventSource.instances = [];
});

afterEach(cleanup);

describe('Files panel', () => {
  it('lists the project as folders and files; double-click and Enter open a file', async () => {
    await renderStudio();

    // The project is the root of the tree.
    expect([...files().querySelectorAll('.tree-name')].map((i) => i.textContent)).toEqual(['demo', 'flows', 'nested.json', 'hello-world.json', 'other.json']);
    expect(files().querySelector('.project-root')!.getAttribute('aria-level')).toBe('1');
    expect(files().querySelector('[data-folder="flows"]')!.getAttribute('aria-level')).toBe('2');
    expect(files().querySelector('[data-folder="flows"]')!.getAttribute('aria-expanded')).toBe('true');
    await act$(() => fireEvent.doubleClick(fileItem('other.json')));
    expect(title()).toBe('other.json');
    expect(fileItem('other.json').getAttribute('aria-current')).toBe('true');

    fireEvent.click(fileItem('other.json'));
    await act$(() => fireEvent.keyDown(fileItem('other.json'), { key: 'ArrowUp' }));
    expect(fileItem('hello-world.json').getAttribute('aria-selected')).toBe('true');
    await act$(() => fireEvent.keyDown(fileItem('hello-world.json'), { key: 'Enter' }));
    expect(title()).toBe('hello-world.json');
  });

  it('creates a file with New…, shows a refused name, and opens the new file', async () => {
    const { api } = await renderStudio();

    await act$(() => fireEvent.click(screen.getByRole('button', { name: 'New…' })));
    const path = within(dialog()).getByLabelText('Path in the project');
    expect((path as HTMLInputElement).value).toBe('new-workflow.json');
    fireEvent.change(path, { target: { value: 'other.json' } });
    await act$(() => fireEvent.click(within(dialog()).getByRole('button', { name: 'Create' })));
    expect(within(dialog()).getByText("'other.json' already exists.")).toBeTruthy();

    fireEvent.change(path, { target: { value: 'flows/made.json' } });
    await act$(() => fireEvent.click(within(dialog()).getByRole('button', { name: 'Create' })));

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(api.files.has('flows/made.json')).toBe(true);
    expect(title()).toBe('flows/made.json');
  });

  it('creates a flowchart or a state machine when New… is given that type (schema 1.1, one starter step)', async () => {
    const { api } = await renderStudio();

    await act$(() => fireEvent.click(screen.getByRole('button', { name: 'New…' })));
    expect((within(dialog()).getByRole('radio', { name: /^Sequence/ }) as HTMLInputElement).checked).toBe(true);
    fireEvent.click(within(dialog()).getByRole('radio', { name: /^Flowchart/ }));
    fireEvent.change(within(dialog()).getByLabelText('Path in the project'), { target: { value: 'chart.json' } });
    await act$(() => fireEvent.click(within(dialog()).getByRole('button', { name: 'Create' })));

    const chart = JSON.parse(api.files.get('chart.json')!.text);
    expect([chart.schemaVersion, chart.root.type, chart.root.children.length]).toEqual(['1.1', 'Core.Flowchart', 1]);
    expect(title()).toBe('chart.json');

    await act$(() => fireEvent.click(screen.getByRole('button', { name: 'New…' })));
    fireEvent.click(within(dialog()).getByRole('radio', { name: /^State machine/ }));
    fireEvent.change(within(dialog()).getByLabelText('Path in the project'), { target: { value: 'states.json' } });
    await act$(() => fireEvent.click(within(dialog()).getByRole('button', { name: 'Create' })));

    const states = JSON.parse(api.files.get('states.json')!.text);
    expect([states.schemaVersion, states.root.type, states.root.children[0].type]).toEqual(['1.1', 'Core.StateMachine', 'Core.State']);
  });

  it('closes and opens a folder by click and by keyboard', async () => {
    await renderStudio();
    const folder = () => files().querySelector<HTMLElement>('[data-folder="flows"]')!;

    fireEvent.click(folder().querySelector('.tree-row')!);
    expect(folder().getAttribute('aria-expanded')).toBe('false');
    expect(fileItem('flows/nested.json')).toBeNull();

    fireEvent.keyDown(folder(), { key: 'ArrowRight' });
    expect(folder().getAttribute('aria-expanded')).toBe('true');
    expect(fileItem('flows/nested.json')).not.toBeNull();
    fireEvent.keyDown(folder(), { key: 'ArrowLeft' });
    expect(folder().getAttribute('aria-expanded')).toBe('false');
    fireEvent.keyDown(folder(), { key: 'Enter' });
    expect(folder().getAttribute('aria-expanded')).toBe('true');

    // The project is the root: Left from a top-level folder goes to it; closing it hides everything; New… goes there.
    const root = () => files().querySelector<HTMLElement>('.project-root')!;
    fireEvent.keyDown(folder(), { key: 'ArrowLeft' });
    fireEvent.keyDown(folder(), { key: 'ArrowLeft' });
    expect(root().getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('button', { name: 'New…' }).title).toBe('Create a new workflow in the project');
    fireEvent.keyDown(root(), { key: 'ArrowLeft' });
    expect(root().getAttribute('aria-expanded')).toBe('false');
    expect(fileItem('other.json')).toBeNull();
    fireEvent.keyDown(root(), { key: 'Enter' });
    expect(fileItem('other.json')).not.toBeNull();
  });

  it('creates a folder with New folder…, refuses a bad or taken name, and lists the empty folder', async () => {
    const { api } = await renderStudio();

    await act$(() => fireEvent.click(screen.getByRole('button', { name: 'New folder…' })));
    const path = within(dialog()).getByLabelText('Path in the project');
    expect((path as HTMLInputElement).value).toBe('new-folder');
    fireEvent.change(path, { target: { value: '../outside' } });
    await act$(() => fireEvent.click(within(dialog()).getByRole('button', { name: 'Create' })));
    expect(within(dialog()).getByText(/Use a folder path inside the project/)).toBeTruthy();
    fireEvent.change(path, { target: { value: 'flows' } });
    await act$(() => fireEvent.click(within(dialog()).getByRole('button', { name: 'Create' })));
    expect(within(dialog()).getByText("'flows' already exists.")).toBeTruthy();

    fireEvent.change(path, { target: { value: 'invoices' } });
    await act$(() => fireEvent.click(within(dialog()).getByRole('button', { name: 'Create' })));

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(api.folders.has('invoices')).toBe(true);
    expect(files().querySelector('[data-folder="invoices"]')).not.toBeNull();
  });

  it('starts New… and New folder… inside the selected folder', async () => {
    await renderStudio();
    fireEvent.click(files().querySelector('[data-folder="flows"] .tree-row')!);

    await act$(() => fireEvent.click(screen.getByRole('button', { name: 'New…' })));
    expect((within(dialog()).getByLabelText('Path in the project') as HTMLInputElement).value).toBe('flows/new-workflow.json');
    await act$(() => fireEvent.click(within(dialog()).getByRole('button', { name: 'Cancel' })));
    await act$(() => fireEvent.click(screen.getByRole('button', { name: 'New folder…' })));
    expect((within(dialog()).getByLabelText('Path in the project') as HTMLInputElement).value).toBe('flows/new-folder');
  });

  it('renames with F2 and deletes with the Delete key after confirming', async () => {
    const { api } = await renderStudio();
    fireEvent.click(fileItem('other.json'));

    await act$(() => fireEvent.keyDown(fileItem('other.json'), { key: 'F2' }));
    expect(screen.getByRole('dialog', { name: 'Rename other.json' })).toBeTruthy();
    fireEvent.change(within(dialog()).getByLabelText('Path in the project'), { target: { value: 'renamed.json' } });
    await act$(() => fireEvent.click(within(dialog()).getByRole('button', { name: 'Rename' })));
    expect(api.files.has('renamed.json') && !api.files.has('other.json')).toBe(true);

    fireEvent.click(fileItem('renamed.json'));
    await act$(() => fireEvent.keyDown(fileItem('renamed.json'), { key: 'Delete' }));
    expect(screen.getByRole('dialog', { name: 'Delete renamed.json' })).toBeTruthy();
    await act$(() => fireEvent.click(within(dialog()).getByRole('button', { name: 'Delete' })));
    expect(api.files.has('renamed.json')).toBe(false);
    expect(fileItem('renamed.json')).toBeNull();
  });
});

describe('In-app prompts', () => {
  it('asks about unsaved changes in the Studio (never window.confirm); Discard opens the other file', async () => {
    const confirm = vi.spyOn(window, 'confirm');
    await renderStudio();
    await act$(() => fireEvent.doubleClick(fileItem('hello-world.json')));
    fireEvent.click(document.querySelector('[role="treeitem"][data-node-id="log-greeting"]')!);
    fireEvent.change(properties().getByLabelText(/^message/), { target: { value: "'changed'" } });

    fireEvent.change(screen.getByRole('combobox', { name: 'Workflow' }), { target: { value: 'other.json' } });
    await act$(() => fireEvent.click(screen.getByRole('button', { name: 'Open' })));

    expect(screen.getByRole('dialog', { name: 'Unsaved changes' })).toBeTruthy();
    expect(confirm).not.toHaveBeenCalled();
    await act$(() => fireEvent.click(within(dialog()).getByRole('button', { name: 'Discard' })));
    expect(title()).toBe('other.json');
    confirm.mockRestore();
  });

  it('offers Reload, Overwrite and Save as when the file changed on disk', async () => {
    const { api } = await renderStudio();
    await act$(() => fireEvent.doubleClick(fileItem('hello-world.json')));
    fireEvent.click(document.querySelector('[role="treeitem"][data-node-id="log-greeting"]')!);
    fireEvent.change(properties().getByLabelText(/^message/), { target: { value: "'mine'" } });
    api.files.get('hello-world.json')!.etag = 8;

    await act$(() => fireEvent.click(screen.getByRole('button', { name: 'Save' })));
    const conflict = screen.getByRole('dialog', { name: 'The file changed on disk' });
    expect(within(conflict).getAllByRole('button').map((b) => b.textContent)).toEqual(['Reload from disk', 'Overwrite with mine', 'Save mine as…', 'Cancel']);
    await act$(() => fireEvent.click(within(conflict).getByRole('button', { name: 'Overwrite with mine' })));

    expect(api.files.get('hello-world.json')!.text).toContain("'mine'");
    expect(title()).toBe('hello-world.json');
  });

  it('offers to restore unsaved changes found for a file', async () => {
    const drafts = memoryDrafts();
    drafts.put('demo', 'hello-world.json', { text: helloWorld.replace('"message": "greeting"', '"message": "\'recovered\'"'), etag: '"1"', savedAt: '2026-10-07T10:00:00Z' });
    await renderStudio(new FakeApi(), drafts);

    await act$(() => fireEvent.doubleClick(fileItem('hello-world.json')));
    expect(screen.getByRole('dialog', { name: 'Recover unsaved changes' })).toBeTruthy();
    await act$(() => fireEvent.click(within(dialog()).getByRole('button', { name: 'Restore' })));

    expect(title()).toBe('hello-world.json •');
    fireEvent.click(document.querySelector('[role="treeitem"][data-node-id="log-greeting"]')!);
    expect((properties().getByLabelText(/^message/) as HTMLInputElement).value).toBe("'recovered'");
  });
});
