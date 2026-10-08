import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { memoryDrafts, type DraftStore } from './drafts';
import { Studio } from './studio';
import { FakeApi, FakeEventSource, helloWorld, immediately, settle } from './test-support';

/** The Properties panel (UX-3: the selected card has the same editors inline). */
const properties = () => within(screen.getByRole('complementary', { name: 'Properties' }));


async function renderStudio(api = new FakeApi(), drafts: DraftStore = memoryDrafts()) {
  api.files.set('other.json', { text: helloWorld.replace('"hello-world"', '"other"'), etag: 1 });
  api.files.set('flows/nested.json', { text: helloWorld, etag: 1 });
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { drafts, draftDelayMs: 60_000 });
  render(<App studio={studio} />);
  await act(settle);
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

    expect(within(files()).getAllByRole('treeitem').map((i) => i.textContent)).toEqual(['flows/nested.json', 'nested.json', 'hello-world.json', 'other.json']);
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
