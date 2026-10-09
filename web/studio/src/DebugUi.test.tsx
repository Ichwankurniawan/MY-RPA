import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { memoryPreferences, preferenceKeys } from './preferences';
import { Studio } from './studio';
import { FakeApi, FakeEventSource, immediately, settle } from './test-support';
import type { DebugState, DebugValue, ExecutionEvent } from './types';

// The debugger in the Studio (ADR-0040, D-3): breakpoints per browser and file, Debug, the debug bar, paused values.

const workflow = (args: object[] = []) =>
  JSON.stringify({
    schemaVersion: '1.0',
    id: 'w',
    name: 'W',
    version: '1.0.0',
    arguments: args,
    variables: [{ name: 'count', type: 'Int', default: 0 }],
    root: { id: 'main', type: 'Core.Sequence', children: [{ id: 'first', type: 'Core.Log', properties: { message: "'x'" } }, { id: 'second', type: 'Core.Log', properties: { message: "'y'" } }] },
  });

async function renderStudio(path = 'plain.json', preferences = memoryPreferences(), api = new FakeApi()) {
  api.files.set('plain.json', { text: workflow(), etag: 1 });
  api.files.set('args.json', { text: workflow([{ name: 'who', direction: 'In', type: 'String' }]), etag: 1 });
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { preferences, validateDelayMs: undefined });
  render(<App studio={studio} />);
  await act(settle);
  await act(async () => {
    await studio.open(path);
  });
  return { studio, api, preferences, state: () => studio.store.get() };
}

const button = (name: string) => screen.getByRole('button', { name }) as HTMLButtonElement;
const debugBar = () => within(screen.getByRole('toolbar', { name: 'Debug' }));
const card = (id: string) => document.querySelector<HTMLElement>(`[role="treeitem"][data-node-id="${id}"] > .node`)!;
const toggle = (id: string) => within(card(id)).queryByRole('button', { name: 'Breakpoint on Log' });
const select = (id: string) => act(async () => fireEvent.click(card(id)));
const key = (k: string, init: KeyboardEventInit = {}) =>
  act(async () => {
    fireEvent.keyDown(window, { key: k, ...init });
    await settle();
  });
const debug = () =>
  act(async () => {
    fireEvent.click(button('Debug'));
    await settle();
  });
const base = { runId: 'run-1', time: '2026-10-08T10:00:00Z', executionId: 'e1' };
const emit = (...events: ExecutionEvent[]) =>
  act(async () => {
    events.forEach((e) => FakeEventSource.instances.at(-1)!.emit(e));
    await settle();
  });
const started: ExecutionEvent = { ...base, sequence: 1, kind: 'execution.started' };
const paused = (sequence: number, nodeId: string, reason = 'breakpoint'): ExecutionEvent => ({ ...base, sequence, kind: 'debug.paused', nodeId, reason, workflowId: 'w' });
const resumed = (sequence: number, nodeId: string, reason = 'continue'): ExecutionEvent => ({ ...base, sequence, kind: 'debug.resumed', nodeId, reason });
const pausedAt = (nodeId: string, values: DebugValue[]): DebugState => ({
  paused: { executionId: 'e1', workflowId: 'w', nodeId, activityType: 'Core.Log', reason: 'breakpoint', values },
  breakpoints: [nodeId],
});

beforeEach(() => {
  FakeEventSource.instances = [];
});

afterEach(cleanup);

describe('Breakpoints', () => {
  it('F9 sets and removes a breakpoint on the selected activity, remembered per browser and file', async () => {
    const { preferences, state } = await renderStudio();
    await select('second');

    await key('F9');
    expect(toggle('second')!.getAttribute('aria-pressed')).toBe('true');
    expect(JSON.parse(preferences.values.get(preferenceKeys.breakpoints)!)).toEqual({ 'demo/plain.json': ['second'] });

    await select('first');
    expect(toggle('second')).not.toBeNull(); // shown while set, even when not selected
    await act(async () => fireEvent.click(toggle('second')!));
    expect(state().breakpoints).toEqual({});
    expect(toggle('second')).toBeNull();
  });

  it('starts with the breakpoints remembered for the file', async () => {
    const preferences = memoryPreferences();
    preferences.write(preferenceKeys.breakpoints, { 'demo/plain.json': ['first'] });
    await renderStudio('plain.json', preferences);

    expect(toggle('first')!.getAttribute('aria-pressed')).toBe('true');
  });

  it('ignores remembered breakpoints of the wrong shape, and refuses one on the workflow itself', async () => {
    const preferences = memoryPreferences();
    preferences.write(preferenceKeys.breakpoints, { 'demo/plain.json': ['first'], broken: 'nope' });
    const { studio, state } = await renderStudio('plain.json', preferences);

    expect(state().breakpoints).toEqual({});
    act(() => studio.toggleBreakpoint('@workflow'));
    expect(state().message).toBe('Cannot set a breakpoint: Select an activity first.');
  });
});

