import { beforeEach, describe, expect, it } from 'vitest';
import { ApiError } from './api';
import { applyEvent, currentRun, inputArguments, maxEvents, maxEventsPerFrame, maxRuns, stopRefusalOf, Studio, type RunView } from './studio';
import { FakeApi, FakeEventSource, immediately, helloWorldEvents, settle } from './test-support';
import type { ExecutionEvent, JsonObject } from './types';

// A workflow without input arguments: Run starts it directly (no run dialog).
const plain = JSON.stringify({
  schemaVersion: '1.0',
  id: 'plain',
  name: 'Plain',
  version: '1.0.0',
  root: {
    id: 'main',
    type: 'Core.Sequence',
    children: [
      { id: 'first', type: 'Core.Log', properties: { message: "'one'" } },
      { id: 'second', type: 'Core.Log', properties: { message: "'two'" } },
    ],
  },
});

const typed = JSON.stringify({
  schemaVersion: '1.0',
  id: 'typed',
  name: 'Typed',
  version: '1.0.0',
  arguments: [
    { name: 'who', direction: 'In', type: 'String', required: true },
    { name: 'count', direction: 'InOut', type: 'Int', default: 3 },
    { name: 'result', direction: 'Out', type: 'String' },
  ],
  root: { id: 'main', type: 'Core.Sequence' },
});

async function open(path = 'plain.json', api = new FakeApi()) {
  api.files.set('plain.json', { text: plain, etag: 1 });
  api.files.set('typed.json', { text: typed, etag: 1 });
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately);
  await studio.connect();
  await studio.open(path);
  return { studio, api, state: () => studio.store.get(), run: () => currentRun(studio.store.get())! };
}

const base = (runId: string) => ({ runId, time: '2026-10-07T10:00:00Z', executionId: 'e1' });
const started = (runId: string, sequence = 1): ExecutionEvent => ({ ...base(runId), sequence, kind: 'execution.started', workflowId: 'plain' });
const nodeStarted = (runId: string, sequence: number, nodeId: string): ExecutionEvent => ({ ...base(runId), sequence, kind: 'node.started', nodeId, activityType: 'Core.Log' });
const nodeCompleted = (runId: string, sequence: number, nodeId: string, status = 'Succeeded'): ExecutionEvent => ({
  ...base(runId),
  sequence,
  kind: 'node.completed',
  nodeId,
  activityType: 'Core.Log',
  status,
});
const completed = (runId: string, sequence: number, status: string, error?: ExecutionEvent['error']): ExecutionEvent => ({
  ...base(runId),
  sequence,
  kind: 'execution.completed',
  workflowId: 'plain',
  status,
  error,
  durationMs: 42,
});
const log = (runId: string, sequence: number, message: string, nodeId = 'first'): ExecutionEvent => ({ ...base(runId), sequence, kind: 'log', level: 'Information', message, nodeId });
const emit = (...events: ExecutionEvent[]) => events.forEach((e) => FakeEventSource.instances.at(-1)!.emit(e));

beforeEach(() => {
  FakeEventSource.instances = [];
});

