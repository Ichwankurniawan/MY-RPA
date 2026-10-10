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

  it('opens on the designer; the rail switches to Home and back', async () => {
    await renderStudio();
    const rail = within(screen.getByRole('navigation', { name: 'Main' }));

    expect(rail.getByRole('button', { name: 'Workflows' }).getAttribute('aria-current')).toBe('page');
    expect(screen.getByRole('list', { name: 'Activity catalog' })).toBeTruthy();

    fireEvent.click(rail.getByRole('button', { name: 'Home' }));
    expect(rail.getByRole('button', { name: 'Home' }).getAttribute('aria-current')).toBe('page');
    expect(screen.getByRole('main', { name: 'Laconi Studio' })).toBeTruthy();
    expect(screen.queryByRole('list', { name: 'Activity catalog' })).toBeNull();

    fireEvent.click(rail.getByRole('button', { name: 'Workflows' }));
    expect(screen.getByRole('list', { name: 'Activity catalog' })).toBeTruthy();
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

  it('gives a plugin activity the icon of its side effect, with no Studio change', async () => {
    await renderStudio(withPlugin());

    const insert = screen.getByRole('button', { name: 'Insert HTTP Request (Http.Request)' });
    expect(insert.querySelector('.icon-globe')).not.toBeNull();
    expect(insert.textContent).toBe('HTTP Request');
  });
});
