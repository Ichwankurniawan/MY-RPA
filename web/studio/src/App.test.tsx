import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { Studio } from './studio';
import { FakeApi, FakeEventSource, helloWorldEvents, settle } from './test-support';

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

beforeEach(() => {
  FakeEventSource.instances = [];
});

afterEach(cleanup);

describe('App', () => {
  it('renders the workflow as a tree and shows the selected node in Properties', async () => {
    await renderStudio();

    const tree = screen.getByRole('tree');
    expect(within(tree).getAllByRole('treeitem').map((item) => item.dataset.nodeId)).toEqual(['main', 'build-greeting', 'log-greeting']);
    expect(treeItem('main').getAttribute('aria-selected')).toBe('true');

    fireEvent.click(treeItem('log-greeting'));

    expect(treeItem('log-greeting').getAttribute('aria-selected')).toBe('true');
    expect(treeItem('main').getAttribute('aria-selected')).toBe('false');
    expect(screen.getByTestId('node-type').textContent).toBe('Core.Log');
  });

  it('moves the selection with the arrow keys', async () => {
    await renderStudio();
    treeItem('main').focus();

    fireEvent.keyDown(treeItem('main'), { key: 'ArrowDown' });
    fireEvent.keyDown(treeItem('build-greeting'), { key: 'End' });

    expect(treeItem('log-greeting').getAttribute('aria-selected')).toBe('true');
    expect(document.activeElement).toBe(treeItem('log-greeting'));
  });

  it('edits a property, shows the dirty marker, and saves through the server', async () => {
    const { api } = await renderStudio();
    fireEvent.click(treeItem('log-greeting'));

    fireEvent.change(screen.getByLabelText(/^message/), { target: { value: "greeting + ' again'" } });

    expect((screen.getByLabelText(/^message/) as HTMLInputElement).value).toBe("greeting + ' again'");
    expect(screen.getByTestId('document-title').textContent).toBe('hello-world.json •');
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Save' }));
      await settle();
    });
    expect(api.saves).toHaveLength(1);
    expect(screen.getByTestId('document-title').textContent).toBe('hello-world.json');
    expect(screen.getByRole('button', { name: 'Save' })).toHaveProperty('disabled', true);
  });

  it('displays the validation result on the problems list and the property', async () => {
    const api = new FakeApi();
    api.validation = {
      valid: false,
      diagnostics: [{ code: 'MYRPA1043', severity: 'Error', message: 'Unexpected end of expression.', path: '$.root.children[1].properties.message', nodeId: 'log-greeting' }],
    };
    await renderStudio(api);

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Validate' }));
      await settle();
    });
    fireEvent.click(within(screen.getByRole('list', { name: 'Problems' })).getByRole('button'));

    expect(treeItem('log-greeting').getAttribute('aria-selected')).toBe('true');
    expect(screen.getByLabelText(/^message/).getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByText(/MYRPA1043: Unexpected end of expression\./, { selector: '.field-error' })).toBeTruthy();
    expect(screen.getByRole('status').textContent).toBe('1 error(s) found.');
  });

  it('runs and shows the status, events and logs streamed over SSE', async () => {
    await renderStudio();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Run' }));
      await settle();
    });
    expect(screen.getByTestId('run-status').textContent).toContain('Running');
    await act(async () => {
      helloWorldEvents('run-1').forEach((e) => FakeEventSource.instances[0].emit(e));
      await settle();
    });

    expect(screen.getByTestId('run-status').textContent).toContain('Succeeded');
    expect(screen.getByTestId('run-outputs').textContent).toContain('Hello, World!');
    const events = within(screen.getByRole('list', { name: 'Execution events' })).getAllByRole('listitem');
    expect(events).toHaveLength(7);
    expect(events[3].textContent).toContain('[Information] Hello, World!');
    expect(treeItem('log-greeting').textContent).toContain('Succeeded');
  });

  it('asks for the start link when the browser has no session', async () => {
    const api = new FakeApi();
    api.signedIn = false;
    render(<App studio={new Studio(api, (url) => new FakeEventSource(url))} />);
    await act(settle);

    expect(screen.getByText(/Open the start link/)).toBeTruthy();
  });
});
