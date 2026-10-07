import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { Studio } from './studio';
import { FakeApi, FakeEventSource, immediately, settle } from './test-support';
import type { ExecutionEvent } from './types';

const workflow = (args: object[]) =>
  JSON.stringify({
    schemaVersion: '1.0',
    id: 'w',
    name: 'W',
    version: '1.0.0',
    arguments: args,
    root: { id: 'main', type: 'Core.Sequence', children: [{ id: 'wait', type: 'Core.Log', properties: { message: "'x'" } }, { id: 'after', type: 'Core.Log', properties: { message: "'y'" } }] },
  });

async function renderStudio(path = 'plain.json', api = new FakeApi()) {
  api.files.set('plain.json', { text: workflow([]), etag: 1 });
  api.files.set('args.json', { text: workflow([{ name: 'who', direction: 'In', type: 'String', required: true }, { name: 'n', direction: 'In', type: 'Int', default: 1 }]), etag: 1 });
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately);
  render(<App studio={studio} />);
  await act(settle);
  fireEvent.change(screen.getByRole('combobox', { name: 'Workflow' }), { target: { value: path } });
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name: 'Open' }));
    await settle();
  });
  return { studio, api };
}

const button = (name: string) => screen.getByRole('button', { name }) as HTMLButtonElement;
const click = (name: string) =>
  act(async () => {
    fireEvent.click(button(name));
    await settle();
  });
const status = () => screen.getByTestId('run-status').textContent;
const node = (id: string) => document.querySelector<HTMLElement>(`[role="treeitem"][data-node-id="${id}"] > .node`)!;
const base = (runId: string) => ({ runId, time: '2026-10-07T10:00:00Z', executionId: 'e1' });
const emit = (...events: ExecutionEvent[]) =>
  act(async () => {
    events.forEach((e) => FakeEventSource.instances.at(-1)!.emit(e));
    await settle();
  });
const started = (runId = 'run-1'): ExecutionEvent => ({ ...base(runId), sequence: 1, kind: 'execution.started' });
const nodeStarted = (sequence: number, nodeId: string, runId = 'run-1'): ExecutionEvent => ({ ...base(runId), sequence, kind: 'node.started', nodeId });
const nodeCompleted = (sequence: number, nodeId: string, nodeStatus: string, runId = 'run-1'): ExecutionEvent => ({ ...base(runId), sequence, kind: 'node.completed', nodeId, status: nodeStatus });
const completed = (sequence: number, runStatus: string, error?: ExecutionEvent['error'], runId = 'run-1'): ExecutionEvent => ({
  ...base(runId),
  sequence,
  kind: 'execution.completed',
  status: runStatus,
  error,
  durationMs: 1500,
});

beforeEach(() => {
  FakeEventSource.instances = [];
});

afterEach(cleanup);

