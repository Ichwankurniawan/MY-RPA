import { beforeEach, describe, expect, it } from 'vitest';
import { memoryDrafts } from './drafts';
import { indexDocument, serialize } from './document';
import { isDirty, Studio, workflowKey } from './studio';
import { FakeApi, FakeEventSource, immediately } from './test-support';
import type { JsonObject } from './types';
import {
  addRow,
  assignableNames,
  diagnosticTarget,
  freeName,
  parseJsonText,
  removeRow,
  rows,
  setMetadata,
  setNodeId,
  setPropertyValue,
  updateRow,
} from './workflowData';

const workflow: JsonObject = {
  schemaVersion: '1.0',
  id: 'w',
  name: 'W',
  version: '1.0.0',
  custom: { kept: true },
  arguments: [
    { name: 'who', direction: 'In', type: 'String', required: true, note: 'unknown fields stay' },
    { name: 'result', direction: 'Out', type: 'String' },
  ],
  variables: [{ name: 'message', type: 'String', default: 'hi' }],
  root: { id: 'main', type: 'Core.Sequence', children: [{ id: 'log-1', type: 'Core.Log', properties: { message: 500 } }] },
};

describe('Workflow data edits', () => {
  it('edits metadata; a blank description is removed, other fields keep what was typed', () => {
    const named = setMetadata(workflow, 'name', 'Greeter');
    expect(named).toMatchObject({ name: 'Greeter', custom: { kept: true } });
    expect(setMetadata(named, 'description', 'Says hello')).toMatchObject({ description: 'Says hello' });
    expect('description' in setMetadata(setMetadata(named, 'description', 'x'), 'description', '  ')).toBe(false);
    expect(setMetadata(workflow, 'version', '')).toMatchObject({ version: '' });
    expect(setMetadata(workflow, 'id', 'w')).toBe(workflow); // unchanged: the same object (no undo step)
  });

  it('adds rows with free names shared by arguments and variables, updates and removes them, keeping unknown fields', () => {
    expect(freeName(workflow, 'variable')).toBe('variable1');
    const added = addRow(addRow(workflow, 'variables'), 'arguments');
    expect(rows(added, 'variables').at(-1)).toEqual({ name: 'variable1', type: 'String' });
    expect(rows(added, 'arguments').at(-1)).toEqual({ name: 'argument1', direction: 'In', type: 'String' });

    const typed = updateRow(workflow, 'arguments', 0, { type: 'Int', default: 3 });
    expect(rows(typed, 'arguments')[0]).toEqual({ name: 'who', direction: 'In', type: 'Int', required: true, note: 'unknown fields stay', default: 3 });
    expect(updateRow(workflow, 'arguments', 0, { type: 'String' })).toBe(workflow);
    expect(rows(removeRow(workflow, 'variables', 0), 'variables')).toEqual([]);
  });

  it('removes required and default when an argument becomes Out (MYRPA1065 is never created by the editor)', () => {
    const out = updateRow(updateRow(workflow, 'arguments', 0, { default: 'x' }), 'arguments', 0, { direction: 'Out' });
    expect(rows(out, 'arguments')[0]).toEqual({ name: 'who', direction: 'Out', type: 'String', note: 'unknown fields stay' });
  });

  it('parses defaults as JSON: blank is no default, invalid JSON is refused with the reason', () => {
    expect(parseJsonText('')).toEqual({ ok: true, value: undefined });
    expect(parseJsonText('"World"')).toEqual({ ok: true, value: 'World' });
    expect(parseJsonText('[1, 2]')).toEqual({ ok: true, value: [1, 2] });
    expect(parseJsonText('World')).toMatchObject({ ok: false, error: expect.stringMatching(/^Not valid JSON/) });
  });

  it('sets node ids (trimmed) and any property value; undefined removes a property', () => {
    const key = indexDocument(workflow).byNodeId.get('log-1')!;
    const at = indexDocument(workflow).byKey.get(key)!.path;
    const renamed = setNodeId(workflow, at, '  log-greeting ');
    expect(indexDocument(renamed).byNodeId.has('log-greeting')).toBe(true);
    const mapped = setPropertyValue(workflow, at, 'level', 'Warning');
    expect(((mapped.root as JsonObject).children as JsonObject[])[0].properties).toEqual({ message: 500, level: 'Warning' });
    expect(((setPropertyValue(mapped, at, 'level', undefined).root as JsonObject).children as JsonObject[])[0].properties).toEqual({ message: 500 });
  });

  it('suggests variables and Out/InOut arguments as assignment targets', () => {
    expect(assignableNames(workflow)).toEqual(['message', 'result']);
  });

  it('locates diagnostics like the WPF DraftValidator: rows, properties (also map entries) and the workflow', () => {
    const d = (path: string, nodeId?: string) => ({ code: 'X', severity: 'Error', message: '', path, nodeId });
    expect(diagnosticTarget(d('$.arguments[1].default'), undefined)).toEqual({ kind: 'row', list: 'arguments', index: 1 });
    expect(diagnosticTarget(d('$.variables[0]'), undefined)).toEqual({ kind: 'row', list: 'variables', index: 0 });
    expect(diagnosticTarget(d('$.root.children[0].properties.message', 'log-1'), undefined)).toEqual({ kind: 'node', nodeId: 'log-1', property: 'message' });
    expect(diagnosticTarget(d('$.root.properties.arguments.who', 'call'), undefined)).toEqual({ kind: 'node', nodeId: 'call', property: 'arguments' });
    expect(diagnosticTarget(d('$.root.children[0]', 'log-1'), undefined)).toEqual({ kind: 'node', nodeId: 'log-1', property: undefined });
    expect(diagnosticTarget(d('$.id'), undefined)).toEqual({ kind: 'workflow', field: 'id' });
    expect(diagnosticTarget(d('$'), undefined)).toEqual({ kind: 'workflow', field: undefined });
  });

  it('locates node diagnostics through the validated document: invalid and duplicate ids, unknown slots, dotted slot names', () => {
    const d = (path: string, nodeId?: string) => ({ code: 'X', severity: 'Error', message: '', path, nodeId });
    const document: JsonObject = {
      schemaVersion: '1.0',
      root: {
        id: 'main',
        type: 'Core.Sequence',
        children: [
          { id: 'bad id', type: 'Core.Log' },
          { id: 'twice', type: 'Core.Log' },
          { id: 'twice', type: 'Core.Log' },
          { id: 's', type: 'Core.Switch', slots: { 'case:1.5': { id: 'a', type: 'Core.Log' }, 'case:1.5.properties': { id: 'b', type: 'Core.Log' } } },
        ],
      },
    };
    const keys = indexDocument(document).entries.map((entry) => entry.key);
    expect(diagnosticTarget(d('$.root.children[0].id'), document)).toEqual({ kind: 'node', key: keys[1], nodeId: 'bad id', property: undefined });
    expect(diagnosticTarget(d('$.root.children[2].id', 'twice'), document)).toMatchObject({ key: keys[3] });
    expect(diagnosticTarget(d('$.root.children[3].slots.case:1.5.properties.message', 'a'), document)).toMatchObject({ key: keys[5], property: 'message' });
    expect(diagnosticTarget(d('$.root.children[3].slots.case:1.5.properties.properties.message', 'b'), document)).toMatchObject({ key: keys[6], nodeId: 'b', property: 'message' });
    expect(diagnosticTarget(d('$.root.x-note'), document)).toMatchObject({ kind: 'node', key: keys[0] });
    expect(diagnosticTarget(d('$.x-note'), document)).toEqual({ kind: 'workflow', field: undefined });
  });
});