describe('Run configuration', () => {
  it('reads the In and InOut arguments with their required flags and defaults; Out arguments are not asked for', () => {
    expect(inputArguments(JSON.parse(typed) as JsonObject)).toEqual([
      { name: 'who', type: 'String', required: true, defaultJson: undefined },
      { name: 'count', type: 'Int', required: false, defaultJson: '3' },
    ]);
  });

  it('runs a workflow without input arguments right away', async () => {
    const { studio, api, state } = await open();

    await studio.requestRun();

    expect(state().runDialog).toBeUndefined();
    expect(api.runs).toHaveLength(1);
  });

  it('asks for the arguments first, sends the typed text as is, leaves blanks out, and remembers the texts', async () => {
    const { studio, api, state } = await open('typed.json');

    await studio.requestRun();
    expect(state().runDialog).toMatchObject({ path: 'typed.json', arguments: [{ name: 'who' }, { name: 'count' }] });
    expect(api.runs).toHaveLength(0);

    await studio.startFromDialog({ who: 'Ada "Lovelace"', count: '  ' }, 5000);
    expect(state().runDialog).toBeUndefined();
    expect(api.runs[0]).toMatchObject({ path: 'typed.json', argumentText: { who: 'Ada "Lovelace"' }, timeoutMs: 5000 });

    await studio.requestRun();
    expect(state().runDialog).toMatchObject({ values: { who: 'Ada "Lovelace"', count: '  ' }, timeoutMs: 5000 });
    studio.closeRunDialog();
    expect(state().runDialog).toBeUndefined();
    expect(api.runs).toHaveLength(1);
  });

  it('never sends a remembered text of an argument the workflow no longer declares', async () => {
    const { studio, api } = await open('typed.json');

    await studio.startFromDialog({ who: 'Ada', removed: 'stale', result: 'Out arguments are not inputs' }, undefined);

    expect(api.runs[0].argumentText).toEqual({ who: 'Ada' });
  });

  it('reports a run the server refused because of an argument as not started (request), never as failed', async () => {
    const api = new FakeApi();
    api.startFailure = new ApiError(400, "Argument 'count': 'many' is not a valid Int.");
    const { studio, run } = await open('typed.json', api);

    await studio.startFromDialog({ who: 'x', count: 'many' }, undefined);

    expect(run()).toMatchObject({ status: 'NotStarted', notStarted: { reason: 'request', message: "Not started: Argument 'count': 'many' is not a valid Int." } });
    expect(run().runId).toBeUndefined();
  });
});

describe('Validation before run', () => {
  it('validates first and does not start an invalid workflow', async () => {
    const api = new FakeApi();
    api.validation = { valid: false, diagnostics: [{ code: 'MYRPA1043', severity: 'Error', message: 'Syntax error.', path: '$.root', nodeId: 'main' }] };
    const { studio, state, run } = await open('plain.json', api);

    await studio.run();

    expect(api.validations).toBe(1);
    expect(api.runs).toHaveLength(0);
    expect(run()).toMatchObject({ status: 'NotStarted', notStarted: { reason: 'validation' } });
    expect(state().diagnostics).toHaveLength(1);
    expect(state().errorNodeIds.has('main')).toBe(true);
    expect(state().busy).toBeUndefined();
  });

  it('starts a valid workflow (warnings allowed), and shows Validating while the server checks it', async () => {
    const api = new FakeApi();
    api.validation = { valid: true, diagnostics: [{ code: 'MYRPA1050', severity: 'Warning', message: 'Unknown field.', path: '$.x' }] };
    const { studio, state, run } = await open('plain.json', api);

    const pending = studio.run();
    expect(run().status).toBe('Validating');
    expect(state().busy).toBe('starting');
    await pending;

    expect(api.runs).toHaveLength(1);
    expect(run()).toMatchObject({ runId: 'run-1', status: 'Starting' });
  });
});

