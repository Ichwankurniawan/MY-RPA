import { beforeEach, describe, expect, it } from 'vitest';
import { memoryDrafts } from './drafts';
import { indexDocument, openWorkflow, serialize } from './document';
import { isDirty, newWorkflowText, pathRefusal, Studio } from './studio';
import { catalog, FakeApi, FakeEventSource, helloWorld, immediately } from './test-support';
import type { JsonObject } from './types';

const message = catalog.find((a) => a.type === 'Core.Log')!.properties[0];
const other = helloWorld.replace('"hello-world"', '"other"');

async function setup(api = new FakeApi(), drafts = memoryDrafts()) {
  api.files.set('other.json', { text: other, etag: 1 });
  api.files.set('flows/nested.json', { text: helloWorld, etag: 1 });
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { drafts, draftDelayMs: 60_000 });
  await studio.connect();
  await studio.open('hello-world.json');
  return { studio, api, drafts, state: () => studio.store.get() };
}

/** Edits the log message of the open hello-world document (makes it dirty). */
function edit(studio: Studio, text = "'edited'") {
  const key = indexDocument(studio.store.get().document!).byNodeId.get('log-greeting')!;
  studio.editProperty(key, message, text);
}

const logText = (document: JsonObject | undefined) => (((document?.root as JsonObject).children as JsonObject[])[1].properties as JsonObject).message;

/** The file's text as the Studio would save it. */
function serialized(text: string): string {
  const opened = openWorkflow(text);
  if (!opened.ok) {
    throw new Error(opened.error);
  }

  return serialize(opened.document);
}

beforeEach(() => {
  FakeEventSource.instances = [];
});

describe('New workflows and paths', () => {
  it('creates a valid workflow named after its file', () => {
    const opened = openWorkflow(newWorkflowText('flows/My Flow.json'));

    expect(opened.ok && opened.document).toMatchObject({ schemaVersion: '1.0', id: 'My-Flow', name: 'My Flow', version: '1.0.0', root: { id: 'main', type: 'Core.Sequence' } });
  });

  it('refuses paths the server would refuse, with a reason', () => {
    expect(pathRefusal('a.json')).toBeUndefined();
    expect(pathRefusal('sub/a.json')).toBeUndefined();
    expect(pathRefusal('a.txt')).toMatch(/\.json/);
    for (const bad of ['/a.json', '../a.json', 'a//b.json', 'a\\b.json', '.hidden/a.json']) {
      expect(pathRefusal(bad)).toMatch(/inside the project/);
    }
  });

  it('New suggests a free name, creates the file, refreshes the list and opens it', async () => {
    const { studio, api, state } = await setup();
    api.files.set('new-workflow.json', { text: helloWorld, etag: 1 });
    await studio.selectProject('demo');

    studio.startName('new');
    expect(state().dialog).toEqual({ kind: 'name', purpose: 'new', from: undefined, initial: 'new-workflow-2.json' });
    await studio.submitName('flows/created.json');

    expect(state().dialog).toBeUndefined();
    expect(state().files.map((f) => f.path)).toContain('flows/created.json');
    expect(state().file?.path).toBe('flows/created.json');
    expect(state().document).toMatchObject({ id: 'created' });
  });

  it('keeps the dialog with the reason when the path is refused or taken', async () => {
    const { studio, state } = await setup();
    studio.startName('new');

    await studio.submitName('bad.txt');
    expect(state().dialog).toMatchObject({ kind: 'name', error: 'The file name must end in .json.' });
    await studio.submitName('other.json');
    expect(state().dialog).toMatchObject({ kind: 'name', error: "'other.json' already exists." });
  });
});