async function setup(api = new FakeApi(), validateDelayMs?: number) {
  api.files.set('w.json', { text: serialize(workflow), etag: 1 });
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { drafts: memoryDrafts(), validateDelayMs });
  await studio.connect();
  await studio.open('w.json');
  return { studio, api, state: () => studio.store.get() };
}

beforeEach(() => {
  FakeEventSource.instances = [];
});

describe('Authoring in the Studio', () => {
  it('every workflow-level edit is one undo step; typing in one field merges; undo restores the saved document', async () => {
    const { studio, state } = await setup();
    const saved = state().document;

    studio.editWorkflow('Edit workflow name', 'workflow:name', (d) => setMetadata(d, 'name', 'G'));
    studio.editWorkflow('Edit workflow name', 'workflow:name', (d) => setMetadata(d, 'name', 'Greeter'));
    studio.editWorkflow('Add variable', undefined, (d) => addRow(d, 'variables'));
    studio.editWorkflow('Edit argument type', undefined, (d) => updateRow(d, 'arguments', 0, { type: 'Int' }));
    expect(state().undo.map((u) => u.label)).toEqual(['Edit workflow name', 'Add variable', 'Edit argument type']);

    studio.undo();
    studio.undo();
    studio.undo();
    expect(state().document).toBe(saved);
    expect(isDirty(state())).toBe(false);
    studio.redo();
    expect(state().document).toMatchObject({ name: 'Greeter' });
  });

  it('edits node ids and property values through the node they belong to', async () => {
    const { studio, state } = await setup();
    const key = indexDocument(state().document!).byNodeId.get('log-1')!;

    studio.editNodeId(key, 'log-greeting');
    studio.editPropertyValue(key, 'level', 'Error');
    expect(indexDocument(state().document!).byNodeId.get('log-greeting')).toBe(key); // the client key survives an id edit
    expect(indexDocument(state().document!).byKey.get(key)!.node.properties).toEqual({ message: 500, level: 'Error' });
    expect(state().undo.map((u) => u.label)).toEqual(['Edit id', 'Edit level']);
  });

  it('validates on the server once typing pauses and never says anything about it', async () => {
    const api = new FakeApi();
    api.validation = { valid: false, diagnostics: [{ code: 'MYRPA1060', severity: 'Error', message: 'Invalid name.', path: '$.variables[0].name' }] };
    const { studio, state } = await setup(api, 5);
    const before = api.validations;
    const message = state().message;

    studio.editWorkflow('Edit variable name', 'variables:0:name', (d) => updateRow(d, 'variables', 0, { name: '1bad' }));
    await new Promise((resolve) => setTimeout(resolve, 30));

    expect(api.validations).toBeGreaterThan(before);
    expect(state().diagnostics).toHaveLength(1);
    expect(state().validated).toBe(state().document);
    expect(state().message).toBe(message);
  });

  it('goes to where a problem belongs: a node, an argument or variable row, or the workflow', async () => {
    const { studio, state } = await setup();

    studio.goToDiagnostic({ code: 'X', severity: 'Error', message: '', path: '$.root.children[0].properties.message', nodeId: 'log-1' });
    expect(state().selectedKey).toBe(indexDocument(state().document!).byNodeId.get('log-1'));
    studio.goToDiagnostic({ code: 'X', severity: 'Error', message: '', path: '$.arguments[1].type' });
    expect(state().rowFocus).toMatchObject({ list: 'arguments', index: 1, seq: 1 });
    studio.goToDiagnostic({ code: 'X', severity: 'Error', message: '', path: '$.arguments[1].type' });
    expect(state().rowFocus?.seq).toBe(2); // the same problem again still moves the focus
    studio.goToDiagnostic({ code: 'X', severity: 'Error', message: '', path: '$.version' });
    expect(state().selectedKey).toBe(workflowKey);
  });

  it('refuses edits of a read-only file', async () => {
    const api = new FakeApi();
    api.files.set('lossy.json', { text: serialize(workflow).replace('"1.0.0"', '"1.0.0", "big": 12345678901234567890'), etag: 1 });
    const { studio, state } = await setup(api);
    await studio.open('lossy.json');
    const document = state().document;

    studio.editWorkflow('Edit workflow name', undefined, (d) => setMetadata(d, 'name', 'x'));

    expect(state().file?.readOnlyReason).toBeDefined();
    expect(state().document).toBe(document);
  });
});