describe('Run lifecycle', () => {
  it('goes Starting → Running → Succeeded from the server events, with start time, current node and duration', async () => {
    const { studio, run } = await open();
    await studio.run();
    await settle();

    expect(run().status).toBe('Starting');
    emit(started('run-1'), nodeStarted('run-1', 2, 'main'), nodeStarted('run-1', 3, 'first'));
    expect(run()).toMatchObject({ status: 'Running', startedAt: '2026-10-07T10:00:00Z', runningNodes: ['main', 'first'] });
    emit(log('run-1', 4, 'one'), nodeCompleted('run-1', 5, 'first'), nodeStarted('run-1', 6, 'second'));
    expect(run().runningNodes).toEqual(['main', 'second']);
    emit(nodeCompleted('run-1', 7, 'second'), nodeCompleted('run-1', 8, 'main'), completed('run-1', 9, 'Succeeded'));
    await settle();

    expect(run()).toMatchObject({ status: 'Succeeded', durationMs: 42, runningNodes: [] });
    expect(run().result?.outputs).toEqual({ greeting: 'Hello, World!' });
    expect([...run().nodeStatus]).toEqual([
      ['main', 'Succeeded'],
      ['first', 'Succeeded'],
      ['second', 'Succeeded'],
    ]);
  });

  it('reports a failure with the failed node, and the workflow stays editable', async () => {
    const { studio, state, run } = await open();
    await studio.run();
    await settle();
    const error = { code: 'MYRPA2001', message: 'Boom.', nodeId: 'second', activityType: 'Core.Log', errorType: 'Activity' };

    emit(started('run-1'), nodeStarted('run-1', 2, 'main'), nodeStarted('run-1', 3, 'second'));
    emit(nodeCompleted('run-1', 4, 'second', 'Failed'), nodeCompleted('run-1', 5, 'main', 'Failed'), completed('run-1', 6, 'Failed', error));

    expect(run()).toMatchObject({ status: 'Failed', error });
    expect(state().nodeStatus.get('second')).toBe('Failed');
    expect(state().message).toBe('Run run-1 Failed at second: Boom..');
    studio.selectNodeId('second');
    studio.editDisplayName(state().selectedKey!, 'Edited after the failure');
    expect(state().undo).toHaveLength(1);
  });

  it('keeps the engine status of timed-out and argument-rejected runs', async () => {
    const { studio, run } = await open();
    await studio.run();
    await settle();
    emit(started('run-1'), completed('run-1', 2, 'TimedOut', { code: 'MYRPA2005', message: 'Too slow.' }));
    expect(run().status).toBe('TimedOut');

    await studio.run();
    await settle();
    emit(started('run-2'), completed('run-2', 2, 'Failed', { code: 'MYRPA2004', message: "Required argument 'who' was not supplied.", errorType: 'Arguments' }));
    expect(run()).toMatchObject({ runId: 'run-2', status: 'Failed', error: { code: 'MYRPA2004' } });
  });
});

describe('Stop', () => {
  it('shows Cancelling… until the server reports Cancelled, and cannot be repeated or used after completion', async () => {
    const { studio, api, state, run } = await open();
    await studio.run();
    await settle();
    emit(started('run-1'), nodeStarted('run-1', 2, 'main'), nodeStarted('run-1', 3, 'first'));

    await studio.stop();

    expect(api.cancels).toEqual(['run-1']);
    expect(run()).toMatchObject({ status: 'Running', cancelRequested: true });
    expect(stopRefusalOf(run())).toBe('Cancelling…');
    await studio.stop();
    expect(api.cancels).toHaveLength(1);

    emit(nodeCompleted('run-1', 4, 'first', 'Cancelled'), nodeCompleted('run-1', 5, 'main', 'Cancelled'), completed('run-1', 6, 'Cancelled'));
    expect(run().status).toBe('Cancelled');
    expect(state().nodeStatus.get('first')).toBe('Cancelled');
    expect(stopRefusalOf(run())).toBe('The run already finished (Cancelled).');
  });

  it('keeps waiting for the stream when the run finished before the cancel request (409)', async () => {
    const api = new FakeApi();
    api.cancelFailure = new ApiError(409, 'The run has already finished.');
    const { studio, run } = await open('plain.json', api);
    await studio.run();
    await settle();
    emit(started('run-1'));

    await studio.stop();
    expect(run()).toMatchObject({ status: 'Running', cancelRequested: true });
    emit(completed('run-1', 2, 'Succeeded'));
    expect(run().status).toBe('Succeeded');
  });

  it('clears the request when the server could not take it', async () => {
    const api = new FakeApi();
    api.cancelFailure = new ApiError(500, 'Broken.');
    const { studio, state, run } = await open('plain.json', api);
    await studio.run();
    await settle();

    await studio.stop();

    expect(run().cancelRequested).toBe(false);
    expect(state().message).toBe('Cannot stop run run-1: Broken.');
  });

  it('refuses to stop runs that never started or are not accepted yet', () => {
    const attempt = { key: 'a', project: 'demo', path: 'x.json', requestedAt: '', cancelRequested: false, events: [], nodeStatus: new Map(), runningNodes: [], missingEvents: 0 };
    expect(stopRefusalOf(undefined)).toBe('No run to stop.');
    expect(stopRefusalOf({ ...attempt, status: 'NotStarted' })).toBe('The run did not start.');
    expect(stopRefusalOf({ ...attempt, status: 'Validating' })).toBe('The run has not been accepted by the server yet.');
    expect(stopRefusalOf({ ...attempt, status: 'Starting', runId: 'r' })).toBeUndefined(); // a queued run can be cancelled
  });
});