describe('Unsaved changes', () => {
  it('opens directly when clean, and asks first when there are unsaved changes', async () => {
    const { studio, state } = await setup();

    await studio.requestOpen('other.json');
    expect(state().file?.path).toBe('other.json');

    edit(studio);
    await studio.requestOpen('hello-world.json');
    expect(state().dialog).toEqual({ kind: 'unsaved', path: 'other.json', next: 'hello-world.json' });
    expect(state().file?.path).toBe('other.json');
  });

  it('Cancel stays; Discard opens the other file and drops the draft; Save saves, then opens', async () => {
    const { studio, api, drafts, state } = await setup();

    edit(studio);
    await studio.requestOpen('other.json');
    await studio.resolveUnsaved('cancel');
    expect(state().file?.path).toBe('hello-world.json');
    expect(isDirty(state())).toBe(true);

    studio.writeDraft();
    expect(drafts.entries.size).toBe(1);
    await studio.requestOpen('other.json');
    await studio.resolveUnsaved('discard');
    expect(state().file?.path).toBe('other.json');
    expect(drafts.entries.size).toBe(0);
    expect(api.files.get('hello-world.json')!.text).toBe(helloWorld);

    await studio.open('hello-world.json');
    edit(studio, "'saved first'");
    await studio.requestOpen('other.json');
    await studio.resolveUnsaved('save');
    expect(api.files.get('hello-world.json')!.text).toContain("'saved first'");
    expect(state().file?.path).toBe('other.json');
  });

  it('stays on the file when saving finds a conflict', async () => {
    const { studio, api, state } = await setup();
    edit(studio);
    api.files.get('hello-world.json')!.etag = 7; // changed on disk

    await studio.requestOpen('other.json');
    await studio.resolveUnsaved('save');

    expect(state().file?.path).toBe('hello-world.json');
    expect(state().dialog).toEqual({ kind: 'conflict', path: 'hello-world.json' });
  });
});

describe('Rename, Save as and Delete', () => {
  it('renames the open file (unsaved edits stay) and any other file', async () => {
    const { studio, api, state } = await setup();
    edit(studio);

    studio.startName('rename');
    expect(state().dialog).toMatchObject({ purpose: 'rename', from: 'hello-world.json', initial: 'hello-world.json' });
    await studio.submitName('renamed/hello.json');

    expect(state().file).toMatchObject({ path: 'renamed/hello.json', etag: '"1"' });
    expect(isDirty(state())).toBe(true);
    expect(api.files.has('hello-world.json')).toBe(false);
    expect(state().files.map((f) => f.path)).toContain('renamed/hello.json');

    studio.startName('rename', 'other.json');
    await studio.submitName('other-2.json');
    expect(api.files.has('other-2.json')).toBe(true);
    expect(state().file?.path).toBe('renamed/hello.json');
  });

  it('refuses to rename over an existing file or a file changed on disk', async () => {
    const { studio, api, state } = await setup();
    studio.startName('rename');
    await studio.submitName('other.json');
    expect(state().dialog).toMatchObject({ error: "'other.json' already exists." });

    api.files.get('hello-world.json')!.etag = 9;
    await studio.submitName('elsewhere.json');
    expect(state().dialog).toMatchObject({ error: "'hello-world.json' changed on disk since it was read; reopen it first." });
  });

  it('Save as writes the document to a new file, which becomes the open (clean) file; the old file is unchanged', async () => {
    const { studio, api, state } = await setup();
    edit(studio, "'copy'");

    studio.startName('save-as');
    expect(state().dialog).toMatchObject({ purpose: 'save-as', initial: 'hello-world-copy.json' });
    await studio.submitName('copy.json');

    expect(state().file?.path).toBe('copy.json');
    expect(isDirty(state())).toBe(false);
    expect(api.files.get('copy.json')!.text).toContain("'copy'");
    expect(api.files.get('hello-world.json')!.text).toBe(helloWorld);
  });

  it('deletes after confirmation; deleting the open file closes it', async () => {
    const { studio, api, state } = await setup();

    studio.startDelete('other.json');
    expect(state().dialog).toEqual({ kind: 'delete', path: 'other.json', dirty: false });
    await studio.confirmDelete();
    expect(api.files.has('other.json')).toBe(false);
    expect(state().file?.path).toBe('hello-world.json');

    edit(studio);
    studio.startDelete('hello-world.json');
    expect(state().dialog).toMatchObject({ dirty: true });
    await studio.confirmDelete();
    expect(state()).toMatchObject({ file: undefined, document: undefined, undo: [] });
    expect(state().files.map((f) => f.path)).toEqual(['flows/nested.json']);
  });

  it('does not delete a file that changed on disk since it was read', async () => {
    const { studio, api, state } = await setup();
    api.files.get('hello-world.json')!.etag = 3;

    studio.startDelete('hello-world.json');
    await studio.confirmDelete();

    expect(api.files.has('hello-world.json')).toBe(true);
    expect(state().message).toBe('Not deleted: hello-world.json changed on disk since it was read.');
  });
});

