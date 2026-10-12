import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { memoryPreferences } from './preferences';
import { Studio } from './studio';
import { FakeApi, FakeEventSource, immediately, settle } from './test-support';

// Studio's start (ADR-0046, ADR-0044 amendment): the projects, without the editing toolbar; New project and Delete project.

async function renderStudio(api = new FakeApi()) {
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { preferences: memoryPreferences(), validateDelayMs: undefined });
  render(<App studio={studio} />);
  await act(settle);
  return { studio, api };
}

const rail = () => within(screen.getByRole('navigation', { name: 'Main' }));
const page = () => within(screen.getByRole('main', { name: 'Studio' }));
const dialog = () => within(screen.getByRole('dialog'));
const showProjects = () => act(async () => fireEvent.click(rail().getByRole('button', { name: 'Studio' })));

afterEach(cleanup);

describe('Studio projects', () => {
  it('are where Studio starts, without the editing toolbar; choosing one opens its workspace with the toolbar', async () => {
    await renderStudio();

    expect(rail().getAllByRole('button').map((b) => b.textContent)).toEqual(['Home', 'Studio']);

    await showProjects();

    expect(rail().getByRole('button', { name: 'Studio' }).getAttribute('aria-current')).toBe('page');
    expect(page().getByRole('button', { name: 'Open project demo' })).toBeTruthy();
    expect(screen.queryByRole('toolbar', { name: 'File' })).toBeNull();
    expect(screen.queryByRole('toolbar', { name: 'Run' })).toBeNull();

    // Choosing a project shows its workspace (still Studio), with the toolbar.
    await act(async () => fireEvent.click(page().getByRole('button', { name: 'Open project demo' })));
    expect(rail().getByRole('button', { name: 'Studio' }).getAttribute('aria-current')).toBe('page');
    expect(screen.getByRole('toolbar', { name: 'File' })).toBeTruthy();
  });

  it('says how to allow New project when the server has no projects folder, and offers no Delete for a --project', async () => {
    await renderStudio();
    await showProjects();

    const create = page().getByRole('button', { name: 'New project…' }) as HTMLButtonElement;
    expect(create.disabled).toBe(true);
    expect(create.title).toBe('Start the server with --projects-root <folder> to create projects');
    expect(page().queryByRole('button', { name: 'Delete project demo' })).toBeNull();
    expect(page().getByText(/named with --project/)).toBeTruthy();
  });

  it('creates a project in the projects folder and opens its workspace; a taken or unsafe name stays in the dialog', async () => {
    const api = new FakeApi();
    api.projectsRoot = 'C:\\Users\\robot\\Documents\\Laconi Projects';
    const { studio } = await renderStudio(api);
    await showProjects();
    expect(page().getByText(/New projects are created in C:\\Users\\robot\\Documents\\Laconi Projects\./)).toBeTruthy();

    await act(async () => fireEvent.click(page().getByRole('button', { name: 'New project…' })));
    const name = dialog().getByLabelText('Project name');
    fireEvent.change(name, { target: { value: 'Demo' } });
    await act(async () => fireEvent.click(dialog().getByRole('button', { name: 'Create' })));
    expect(dialog().getByRole('status').textContent).toBe("A project or folder named 'Demo' already exists.");
    fireEvent.change(name, { target: { value: 'a/b' } });
    await act(async () => fireEvent.click(dialog().getByRole('button', { name: 'Create' })));
    expect(dialog().getByRole('status').textContent).toMatch(/^A project name cannot/);

    fireEvent.change(name, { target: { value: 'Invoices' } });
    await act(async () => fireEvent.click(dialog().getByRole('button', { name: 'Create' })));
    await act(settle);

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(api.createdProjects).toEqual(['Invoices']);
    expect(studio.store.get().page).toBe('workflows');
    expect(screen.getByTestId('current-project').textContent).toBe('Invoices');
    expect(studio.store.get().message).toBe('Created the project Invoices.');
  });

  it('deletes a project of the projects folder only after its name is typed, closes its open workflow, and moves on', async () => {
    const api = new FakeApi();
    api.projectsRoot = '/home/robot/Laconi Projects';
    api.createdProjects = ['Old'];
    api.removableProjects.add('Old');
    const { studio } = await renderStudio(api);
    await act(async () => studio.enterProject('Old'));
    await act(async () => studio.open('hello-world.json'));
    expect(studio.store.get().file?.project).toBe('Old');
    // In a project's workspace, the title bar's Studio link goes back to the projects (the rail returns to the workspace).
    await act(async () => fireEvent.click(within(screen.getByRole('navigation', { name: 'Location' })).getByRole('button', { name: 'Studio' })));

    await act(async () => fireEvent.click(page().getByRole('button', { name: 'Delete project Old' })));
    const confirm = dialog().getByRole('button', { name: 'Delete project' }) as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);
    fireEvent.change(dialog().getByLabelText('Type Old to confirm'), { target: { value: 'old' } });
    expect(confirm.disabled).toBe(true);
    fireEvent.change(dialog().getByLabelText('Type Old to confirm'), { target: { value: 'Old' } });
    expect(confirm.disabled).toBe(false);
    await act(async () => fireEvent.click(confirm));
    await act(settle);

    expect(api.deletedProjects).toEqual(['Old']);
    expect(studio.store.get().file).toBeUndefined();
    expect(studio.store.get().project).toBe('demo');
    expect(page().queryByRole('button', { name: 'Open project Old' })).toBeNull();
    expect(studio.store.get().message).toBe('Deleted the project Old: its folder is now .trash/Old-20261011-120000 in the projects folder.');
  });
});
