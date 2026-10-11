// The AI Workflow Assistant's review helpers (ADR-0047): what a proposal changes, computed from the documents
// themselves (never from the model's description), by node id.

import { isObject } from './document';
import type { Json, JsonObject } from './types';

/** A change to one activity. */
export interface NodeChange {
  readonly kind: 'added' | 'removed' | 'changed' | 'moved';
  readonly id: string;
  readonly type: string;
  readonly detail?: string;
}

/** What a proposal changes: activities by id, and the workflow's own fields, arguments and variables. */
export interface WorkflowChanges {
  readonly nodes: readonly NodeChange[];
  readonly workflow: readonly string[];
}

interface NodeInfo {
  readonly node: JsonObject;
  readonly parent: string;
}

/** Every node by id, with where it sits (its parent's id and slot). */
function nodes(document: JsonObject | undefined): Map<string, NodeInfo> {
  const found = new Map<string, NodeInfo>();
  const visit = (node: Json | undefined, parent: string) => {
    if (!isObject(node)) {
      return;
    }

    const id = typeof node.id === 'string' ? node.id : `(no id ${found.size + 1})`;
    found.set(id, { node, parent });
    if (Array.isArray(node.children)) {
      node.children.forEach((child) => visit(child, id));
    }

    if (isObject(node.slots)) {
      for (const [slot, child] of Object.entries(node.slots)) {
        visit(child, `${id}.${slot}`);
      }
    }
  };
  visit(document?.root, '');
  return found;
}

/** JSON text with sorted keys, so key order never counts as a change. */
function canonical(value: Json | undefined): string {
  if (Array.isArray(value)) {
    return `[${value.map(canonical).join(',')}]`;
  }

  if (isObject(value)) {
    return `{${Object.keys(value).sort().map((k) => `${JSON.stringify(k)}:${canonical(value[k])}`).join(',')}}`;
  }

  return JSON.stringify(value ?? null);
}

function changedKeys(before: Json | undefined, after: Json | undefined): string[] {
  const a = isObject(before) ? before : {};
  const b = isObject(after) ? after : {};
  return [...new Set([...Object.keys(a), ...Object.keys(b)])].filter((k) => canonical(a[k]) !== canonical(b[k])).sort();
}

function rowsByName(document: JsonObject | undefined, list: 'arguments' | 'variables'): Map<string, Json> {
  const rows = document?.[list];
  return new Map(Array.isArray(rows) ? rows.filter(isObject).map((r) => [String(r.name), r] as const) : []);
}

/** What `after` changes compared with `before` (undefined: a new workflow, so every activity is added). */
export function compareWorkflows(before: JsonObject | undefined, after: JsonObject): WorkflowChanges {
  const old = nodes(before);
  const next = nodes(after);
  const changes: NodeChange[] = [];
  for (const [id, { node, parent }] of next) {
    const type = String(node.type ?? '');
    const previous = old.get(id);
    if (previous === undefined) {
      changes.push({ kind: 'added', id, type });
      continue;
    }

    const details: string[] = [];
    if (previous.node.type !== node.type) {
      details.push(`type ${String(previous.node.type)} → ${type}`);
    }

    const properties = changedKeys(previous.node.properties, node.properties);
    if (properties.length > 0) {
      details.push(`properties ${properties.join(', ')}`);
    }

    if (previous.node.displayName !== node.displayName) {
      details.push('display name');
    }

    if (canonical(previous.node.transitions) !== canonical(node.transitions)) {
      details.push('transitions');
    }

    if (details.length > 0) {
      changes.push({ kind: 'changed', id, type, detail: details.join('; ') });
    }

    if (previous.parent !== parent) {
      changes.push({ kind: 'moved', id, type, detail: parent === '' ? 'to the top' : `into ${parent}` });
    }
  }

  for (const [id, { node }] of old) {
    if (!next.has(id)) {
      changes.push({ kind: 'removed', id, type: String(node.type ?? '') });
    }
  }

  const workflow: string[] = [];
  if (before !== undefined) {
    for (const field of ['name', 'description', 'version', 'schemaVersion'] as const) {
      if (canonical(before[field]) !== canonical(after[field])) {
        workflow.push(`${field} changed`);
      }
    }

    for (const list of ['arguments', 'variables'] as const) {
      const a = rowsByName(before, list);
      const b = rowsByName(after, list);
      const noun = list === 'arguments' ? 'argument' : 'variable';
      for (const [name, row] of b) {
        if (!a.has(name)) {
          workflow.push(`${noun} ${name} added`);
        } else if (canonical(a.get(name)) !== canonical(row)) {
          workflow.push(`${noun} ${name} changed`);
        }
      }

      for (const name of a.keys()) {
        if (!b.has(name)) {
          workflow.push(`${noun} ${name} removed`);
        }
      }
    }
  }

  return { nodes: changes, workflow };
}

/** Plain words for the side effects the catalog declares. */
export function sideEffectWords(effects: readonly string[]): string {
  const words: Record<string, string> = { FileSystem: 'files', Network: 'the network', Browser: 'a browser' };
  return effects.map((e) => words[e] ?? e).join(', ');
}