describe('Node state and logs', () => {
  it('maps only the run’s own workflow to tree nodes; invoked workflows go to the log only', async () => {
    const { studio, run } = await open();
    await studio.run();
    await settle();

    emit(started('run-1'), { ...nodeStarted('run-1', 2, 'first'), executionId: 'child', parentExecutionId: 'e1' });

    expect(run().nodeStatus.has('first')).toBe(false);
    expect(run().events).toHaveLength(2);
  });

  it('shows node states on the tree only for a run of the open file', async () => {
    const { studio, state } = await open();
    await studio.run();
    await settle();
    emit(started('run-1'), nodeStarted('run-1', 2, 'main'));
    expect(state()).toMatchObject({ treeShowsRun: true });
    expect(state().nodeStatus.get('main')).toBe('Running');

    await studio.open('typed.json'); // also has a node 'main'
    expect(state()).toMatchObject({ treeShowsRun: false });
    expect(state().nodeStatus.size).toBe(0);
  });

  it('keeps the latest events of a run', () => {
    let run: RunView = { key: 'k', runId: 'r', project: 'demo', path: 'p', requestedAt: '', status: 'Running', cancelRequested: false, events: [], nodeStatus: new Map(), runningNodes: [], missingEvents: 0 };
    for (let i = 1; i <= maxEvents + 5; i++) {
      run = applyEvent(run, log('r', i, `line ${i}`));
    }

    expect(run.events).toHaveLength(maxEvents);
    expect(run.events[0].sequence).toBe(6);
  });
});

describe('Multiple runs', () => {
  it('keeps concurrent runs apart on the one stream', async () => {
    const { studio, api, state } = await open();
    await studio.run();
    await studio.run();
    await settle();
    const [first, second] = [...state().runs].reverse();

    emit(started('run-1'), started('run-2'), log('run-2', 2, 'two'), log('run-1', 2, 'one'), nodeStarted('run-2', 3, 'second'));
    emit(completed('run-1', 3, 'Succeeded'), { ...completed('run-2', 4, 'Failed', { code: 'MYRPA2001', message: 'x', nodeId: 'second' }) });

    const byKey = (key: string) => state().runs.find((r) => r.key === key)!;
    expect(byKey(first.key)).toMatchObject({ runId: 'run-1', status: 'Succeeded' });
    expect(byKey(second.key)).toMatchObject({ runId: 'run-2', status: 'Failed' });
    expect(byKey(first.key).events.filter((e) => e.kind === 'log').map((e) => e.message)).toEqual(['one']);
    expect(byKey(second.key).events.filter((e) => e.kind === 'log').map((e) => e.message)).toEqual(['two']);
    expect(byKey(first.key).nodeStatus.has('second')).toBe(false);
    expect(api.streamsCreated).toBe(1);
    expect(FakeEventSource.instances).toHaveLength(1);

    expect(state().currentRunKey).toBe(second.key);
    expect(state().nodeStatus.get('second')).toBe('Running');
    studio.selectRun(first.key);
    expect(currentRun(state())?.runId).toBe('run-1');
    expect(state().nodeStatus.has('second')).toBe(false);
  });

  it(`keeps at most ${maxRuns} runs, dropping the oldest finished ones but never an unfinished one`, async () => {
    const { studio, state } = await open();
    await studio.run(); // run-1 stays unfinished
    await settle();
    for (let i = 2; i <= maxRuns + 3; i++) {
      await studio.run();
      await settle();
      emit(started(`run-${i}`), completed(`run-${i}`, 2, 'Succeeded'));
    }

    expect(state().runs).toHaveLength(maxRuns);
    expect(state().runs.some((r) => r.runId === 'run-1')).toBe(true);
    expect(state().runs.some((r) => r.runId === 'run-2')).toBe(false);
  });
});

