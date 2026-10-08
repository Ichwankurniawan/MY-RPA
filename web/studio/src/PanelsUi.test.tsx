import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { memoryPreferences, preferenceKeys } from './preferences';
import { maxRecent, paneLimits, Studio } from './studio';
import { catalog, FakeApi, FakeEventSource, immediately, settle } from './test-support';

// UX-2 (studio-ux-plan.md): favorites and recent activities, namespaces, the bottom tab strip, resizable panels.

async function renderStudio(preferences = memoryPreferences(), api = new FakeApi()) {
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { preferences, validateDelayMs: undefined });
  render(<App studio={studio} />);
  await act(settle);
  await act(async () => {
    await studio.open('hello-world.json');
  });
  return { studio, api, preferences, state: () => studio.store.get() };
}

const treeItem = (nodeId: string) => document.querySelector<HTMLElement>(`[role="treeitem"][data-node-id="${nodeId}"]`)!;

beforeEach(() => {
  FakeEventSource.instances = [];
});

afterEach(cleanup);

describe('Activity panel', () => {
  it('groups the catalog by namespace, built-in first, then by category', async () => {
    await renderStudio();
    const panel = screen.getByRole('list', { name: 'Activity catalog' });

    expect([...panel.querySelectorAll('.namespace-toggle')].map((b) => b.textContent)).toEqual([`▾ Built-in (${catalog.length})`]);
    fireEvent.click(within(panel).getByRole('button', { name: `Built-in (${catalog.length})`, expanded: true }));
    expect(within(panel).queryByRole('button', { name: 'Insert Log (Core.Log)' })).toBeNull();
  });

  it('pins favorites at the top and remembers them per browser', async () => {
    const { preferences, state } = await renderStudio();

    fireEvent.click(screen.getByRole('button', { name: 'Add Log to favorites' }));

    const favorites = screen.getByRole('region', { name: 'Favorites' });
    expect(within(favorites).getByRole('button', { name: 'Log (Core.Log) from Favorites' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Remove Log from favorites' }).getAttribute('aria-pressed')).toBe('true');
    expect(state().favorites).toEqual(['Core.Log']);
    expect(preferences.values.get(preferenceKeys.favorites)).toBe('["Core.Log"]');

    fireEvent.click(screen.getByRole('button', { name: 'Remove Log from favorites' }));
    expect(screen.queryByRole('region', { name: 'Favorites' })).toBeNull();
  });

  it('lists recently inserted activities (most recent first, at most ten), and inserts from there', async () => {
    const { state } = await renderStudio();
    fireEvent.click(treeItem('main'));

    fireEvent.click(screen.getByRole('button', { name: 'Insert Log (Core.Log)' }));
    fireEvent.click(treeItem('main'));
    fireEvent.click(screen.getByRole('button', { name: 'Insert Assign (Core.Assign)' }));

    const recent = screen.getByRole('region', { name: 'Recent' });
    expect(within(recent).getAllByRole('button').map((b) => b.getAttribute('aria-label'))).toEqual(['Assign (Core.Assign) from Recent', 'Log (Core.Log) from Recent']);
    fireEvent.click(treeItem('main'));
    fireEvent.click(within(recent).getByRole('button', { name: 'Log (Core.Log) from Recent' }));
    expect(state().recentActivities).toEqual(['Core.Log', 'Core.Assign']);
    expect(maxRecent).toBe(10);
  });

  it('starts with the remembered favorites and recent activities, ignoring unknown types', async () => {
    const preferences = memoryPreferences();
    preferences.write(preferenceKeys.favorites, ['Core.Assign', 'Vendor.Gone']);
    preferences.write(preferenceKeys.recent, ['Core.Log']);
    await renderStudio(preferences);

    expect(within(screen.getByRole('region', { name: 'Favorites' })).getAllByRole('button')).toHaveLength(1);
    expect(within(screen.getByRole('region', { name: 'Recent' })).getByRole('button', { name: 'Log (Core.Log) from Recent' })).toBeTruthy();
  });
});

describe('Bottom panel', () => {
  it('is one tab strip; a run shows Execution, Validate shows Problems (with or without errors)', async () => {
    const { api, studio } = await renderStudio();
    const tab = (name: RegExp) => screen.getByRole('tab', { name });

    expect(tab(/^Problems/).getAttribute('aria-selected')).toBe('true');
    expect(screen.getAllByRole('tab').map((t) => t.textContent)).toEqual(['Problems', 'Variables (0)', 'Arguments (2)', 'Execution', 'Recorder']);

    await act(async () => {
      await studio.run({ argumentText: {} });
    });
    expect(tab(/^Execution/).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByTestId('run-status')).toBeTruthy();

    await act(async () => {
      await studio.validate();
    });
    expect(tab(/^Problems/).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByTestId('no-problems')).toBeTruthy();

    studio.showOutput('execution');
    api.validation = { valid: false, diagnostics: [{ code: 'MYRPA1043', severity: 'Error', message: 'Syntax error.', path: '$.root', nodeId: 'main' }] };
    await act(async () => {
      await studio.validate();
    });
    expect(tab(/^Problems/).getAttribute('aria-selected')).toBe('true');
    expect(tab(/^Problems/).textContent).toBe('Problems (1)');
  });

  it('moves between tabs with the arrow keys', async () => {
    await renderStudio();
    const problems = screen.getByRole('tab', { name: /^Problems/ });
    problems.focus();

    fireEvent.keyDown(problems, { key: 'End' });
    expect(screen.getByRole('tab', { name: 'Recorder' }).getAttribute('aria-selected')).toBe('true');
    fireEvent.keyDown(screen.getByRole('tab', { name: 'Recorder' }), { key: 'ArrowLeft' });
    expect(screen.getByRole('tab', { name: 'Execution' }).getAttribute('aria-selected')).toBe('true');
    fireEvent.keyDown(screen.getByRole('tab', { name: 'Execution' }), { key: 'End' });
    fireEvent.keyDown(screen.getByRole('tab', { name: 'Recorder' }), { key: 'ArrowRight' });
    expect(screen.getByRole('tab', { name: /^Problems/ }).getAttribute('aria-selected')).toBe('true');
  });
});

describe('Panels', () => {
  it('resizes a panel with its splitter and the arrow keys, within limits, and remembers the size', async () => {
    const { state, preferences, studio } = await renderStudio();
    const splitter = screen.getByRole('separator', { name: 'Resize the activities panel' });

    act(() => studio.setPaneSize('toolbox', 300));
    fireEvent.keyDown(splitter, { key: 'ArrowRight' });
    expect(state().panes.toolbox).toBe(316);
    expect(splitter.getAttribute('aria-valuenow')).toBe('316');
    fireEvent.keyDown(splitter, { key: 'End' });
    expect(state().panes.toolbox).toBe(paneLimits.toolbox.max);
    fireEvent.keyDown(splitter, { key: 'Home' });
    expect(state().panes.toolbox).toBe(paneLimits.toolbox.min);

    expect(document.querySelector<HTMLElement>('.studio')!.style.gridTemplateColumns).toBe(`${paneLimits.toolbox.min}px minmax(0, 1fr) 340px`);
    expect(JSON.parse(preferences.values.get(preferenceKeys.panes)!)).toMatchObject({ toolbox: paneLimits.toolbox.min });
  });

  it('hides and shows panels from the View group', async () => {
    await renderStudio();
    const view = screen.getByRole('toolbar', { name: 'View' });
    const properties = within(view).getByRole('button', { name: 'Properties' });

    expect(properties.getAttribute('aria-pressed')).toBe('true');
    fireEvent.click(properties);
    expect(properties.getAttribute('aria-pressed')).toBe('false');
    expect(screen.queryByRole('complementary', { name: 'Properties' })).toBeNull();
    expect(document.querySelector<HTMLElement>('.studio')!.style.gridTemplateColumns).toBe('240px minmax(0, 1fr) 0px');

    fireEvent.click(within(view).getByRole('button', { name: 'Activities' }));
    expect(document.querySelector<HTMLElement>('.sidebar')!.hidden).toBe(true);
    fireEvent.click(within(view).getByRole('button', { name: 'Bottom' }));
    expect(screen.queryByRole('tablist', { name: 'Output' })).toBeNull();

    fireEvent.click(properties);
    expect(screen.getByRole('complementary', { name: 'Properties' })).toBeTruthy();
  });

  it('ignores remembered panel settings of the wrong shape', async () => {
    const preferences = memoryPreferences();
    preferences.write(preferenceKeys.panes, { hidden: ['nonsense'], toolbox: 'wide' });
    const { state } = await renderStudio(preferences);

    expect(state().panes).toEqual({ hidden: [] });
    expect(document.querySelector<HTMLElement>('.studio')!.style.gridTemplateColumns).toBe('');
  });
});
