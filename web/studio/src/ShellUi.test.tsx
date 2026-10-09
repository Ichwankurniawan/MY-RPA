import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { Icon, iconNames } from './icons';
import { Studio } from './studio';
import { FakeApi, FakeEventSource, immediately, settle } from './test-support';

/** The Properties panel (UX-3: the selected card has the same editors inline). */
const properties = () => within(screen.getByRole('complementary', { name: 'Properties' }));


// UX-1 (studio-ux-plan.md): the icon set, the command bar, the status bar and the theme choice.

async function renderStudio(api = new FakeApi()) {
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately);
  render(<App studio={studio} />);
  await act(settle);
  await act(async () => {
    await studio.open('hello-world.json');
  });
  return { studio, api };
}

const treeItem = (nodeId: string) => document.querySelector<HTMLElement>(`[role="treeitem"][data-node-id="${nodeId}"]`)!;

beforeEach(() => {
  FakeEventSource.instances = [];
  delete document.documentElement.dataset.theme;
});

afterEach(cleanup);

describe('Icons', () => {
  it('draws every icon of the set as a decorative SVG in the current colour, without style attributes', () => {
    render(
      <div data-testid="gallery">
        {iconNames.map((name) => (
          <Icon key={name} name={name} />
        ))}
      </div>,
    );

    const svgs = [...screen.getByTestId('gallery').querySelectorAll('svg')];
    expect(svgs).toHaveLength(iconNames.length);
    expect(iconNames.length).toBeGreaterThanOrEqual(30);
    for (const svg of svgs) {
      expect(svg.getAttribute('aria-hidden')).toBe('true');
      expect(svg.getAttribute('stroke')).toBe('currentColor');
      expect(svg.getAttribute('viewBox')).toBe('0 0 24 24');
      expect(svg.querySelector('[style]')).toBeNull();
      expect(svg.hasAttribute('style')).toBe(false);
      expect(svg.children.length).toBeGreaterThan(0);
    }
  });

  it('is an image with a name when given a label', () => {
    render(<Icon name="logo" label="Laconi" />);

    expect(screen.getByRole('img', { name: 'Laconi' })).toBeTruthy();
  });
});

describe('Command bar', () => {
  it('groups the commands as File, Edit, Run and View toolbars; labels are the accessible names', async () => {
    await renderStudio();

    const groups = screen.getAllByRole('toolbar').map((t) => t.getAttribute('aria-label'));
    expect(groups).toEqual(expect.arrayContaining(['File', 'Edit', 'Run', 'View']));
    const names = (group: string) => within(screen.getByRole('toolbar', { name: group })).getAllByRole('button').map((b) => b.textContent);
    expect(names('File')).toEqual(['New workflow…', 'Save', 'Save as…']);
    expect(names('Edit')).toEqual(['Undo', 'Redo', 'Cut', 'Copy', 'Paste', 'Delete', 'Move up', 'Move down']);
    expect(names('Run')).toEqual(['Validate', 'Run', 'Debug', 'Stop', 'Record']);
  });

  it('says why a command is disabled, and enables it when it applies', async () => {
    const { studio } = await renderStudio();
    const save = within(screen.getByRole('toolbar', { name: 'File' })).getByRole('button', { name: 'Save' }) as HTMLButtonElement;
    const undo = within(screen.getByRole('toolbar', { name: 'Edit' })).getByRole('button', { name: 'Undo' }) as HTMLButtonElement;

    expect(save.disabled).toBe(true);
    expect(undo.disabled).toBe(true);
    expect(undo.title).toBe('Nothing to undo');

    fireEvent.click(treeItem('log-greeting'));
    fireEvent.change(properties().getByLabelText(/^message/), { target: { value: "'changed'" } });

    expect(save.disabled).toBe(false);
    expect(undo.disabled).toBe(false);
    expect(undo.title).toMatch(/^Undo: .* \(Ctrl\+Z\)$/);
    expect(studio.store.get().undo).toHaveLength(1);
  });

  it('New workflow… opens the name dialog', async () => {
    await renderStudio();

    fireEvent.click(screen.getByRole('button', { name: 'New workflow…' }));

    expect(screen.getByRole('dialog')).toBeTruthy();
  });
});

describe('Status bar and title', () => {
  it('shows the connection, the file and its state, and validation', async () => {
    await renderStudio();

    expect(screen.getByText('Connected')).toBeTruthy();
    expect(screen.getByTestId('status-file').textContent).toContain('hello-world.json — Saved');
    expect(screen.getByTestId('status-validation').textContent).toContain('Not validated');

    fireEvent.click(treeItem('log-greeting'));
    fireEvent.change(properties().getByLabelText(/^message/), { target: { value: "'changed'" } });

    expect(screen.getByTestId('status-file').textContent).toContain('Unsaved changes');
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Validate' }));
      await settle();
    });
    expect(screen.getByTestId('status-validation').textContent).toContain('No problems');
  });

  it('shows the workflow name above the designer', async () => {
    await renderStudio();

    expect(document.querySelector('.workflow-name')?.textContent).toBe('Hello World');
  });
});

describe('Theme', () => {
  it('follows the system by default, and remembers a chosen theme', async () => {
    await renderStudio();
    const theme = screen.getByRole('combobox', { name: 'Theme' }) as HTMLSelectElement;

    expect(theme.value).toBe('system');
    expect(document.documentElement.dataset.theme).toBeUndefined();

    fireEvent.change(theme, { target: { value: 'dark' } });

    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(window.localStorage.getItem('myrpa.ui.theme')).toBe('dark');

    fireEvent.change(theme, { target: { value: 'system' } });

    expect(document.documentElement.dataset.theme).toBeUndefined();
  });
});