describe('Event batching', () => {
  it('applies a burst of events in one store update when the frame comes, in stream order, per run', async () => {
    const api = new FakeApi();
    api.files.set('plain.json', { text: plain, etag: 1 });
    const frames: (() => void)[] = [];
    const studio = new Studio(api, (url) => new FakeEventSource(url), (flush) => frames.push(flush));
    await studio.connect();
    await studio.open('plain.json');
    await studio.run();
    await settle();
    let updates = 0;
    studio.store.subscribe(() => updates++);

    emit(started('run-1'), nodeStarted('run-1', 2, 'main'), nodeStarted('run-1', 3, 'first'), log('run-1', 4, 'one'), nodeCompleted('run-1', 5, 'first'));
    expect(frames).toHaveLength(1); // one flush scheduled for the whole burst
    expect(currentRun(studio.store.get())!.events).toHaveLength(0);

    frames.shift()!();
    const run = currentRun(studio.store.get())!;
    expect(updates).toBe(1);
    expect(run.events.map((e) => e.sequence)).toEqual([1, 2, 3, 4, 5]);
    expect(run).toMatchObject({ status: 'Running', runningNodes: ['main'] });
    expect(run.nodeStatus.get('first')).toBe('Succeeded');

    emit(completed('run-1', 6, 'Succeeded'));
    expect(frames).toHaveLength(1);
    frames.shift()!();
    expect(currentRun(studio.store.get())!.status).toBe('Succeeded');
  });

  it(`spreads a burst over frames, at most ${maxEventsPerFrame} events each, without losing or reordering any`, async () => {
    const api = new FakeApi();
    api.files.set('plain.json', { text: plain, etag: 1 });
    const frames: (() => void)[] = [];
    const studio = new Studio(api, (url) => new FakeEventSource(url), (flush) => frames.push(flush));
    await studio.connect();
    await studio.open('plain.json');
    await studio.run();
    await settle();
    const total = maxEventsPerFrame * 2 + 10;

    emit(started('run-1'), ...Array.from({ length: total - 1 }, (_, i) => log('run-1', i + 2, `line ${i}`)));
    const applied: number[] = [];
    while (frames.length > 0) {
      frames.shift()!();
      applied.push(currentRun(studio.store.get())!.events.length);
    }

    expect(applied).toEqual([maxEventsPerFrame, maxEventsPerFrame * 2, total]);
    expect(currentRun(studio.store.get())!.events.map((e) => e.sequence)).toEqual(Array.from({ length: total }, (_, i) => i + 1));
  });
});

describe('Reconnect', () => {
  it('re-creates a lost stream, resumes each unfinished run after its last sequence, ignores replays and shows gaps', async () => {
    const { studio, api, state, run } = await open();
    await studio.run();
    await studio.run();
    await settle();
    const events = helloWorldEvents('run-1');
    emit(...events.slice(0, 3), started('run-2'), completed('run-2', 2, 'Succeeded'));

    FakeEventSource.instances[0].emit({ kind: 'stream.opened' } as unknown as ExecutionEvent);
    expect(state().stream).toBe('connected');
    FakeEventSource.instances[0].fail();
    expect(state().stream).toBe('reconnecting');
    await settle();

    expect(api.streamsCreated).toBe(2);
    expect(api.subscriptions.slice(2)).toEqual([{ streamId: 'stream-2', runId: 'run-1', afterSequence: 3 }]); // run-2 finished
    emit(events[1], events[2]); // replayed by the server: ignored
    emit({ ...base('run-1'), sequence: 0, kind: 'stream.gap', missingFromSequence: 4, missingToSequence: 5 });
    emit(...events.slice(5));

    studio.selectRun(state().runs.find((r) => r.runId === 'run-1')!.key);
    expect(run().events.map((e) => e.sequence)).toEqual([1, 2, 3, 0, 6, 7]);
    expect(run()).toMatchObject({ status: 'Succeeded', missingEvents: 2 });
  });
});
