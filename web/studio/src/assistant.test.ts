import { describe, expect, it } from 'vitest';
import { compareWorkflows, sideEffectWords } from './assistant';
import type { JsonObject } from './types';

const base: JsonObject = {
  schemaVersion: '1.0',
  id: 'w',
  name: 'W',
  version: '1',
  arguments: [{ name: 'who', direction: 'In', type: 'String' }],
  root: {
    id: 'main',
    type: 'Core.Sequence',
    children: [
      { id: 'say', type: 'Core.Log', properties: { message: "'hi'", level: 'Information' } },
      { id: 'check', type: 'Core.If', properties: { condition: 'true' }, slots: { then: { id: 'inner', type: 'Core.Log', properties: { message: "'x'" } } } },
    ],
  },
};

describe('compareWorkflows (the review diff, by node id)', () => {
  it('reports no changes for the same content in another key order', () => {
    const same: JsonObject = {
      ...base,
      root: {
        id: 'main',
        type: 'Core.Sequence',
        children: [{ type: 'Core.Log', id: 'say', properties: { level: 'Information', message: "'hi'" } }, (base.root as { children: JsonObject[] }).children[1]],
      },
    };

    expect(compareWorkflows(base, same)).toEqual({ nodes: [], workflow: [] });
  });

  it('names added, removed, changed and moved activities, and workflow-level changes', () => {
    const after: JsonObject = {
      ...base,
      name: 'Renamed',
      arguments: [{ name: 'who', direction: 'In', type: 'String', default: 'World' }, { name: 'count', direction: 'Out', type: 'Int' }],
      root: {
        id: 'main',
        type: 'Core.Sequence',
        children: [
          { id: 'say', type: 'Core.Log', properties: { message: "'hello'", level: 'Information' } },
          { id: 'inner', type: 'Core.Log', properties: { message: "'x'" } },
          { id: 'new-one', type: 'Core.Delay', properties: { milliseconds: 10 } },
        ],
      },
    };

    const changes = compareWorkflows(base, after);

    expect(changes.nodes).toEqual([
      { kind: 'changed', id: 'say', type: 'Core.Log', detail: 'properties message' },
      { kind: 'moved', id: 'inner', type: 'Core.Log', detail: 'into main' },
      { kind: 'added', id: 'new-one', type: 'Core.Delay' },
      { kind: 'removed', id: 'check', type: 'Core.If' },
    ]);
    expect(changes.workflow).toEqual(['name changed', 'argument who changed', 'argument count added']);
  });

  it('lists every activity of a new workflow as added', () => {
    expect(compareWorkflows(undefined, base).nodes.map((n) => `${n.kind} ${n.id}`)).toEqual(['added main', 'added say', 'added check', 'added inner']);
  });

  it('says side effects in plain words', () => {
    expect(sideEffectWords(['FileSystem', 'Network'])).toBe('files, the network');
  });
});
