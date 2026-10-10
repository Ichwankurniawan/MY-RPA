import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { memoryPreferences } from './preferences';
import { Studio } from './studio';
import { catalog, FakeApi, FakeEventSource, helloWorld, immediately, settle } from './test-support';
import type { ActivityDescriptor } from './types';

// ADR-0044: the Laconi brand, the navigation rail, the Home page (real data only) and catalog-driven icons.

const http: ActivityDescriptor = {
  type: 'Http.Request',
  displayName: 'HTTP Request',
  category: 'HTTP',
  description: 'Sends one HTTP request.',
  allowsChildren: false,
  properties: [],
  slots: [],
  sideEffects: ['Network'],
};

async function renderStudio(api = new FakeApi()) {
  api.files.set('flows/second.json', { text: helloWorld, etag: 1 });
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { preferences: memoryPreferences(), validateDelayMs: undefined });
  render(<App studio={studio} />);
  await act(settle);
  return { studio, api };
}

function withPlugin(): FakeApi {
  const api = new FakeApi();
  api.activities = async () => [...catalog, http];
  return api;
}

beforeEach(() => {
  FakeEventSource.instances = [];
});

afterEach(cleanup);

describe('Laconi shell', () => {
  it('names the product Laconi Studio in the header and the page title', async () => {
    await renderStudio();

    expect(screen.getByRole('heading', { level: 1, name: 'Laconi Studio' })).toBeTruthy();
    expect(document.title).toBe('Laconi Studio');
  });

  it('opens the workspace of the only project; Projects in the title bar lists the projects; the rail switches to Home and back', async () => {
    await renderStudio();
    const rail = within(screen.getByRole('navigation', { name: 'Main' }));

    // One project: no choice to make, so its workspace (the tree starts at the project).
    expect(rail.getByRole('button', { name: 'Workflows' }).getAttribute('aria-current')).toBe('page');
    expect(screen.queryByRole('main', { name: 'Projects' })).toBeNull();
    expect(screen.getByRole('list', { name: 'Activity catalog' })).toBeTruthy();
    expect(screen.getByRole('tree', { name: 'Workflow files', hidden: true }).querySelector('.project-root .tree-name')!.textContent).toBe('demo');
    const location = within(screen.getByRole('navigation', { name: 'Location' }));
    expect(location.getByTestId('current-project').textContent).toBe('demo');

    // The title bar's Projects link shows the projects; choosing one comes back to its workspace.
    await act(async () => fireEvent.click(location.getByRole('button', { name: 'Projects' })));
    const card = within(screen.getByRole('main', { name: 'Projects' })).getByRole('button', { name: 'Open project demo' });
    expect(document.getElementById(card.getAttribute('aria-describedby')!)!.textContent).toMatch(/^2 workflows · 1 folder · changed /);
    await act(async () => fireEvent.click(card));
    expect(screen.getByRole('tab', { name: 'Files' }).getAttribute('aria-selected')).toBe('true');

    fireEvent.click(rail.getByRole('button', { name: 'Home' }));
    expect(rail.getByRole('button', { name: 'Home' }).getAttribute('aria-current')).toBe('page');
    expect(screen.getByRole('main', { name: 'Laconi Studio' })).toBeTruthy();
    expect(screen.queryByRole('tree', { name: 'Workflow files' })).toBeNull();

    // Back on Workflows, the chosen project's workspace is where it was.
    fireEvent.click(rail.getByRole('button', { name: 'Workflows' }));
    expect(screen.getByRole('tree', { name: 'Workflow files' })).toBeTruthy();
  });

  it('lists the real project workflows and activities on Home, and opens a workflow in the designer', async () => {
    await renderStudio(withPlugin());
    fireEvent.click(screen.getByRole('button', { name: 'Home' }));
    const home = within(screen.getByRole('main', { name: 'Laconi Studio' }));

    expect(home.getByText(/no runs yet/i)).toBeTruthy();
    expect(home.getByText(`${catalog.length} built-in, 1 from plugins.`, { exact: false })).toBeTruthy();

    await act(async () => fireEvent.click(home.getByRole('button', { name: 'Open flows/second.json' })));
    await act(settle);

    expect(screen.getByTestId('document-title').textContent).toBe('flows/second.json');
    expect(screen.getByRole('button', { name: 'Workflows' }).getAttribute('aria-current')).toBe('page');
  });

  it('reopens the project used last in this browser when there are several', async () => {
    const api = new FakeApi();
    const info = api.info.bind(api);
    api.info = async () => ({ ...(await info()), projects: ['other', 'demo'] });
    const preferences = memoryPreferences();
    preferences.write('myrpa.ui.lastProject', 'demo');
    const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { preferences, validateDelayMs: undefined });
    render(<App studio={studio} />);
    await act(settle);

    expect(screen.queryByRole('main', { name: 'Projects' })).toBeNull();
    expect(screen.getByTestId('current-project').textContent).toBe('demo');

    // Entering another project makes it the one reopened next time.
    await act(async () => studio.enterProject('other'));
    expect(preferences.values.get('myrpa.ui.lastProject')).toBe('"other"');
  });

  it('lists every project with what it holds, says why one cannot be listed, and opens the one chosen', async () => {
    const api = new FakeApi();
    const info = api.info.bind(api);
    const listing = api.workflows.bind(api);
    api.info = async () => ({ ...(await info()), projects: ['demo', 'empty', 'broken'] });
    // The Studio passes the project's name; the fake ignores it, this override does not.
    api.workflows = (async (project: string) => {
      if (project === 'broken') {
        throw new Error('the folder is gone');
      }

      return project === 'empty' ? { workflows: [], folders: [] } : listing();
    }) as unknown as typeof api.workflows;
    await renderStudio(api);
    const projects = within(screen.getByRole('list', { name: 'Projects' }));
    const details = (name: string) => document.getElementById(projects.getByRole('button', { name: `Open project ${name}` }).getAttribute('aria-describedby')!)!.textContent;

    expect(projects.getAllByRole('button').map((b) => b.getAttribute('aria-label'))).toEqual(['Open project demo', 'Open project empty', 'Open project broken']);
    expect(details('empty')).toBe('0 workflows · 0 folders · named with --project');
    expect(details('broken')).toBe('Cannot list it: the folder is gone · named with --project');

    await act(async () => fireEvent.click(projects.getByRole('button', { name: 'Open project empty' })));
    expect(screen.getByTestId('current-project').textContent).toBe('empty');
    expect([...screen.getByRole('tree', { name: 'Workflow files' }).querySelectorAll('.tree-name')].map((n) => n.textContent)).toEqual(['empty']);
  });

  it('gives a plugin activity the icon of its side effect, with no Studio change', async () => {
    const { studio } = await renderStudio(withPlugin());
    await act(async () => studio.enterProject('demo'));
    await act(async () => fireEvent.click(screen.getByRole('tab', { name: 'Activities' })));

    const insert = screen.getByRole('button', { name: 'Insert HTTP Request (Http.Request)' });
    expect(insert.querySelector('.icon-globe')).not.toBeNull();
    expect(insert.textContent).toBe('HTTP Request');
  });
});
