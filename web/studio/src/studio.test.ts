import { beforeEach, describe, expect, it } from 'vitest';
import { indexDocument } from './document';
import { isDirty, Studio } from './studio';
import { FakeApi, FakeEventSource, catalog, helloWorldEvents, settle } from './test-support';
import type { JsonObject } from './types';

const message = catalog.find((a) => a.type === 'Core.Log')!.properties[0];

async function openHelloWorld(api = new FakeApi()) {
  const studio = new Studio(api, (url) => new FakeEventSource(url));
  await studio.connect();
  await studio.open('hello-world.json');
  return { studio, api, state: () => studio.store.get() };
}

const logKey = (studio: Studio) => indexDocument(studio.store.get().document!).byNodeId.get('log-greeting')!;
const logMessage = (document: JsonObject | undefined) => ((document?.root as JsonObject).children as JsonObject[])[1].properties;

beforeEach(() => {
  FakeEventSource.instances = [];
});

describe('Studio', () => {
  it('connects, lists the project, opens a workflow clean with the root selected', async () => {
    const { studio, state } = await openHelloWorld();

    expect(state().connection).toBe('ready');
    expect(state().project).toBe('demo');
    expect(state().file).toMatchObject({ project: 'demo', path: 'hello-world.json', etag: '"1"' });
    expect(isDirty(state())).toBe(false);
    expect(state().selectedKey).toBe(indexDocument(state().document!).byNodeId.get('main'));
    studio.select(logKey(studio));
    expect(state().selectedKey).toBe(logKey(studio));
  });

  it('reports a missing session instead of failing', async () => {
    const api = new FakeApi();
    api.signedIn = false;
    const studio = new Studio(api, (url) => new FakeEventSource(url));

    await studio.connect();

    expect(studio.store.get().connection).toBe('signed-out');
  });

  it('edits a property into the document and marks it dirty', async () => {
    const { studio, state } = await openHelloWorld();
    const saved = state().saved;

    studio.editProperty(logKey(studio), message, "greeting + '!'");

    expect(logMessage(state().document)).toEqual({ message: "greeting + '!'" });
    expect(state().saved).toBe(saved);
    expect(isDirty(state())).toBe(true);
  });

  it('saves through the server with If-Match, then is clean and uses the new ETag next time', async () => {
    const { studio, api, state } = await openHelloWorld();
    studio.editProperty(logKey(studio), message, "'one'");

    await studio.save();
    studio.editProperty(logKey(studio), message, "'two'");
    await studio.save();

    expect(api.saves.map((s) => s.etag)).toEqual(['"1"', '"2"']);
    expect(JSON.parse(api.files.get('hello-world.json')!.text).root.children[1].properties.message).toBe("'two'");
    expect(state().file?.etag).toBe('"3"');
    expect(isDirty(state())).toBe(false);
  });

  it('keeps the edits and reports a conflict when the file changed on disk (412)', async () => {
    const { studio, api, state } = await openHelloWorld();
    api.files.get('hello-world.json')!.etag = 9;
    studio.editProperty(logKey(studio), message, "'mine'");

    await studio.save();

    expect(isDirty(state())).toBe(true);
    expect(state().message).toContain('changed on disk');
  });

  it('shows the server validation result and marks the nodes with errors', async () => {
    const { studio, api, state } = await openHelloWorld();
    api.validation = {
      valid: false,
      diagnostics: [{ code: 'MYRPA1043', severity: 'Error', message: 'Syntax error.', path: '$.root.children[1].properties.message', nodeId: 'log-greeting' }],
    };

    await studio.validate();

    expect(state().diagnostics).toHaveLength(1);
    expect(state().errorNodeIds.has('log-greeting')).toBe(true);
    expect(state().message).toBe('1 error(s) found.');
  });

  it('runs the saved file and follows the run on the tab stream until it completes', async () => {
    const { studio, api, state } = await openHelloWorld();

    await studio.run();
    await settle();
    const source = FakeEventSource.instances[0];
    expect(api.runs).toEqual([{ path: 'hello-world.json', document: undefined }]);
    expect(source.url).toBe('/api/streams/stream-1');
    expect(api.subscriptions).toEqual([{ streamId: 'stream-1', runId: 'run-1', afterSequence: 0 }]);
    expect(state().run?.status).toBe('Running');

    const events = helloWorldEvents('run-1');
    events.slice(0, 3).forEach((e) => source.emit(e));
    expect(state().nodeStatus.get('log-greeting')).toBe('Running');
    events.slice(3).forEach((e) => source.emit(e));
    source.emit(events[4]); // a duplicate after a reconnect is ignored
    await settle();

    expect(state().run).toMatchObject({ runId: 'run-1', status: 'Succeeded', result: { outputs: { greeting: 'Hello, World!' } } });
    expect(state().events.map((e) => e.kind)).toEqual(events.map((e) => e.kind));
    expect(state().nodeStatus.get('log-greeting')).toBe('Succeeded');
  });

  it('uses one stream for every run of the tab', async () => {
    const { studio, api } = await openHelloWorld();

    await studio.run();
    await studio.run();

    expect(api.streamsCreated).toBe(1);
    expect(FakeEventSource.instances).toHaveLength(1);
    expect(api.subscriptions.map((s) => s.runId)).toEqual(['run-1', 'run-2']);
  });

  it('runs an unsaved document as a buffer', async () => {
    const { studio, api } = await openHelloWorld();
    studio.editProperty(logKey(studio), message, "'unsaved'");

    await studio.run();

    expect(logMessage(api.runs[0].document)).toEqual({ message: "'unsaved'" });
  });

  it('shows the diagnostics when the server refuses to run an invalid workflow', async () => {
    const { studio, api, state } = await openHelloWorld();
    api.runRefusal = { valid: false, diagnostics: [{ code: 'MYRPA1043', severity: 'Error', message: 'Syntax error.', path: '$.root', nodeId: 'main' }] };

    await studio.run();

    expect(state().run?.status).toBe('NotStarted');
    expect(state().diagnostics?.[0].code).toBe('MYRPA1043');
  });

  it('reopens a lost stream and resumes unfinished runs after the last sequence seen', async () => {
    const { studio, api } = await openHelloWorld();
    await studio.run();
    await settle();
    helloWorldEvents('run-1').slice(0, 3).forEach((e) => FakeEventSource.instances[0].emit(e));

    FakeEventSource.instances[0].fail();
    await settle();

    expect(api.streamsCreated).toBe(2);
    expect(api.subscriptions.at(-1)).toEqual({ streamId: 'stream-2', runId: 'run-1', afterSequence: 3 });
  });

  it('opens lossy files read-only: no edits, no saves', async () => {
    const api = new FakeApi();
    api.files.set('lossy.json', { text: '{ "schemaVersion": "1.0", "root": { "id": "l", "type": "Core.Log", "properties": { "message": 1.50 } } }', etag: 1 });
    const studio = new Studio(api, (url) => new FakeEventSource(url));
    await studio.connect();
    await studio.open('lossy.json');
    const key = studio.store.get().selectedKey!;

    studio.editProperty(key, message, "'x'");
    await studio.save();

    expect(studio.store.get().file?.readOnlyReason).toBeDefined();
    expect(isDirty(studio.store.get())).toBe(false);
    expect(api.saves).toHaveLength(0);
  });
});