describe('Save conflicts', () => {
  async function conflicted() {
    const context = await setup();
    edit(context.studio, "'mine'");
    context.api.files.get('hello-world.json')!.text = helloWorld.replace('"message": "greeting"', '"message": "theirs"');
    context.api.files.get('hello-world.json')!.etag = 5;
    await context.studio.save();
    expect(context.state().dialog).toEqual({ kind: 'conflict', path: 'hello-world.json' });
    return context;
  }

  it('Overwrite saves this version over the newer one on disk', async () => {
    const { studio, api, state } = await conflicted();

    await studio.resolveConflict('overwrite');

    expect(api.files.get('hello-world.json')!.text).toContain("'mine'");
    expect(isDirty(state())).toBe(false);
  });

  it('Reload discards this version and opens the disk version', async () => {
    const { studio, state } = await conflicted();

    await studio.resolveConflict('reload');

    expect(isDirty(state())).toBe(false);
    expect(logText(state().document)).toBe('theirs');
  });

  it('Save as keeps both versions', async () => {
    const { studio, api, state } = await conflicted();

    await studio.resolveConflict('save-as');
    expect(state().dialog).toMatchObject({ kind: 'name', purpose: 'save-as' });
    await studio.submitName('mine.json');

    expect(api.files.get('mine.json')!.text).toContain("'mine'");
    expect(api.files.get('hello-world.json')!.text).not.toContain("'mine'");
  });

  it('Overwrite writes the file again if it was deleted on disk', async () => {
    const { studio, api } = await conflicted();
    api.files.delete('hello-world.json');

    await studio.resolveConflict('overwrite');

    expect(api.files.get('hello-world.json')!.text).toContain("'mine'");
  });
});

describe('Crash recovery', () => {
  it('keeps a draft of unsaved edits and offers it when the file is opened again (as after a crash)', async () => {
    const drafts = memoryDrafts();
    const first = await setup(new FakeApi(), drafts);
    edit(first.studio, "'unsaved work'");
    first.studio.writeDraft();
    expect(drafts.entries.size).toBe(1);

    const { studio, state } = await setup(first.api, drafts); // a new tab on the same server
    expect(state().dialog).toMatchObject({ kind: 'recover', path: 'hello-world.json', stale: false });
    expect(isDirty(state())).toBe(false);

    studio.resolveRecovery('restore');
    expect(logText(state().document)).toBe("'unsaved work'");
    expect(isDirty(state())).toBe(true);
    studio.undo();
    expect(isDirty(state())).toBe(false); // Undo returns to the file as on disk
  });

  it('marks a draft whose file changed on disk since, and discards on request', async () => {
    const drafts = memoryDrafts();
    const first = await setup(new FakeApi(), drafts);
    edit(first.studio);
    first.studio.writeDraft();
    first.api.files.get('hello-world.json')!.etag = 4;

    const { studio, state } = await setup(first.api, drafts);
    expect(state().dialog).toMatchObject({ kind: 'recover', stale: true });
    studio.resolveRecovery('discard');

    expect(drafts.entries.size).toBe(0);
    expect(state().dialog).toBeUndefined();
  });

  it('drops a draft that holds exactly what is on disk, and removes the draft when saving', async () => {
    const drafts = memoryDrafts();
    const { studio, state } = await setup(new FakeApi(), drafts);
    edit(studio);
    studio.writeDraft();
    await studio.save();
    expect(drafts.entries.size).toBe(0);

    drafts.put('demo', 'other.json', { text: serialized(other), etag: '"1"', savedAt: new Date().toISOString() });
    await studio.open('other.json');
    expect(state().dialog).toBeUndefined();
    expect(drafts.entries.size).toBe(0);
  });

  it('writes the draft once typing pauses', async () => {
    const drafts = memoryDrafts();
    const api = new FakeApi();
    const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { drafts, draftDelayMs: 5 });
    await studio.connect();
    await studio.open('hello-world.json');

    edit(studio);
    expect(drafts.entries.size).toBe(0);
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(drafts.entries.size).toBe(1);
  });
});

describe('Opening a workflow named on the command line', () => {
  it('opens the server’s --open workflow after connecting', async () => {
    const api = new FakeApi();
    api.files.set('flows/nested.json', { text: helloWorld, etag: 1 });
    api.openOnStart = { project: 'demo', path: 'flows/nested.json' };
    const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { drafts: memoryDrafts() });

    await studio.connect();

    expect(studio.store.get().file?.path).toBe('flows/nested.json');
  });
});