describe('Debugging', () => {
  it('F11 without a debug run starts one paused at the first activity; Debug runs with the file’s breakpoints', async () => {
    const { api } = await renderStudio();
    await select('second');
    await key('F9');

    await key('F11');
    await debug();

    expect(api.runs).toEqual([
      { path: 'plain.json', debug: { breakpoints: ['second'], pauseAtStart: true } },
      { path: 'plain.json', debug: { breakpoints: ['second'], pauseAtStart: false } },
    ]);
  });

  it('a workflow with arguments asks for them first, in a Debug dialog', async () => {
    const { api } = await renderStudio('args.json');

    await key('F6');
    const dialog = screen.getByRole('dialog', { name: 'Debug args.json' });
    fireEvent.change(within(dialog).getByLabelText(/^who/), { target: { value: 'Ada' } });
    await act(async () => {
      fireEvent.click(within(dialog).getByRole('button', { name: 'Start' }));
      await settle();
    });

    expect(api.runs).toEqual([{ path: 'args.json', argumentText: { who: 'Ada' }, debug: { breakpoints: [], pauseAtStart: false } }]);
  });

  it('a pause shows the paused card, the debug bar and the values in scope; the commands follow', async () => {
    const api = new FakeApi();
    api.debugStates.set('run-1', pausedAt('second', [{ name: 'count', kind: 'Variable', type: 'Int', value: 2 }]));
    const { state } = await renderStudio('plain.json', memoryPreferences(), api);
    await debug();
    await emit(started);

    expect(debugBar().getByTestId('debug-state').textContent).toBe('Debugging: running');
    expect((debugBar().getByRole('button', { name: 'Continue' }) as HTMLButtonElement).title).toBe('The run is not paused.');
    expect((debugBar().getByRole('button', { name: 'Pause' }) as HTMLButtonElement).disabled).toBe(false);

    await emit({ ...base, sequence: 2, kind: 'node.started', nodeId: 'main' }, { ...base, sequence: 3, kind: 'node.started', nodeId: 'first' }, paused(4, 'second'));

    expect(debugBar().getByTestId('debug-state').textContent).toBe('Paused before second: breakpoint');
    expect(card('second').dataset.runStatus).toBe('Paused');
    expect(state().selectedKey).toBe(document.querySelector<HTMLElement>('[role="treeitem"][data-node-id="second"]')!.dataset.key);
    expect(screen.getByTestId('run-status').textContent).toContain('Paused');
    const values = within(screen.getByRole('table', { name: 'Values in scope' }));
    expect(values.getByRole('rowheader', { name: 'count' }).closest('tr')!.textContent).toBe('countVariableInt2');

    await act(async () => {
      fireEvent.click(debugBar().getByRole('button', { name: 'Step over' }));
      await settle();
    });
    await key('F10');
    await key('F11');
    await key('F11', { shiftKey: true });
    await key('F5');
    expect(api.debugCommands.map((c) => c.command)).toEqual(['stepOver', 'stepOver', 'stepInto', 'stepOut', 'continue']);

    await emit(resumed(5, 'second'), { ...base, sequence: 6, kind: 'node.started', nodeId: 'second' });
    expect(card('second').dataset.runStatus).toBe('Running');
    expect(screen.queryByTestId('run-paused')).toBeNull();

    await emit({ ...base, sequence: 7, kind: 'execution.completed', status: 'Succeeded', durationMs: 5 });
    expect(screen.queryByRole('toolbar', { name: 'Debug' })).toBeNull();
  });

  it('values fetched for an earlier pause are not shown at a later one', async () => {
    const api = new FakeApi();
    api.debugStates.set('run-1', pausedAt('first', [{ name: 'count', kind: 'Variable', type: 'Int', value: 0 }]));
    await renderStudio('plain.json', memoryPreferences(), api);
    await debug();

    await emit(started, paused(2, 'first'), resumed(3, 'first', 'stepOver'), paused(4, 'second', 'step'));

    expect(screen.getByTestId('run-paused').textContent).toContain('Paused before second: step');
    expect(screen.queryByRole('table', { name: 'Values in scope' })).toBeNull();
  });

  it('stopping while paused ends the pause; the paused node is not left marked', async () => {
    const { api } = await renderStudio();
    await debug();
    await emit(started, paused(2, 'first'));

    await act(async () => {
      fireEvent.click(debugBar().getByRole('button', { name: 'Stop debugging' }));
      await settle();
    });
    await emit({ ...base, sequence: 3, kind: 'execution.completed', status: 'Cancelled', durationMs: 5 });

    expect(api.cancels).toEqual(['run-1']);
    expect(card('first').dataset.runStatus).toBeUndefined();
    expect(screen.getByTestId('run-status').textContent).toContain('Cancelled');
  });

  it('breakpoints changed during a debug run of the file are sent to it', async () => {
    const { api } = await renderStudio();
    await debug();
    await emit(started);

    await select('first');
    await key('F9');

    expect(api.breakpointUpdates).toEqual([{ runId: 'run-1', breakpoints: ['first'] }]);
  });

  it('explains why a command is not available outside a debug run', async () => {
    const { state } = await renderStudio();

    await key('F10');

    expect(state().message).toBe('Cannot step over: Start a debug run first (Debug, F6).');
    expect(screen.queryByRole('toolbar', { name: 'Debug' })).toBeNull();
  });
});