describe('Execution UI', () => {
  it('asks for arguments: a blank required one blocks Start with the reason; the typed text is sent', async () => {
    const { api } = await renderStudio('args.json');

    await click('Run');
    const dialog = screen.getByRole('dialog', { name: 'Run args.json' });
    expect(within(dialog).getByLabelText(/^n/).getAttribute('placeholder')).toBe('default: 1');
    expect(button('Start').disabled).toBe(true);
    expect(within(dialog).getByText('Enter the required argument(s): who.')).toBeTruthy();

    fireEvent.change(within(dialog).getByLabelText(/^who/), { target: { value: 'Ada' } });
    fireEvent.change(within(dialog).getByLabelText(/^Timeout/), { target: { value: '0' } });
    expect(button('Start').disabled).toBe(true);
    fireEvent.change(within(dialog).getByLabelText(/^Timeout/), { target: { value: '3000000000' } }); // above the server's 32-bit limit
    expect(button('Start').disabled).toBe(true);
    expect(within(dialog).getByText('The timeout must be a whole number of milliseconds from 1 to 2147483647.')).toBeTruthy();
    fireEvent.change(within(dialog).getByLabelText(/^Timeout/), { target: { value: '30000' } });
    await click('Start');

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(api.runs).toEqual([{ path: 'args.json', argumentText: { who: 'Ada' }, timeoutMs: 30000 }]);
  });

  it('closes the dialog without running on Cancel', async () => {
    const { api } = await renderStudio('args.json');

    await click('Run');
    await click('Cancel');

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(api.runs).toHaveLength(0);
  });

  it('shows the lifecycle: waiting, running with the current node and start time, then succeeded with node states', async () => {
    await renderStudio();
    expect(button('Stop').disabled).toBe(true);

    await click('Run');
    expect(status()).toContain('Waiting to start');
    expect(button('Stop').disabled).toBe(false); // a queued run can be cancelled

    await emit(started(), nodeStarted(2, 'main'), nodeStarted(3, 'wait'));
    expect(status()).toContain('Running');
    expect(screen.getByTestId('toolbar-run-status').textContent).toBe('Status: Running');
    expect(screen.getByTestId('run-current').textContent).toBe('wait');
    expect(screen.getByTestId('run-started').textContent).toBeTruthy();
    expect(screen.getByTestId('run-elapsed')).toBeTruthy();
    expect(node('wait').dataset.runStatus).toBe('Running');
    expect(node('after').dataset.runStatus).toBeUndefined(); // not executed (yet)
    expect(screen.getByRole('tree', { name: 'Workflow' }).classList.contains('shows-run')).toBe(true);

    await emit(nodeCompleted(4, 'wait', 'Succeeded'), nodeStarted(5, 'after'), nodeCompleted(6, 'after', 'Succeeded'), nodeCompleted(7, 'main', 'Succeeded'), completed(8, 'Succeeded'));
    expect(status()).toContain('Succeeded');
    expect(screen.getByTestId('run-elapsed').textContent).toBe('1.5 s');
    expect(screen.queryByTestId('run-current')).toBeNull();
    expect(node('after').textContent).toContain('Succeeded');
    expect(button('Stop').disabled).toBe(true);
    expect(button('Stop').title).toBe('The run already finished (Succeeded).');
  });

  it('stops a run: Cancelling… until the server reports Cancelled; Shift+F5 does the same', async () => {
    const { api } = await renderStudio();
    await click('Run');
    await emit(started(), nodeStarted(2, 'main'), nodeStarted(3, 'wait'));

    await act(async () => {
      fireEvent.keyDown(window, { key: 'F5', shiftKey: true });
      await settle();
    });

    expect(api.cancels).toEqual(['run-1']);
    expect(status()).toContain('Cancelling…');
    expect(button('Stop').disabled).toBe(true);
    await emit(nodeCompleted(4, 'wait', 'Cancelled'), nodeCompleted(5, 'main', 'Cancelled'), completed(6, 'Cancelled', { code: 'MYRPA2006', message: 'The workflow was cancelled.' }));
    expect(status()).toContain('Cancelled');
    expect(node('wait').textContent).toContain('Cancelled');
  });

  it('explains a failure, selects the failed node on request, and keeps the workflow editable', async () => {
    await renderStudio();
    await click('Run');
    await emit(started(), nodeStarted(2, 'main'), nodeStarted(3, 'after'));
    await emit(nodeCompleted(4, 'after', 'Failed'), completed(5, 'Failed', { code: 'MYRPA2001', message: 'Boom.', nodeId: 'after' }));

    expect(status()).toContain('Failed');
    expect(screen.getByTestId('run-error').textContent).toContain('MYRPA2001 at after: Boom.');
    expect(node('after').textContent).toContain('Failed');
    await click('Select failed node');
    expect(node('after').parentElement!.getAttribute('aria-selected')).toBe('true');
    fireEvent.change(screen.getByLabelText(/^message/), { target: { value: "'fixed'" } });
    expect(screen.getByTestId('document-title').textContent).toBe('plain.json •');
  });

  it('labels argument rejection by the engine, and validation failures as not started', async () => {
    const api = new FakeApi();
    await renderStudio('plain.json', api);
    await click('Run');
    await emit(started(), completed(2, 'Failed', { code: 'MYRPA2004', message: "Required argument 'who' was not supplied.", errorType: 'Arguments' }));
    expect(status()).toContain('Failed — arguments rejected, no activity ran');

    api.validation = { valid: false, diagnostics: [{ code: 'MYRPA1043', severity: 'Error', message: 'Syntax error.', path: '$.root', nodeId: 'main' }] };
    await click('Run');
    expect(status()).toContain('Not started — validation failed');
    expect(screen.getByTestId('run-not-started').textContent).toContain('Fix the problems listed');
    expect(api.runs).toHaveLength(1);
  });

  it('lists recent runs and shows each one’s own events', async () => {
    await renderStudio();
    await click('Run');
    await click('Run');
    await emit(started('run-1'), { ...base('run-1'), sequence: 2, kind: 'log', level: 'Information', message: 'from one' }, started('run-2'));

    const recent = screen.getByRole('combobox', { name: /Recent runs/ }) as HTMLSelectElement;
    expect(recent.options).toHaveLength(2);
    const events = () => within(screen.getByRole('list', { name: 'Execution events' })).queryAllByRole('listitem').map((li) => li.textContent);
    expect(events().join('|')).not.toContain('from one');

    fireEvent.change(recent, { target: { value: recent.options[1].value } });
    expect(screen.getByTestId('run-status').textContent).toContain('run-1');
    expect(events().join('|')).toContain('[Information] from one');
  });

  it('names the failing activity type and error kind, shows execution and correlation ids, and clears the log', async () => {
    const { api } = await renderStudio();
    await click('Run');
    await emit(started(), { ...base('run-1'), sequence: 2, kind: 'log', level: 'Information', message: 'before' });
    await emit(completed(3, 'Failed', { code: 'MYRPA2001', message: 'No such element.', nodeId: 'after', activityType: 'Browser.Click', errorType: 'ElementNotFound' }));

    expect(screen.getByTestId('run-error').textContent).toContain('MYRPA2001 at after (Browser.Click, ElementNotFound): No such element.');
    expect(screen.getByTestId('run-execution-id').textContent).toBe('e1');
    expect(screen.getByTestId('run-correlation-id').textContent).toBe('run-1');
    expect(api.runs).toHaveLength(1);

    await click('Clear log');
    expect(within(screen.getByRole('list', { name: 'Execution events' })).queryAllByRole('listitem')).toHaveLength(0);
    expect(status()).toContain('Failed'); // clearing the log does not change the run
  });

  it('reports plugins that failed to load, and lists the loaded ones', async () => {
    const api = new FakeApi();
    api.pluginReport = {
      plugins: [{ id: 'MyRPA.Samples.Demo', name: 'Demo', version: '1.0.0', sha256: 'x', activities: ['Demo.Echo', 'Demo.GetField'] }],
      diagnostics: [{ code: 'MYRPA3104', severity: 'Error', message: 'The plugin assembly could not be loaded.', pluginId: 'Vendor.Broken' }],
    };
    await renderStudio('plain.json', api);

    expect(screen.getByRole('alert').textContent).toContain('Error MYRPA3104 (Vendor.Broken): The plugin assembly could not be loaded.');
    expect(screen.getByText('Plugins: Demo 1.0.0 (2 activities)')).toBeTruthy();
  });

  it('shows a dropped stream and missing events', async () => {
    await renderStudio();
    await click('Run');
    await emit(started());

    await act(async () => {
      FakeEventSource.instances[0].fail();
    });
    expect(screen.getByTestId('stream-reconnecting')).toBeTruthy();
    await act(settle);
    await emit({ kind: 'stream.opened' } as unknown as ExecutionEvent, { ...base('run-1'), sequence: 0, kind: 'stream.gap', missingFromSequence: 2, missingToSequence: 4 });

    expect(screen.queryByTestId('stream-reconnecting')).toBeNull();
    expect(screen.getByTestId('run-gap').textContent).toContain('3 earlier event(s)');
  });
});
