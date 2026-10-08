// Workflow-level authoring (W4B, ADR-0032): workflow metadata, arguments, variables, node ids, typed property values,
// and where a validation diagnostic belongs. Like document.ts, every edit is a pure function on the v1.0 JSON that
// copies only what changes and keeps unknown fields. Validation stays on the server (WorkflowLoader); the checks here
// only keep the editors honest (for example a default must be JSON before it can be stored).

import { indexDocument, isObject, updateNode, type NodeEntry, type Step } from './document';
import type { Diagnostic, Json, JsonObject } from './types';

/** The workflow's identity fields (format §1). */
export type MetadataField = 'id' | 'name' | 'version' | 'description';

/** Data types of arguments and variables (format §2). */
export const dataTypes = ['String', 'Int', 'Decimal', 'Boolean', 'DateTime', 'Object', 'List', 'Dictionary'] as const;

export const directions = ['In', 'Out', 'InOut'] as const;

export type DataList = 'arguments' | 'variables';

/** Sets a metadata field. A blank description is removed (it is optional); the others keep what was typed. */
export function setMetadata(document: JsonObject, field: MetadataField, text: string): JsonObject {
  if (field === 'description' && text.trim() === '') {
    if (!('description' in document)) {
      return document;
    }

    const { description: _removed, ...rest } = document;
    return rest;
  }

  return document[field] === text ? document : { ...document, [field]: text };
}

/** The rows of `arguments` or `variables` (objects only; anything else is left for validation to report). */
export function rows(document: JsonObject, list: DataList): readonly Json[] {
  const value = document[list];
  return Array.isArray(value) ? value : [];
}

/** Every argument and variable name in use. */
export function namesInUse(document: JsonObject): Set<string> {
  const names = new Set<string>();
  for (const list of ['arguments', 'variables'] as const) {
    for (const row of rows(document, list)) {
      if (isObject(row) && typeof row.name === 'string') {
        names.add(row.name);
      }
    }
  }

  return names;
}

/** `base1`, `base2`, … : the first name not used by any argument or variable (names share one namespace). */
export function freeName(document: JsonObject, base: string): string {
  const used = namesInUse(document);
  for (let i = 1; ; i++) {
    if (!used.has(`${base}${i}`)) {
      return `${base}${i}`;
    }
  }
}

/** Adds a row: an In String argument or a String variable with a free name. */
export function addRow(document: JsonObject, list: DataList): JsonObject {
  const row: JsonObject = list === 'arguments' ? { name: freeName(document, 'argument'), direction: 'In', type: 'String' } : { name: freeName(document, 'variable'), type: 'String' };
  return { ...document, [list]: [...rows(document, list), row] };
}

export function removeRow(document: JsonObject, list: DataList, index: number): JsonObject {
  const current = rows(document, list);
  return { ...document, [list]: [...current.slice(0, index), ...current.slice(index + 1)] };
}

/**
 * Changes fields of a row; `undefined` removes a field. Making an argument Out also removes `required` and `default`,
 * which an Out argument may not have (MYRPA1065), so the editor never creates that error by itself.
 */
export function updateRow(document: JsonObject, list: DataList, index: number, changes: Readonly<Record<string, Json | undefined>>): JsonObject {
  const current = rows(document, list);
  const row = current[index];
  if (!isObject(row)) {
    return document;
  }

  const merged: Record<string, Json | undefined> = { ...row, ...changes };
  if (list === 'arguments' && merged.direction === 'Out') {
    merged.required = undefined;
    merged.default = undefined;
  }

  const updated = Object.fromEntries(Object.entries(merged).filter(([, value]) => value !== undefined)) as JsonObject;
  if (Object.keys(updated).length === Object.keys(row).length && Object.entries(updated).every(([key, value]) => row[key] === value)) {
    return document;
  }

  const copy = current.slice();
  copy[index] = updated;
  return { ...document, [list]: copy };
}

/** Parses a default typed as JSON: blank means "no default"; otherwise the JSON value or the reason it is not JSON. */
export function parseJsonText(text: string): { ok: true; value: Json | undefined } | { ok: false; error: string } {
  if (text.trim() === '') {
    return { ok: true, value: undefined };
  }

  try {
    return { ok: true, value: JSON.parse(text) as Json };
  } catch (error) {
    return { ok: false, error: `Not valid JSON: ${(error as Error).message}` };
  }
}

/** A JSON value as one line of text for an editor (undefined: empty). */
export const jsonText = (value: Json | undefined): string => (value === undefined ? '' : JSON.stringify(value));

/** Sets a node's id (trimmed, as the WPF Studio). Uniqueness and form are checked by validation (MYRPA1030/1031). */
export function setNodeId(document: JsonObject, path: readonly Step[], id: string): JsonObject {
  const trimmed = id.trim();
  return updateNode(document, path, (node) => (node.id === trimmed ? node : { ...node, id: trimmed }));
}

/** Sets any property value (a map, a literal, raw JSON); `undefined` removes the property. */
export function setPropertyValue(document: JsonObject, path: readonly Step[], name: string, value: Json | undefined): JsonObject {
  return updateNode(document, path, (node) => {
    const properties = isObject(node.properties) ? node.properties : {};
    if (value === undefined) {
      if (!(name in properties)) {
        return node;
      }

      const { [name]: _removed, ...rest } = properties;
      return { ...node, properties: rest };
    }

    return properties[name] === value ? node : { ...node, properties: { ...properties, [name]: value } };
  });
}

/** The names an assignment target may name: variables and Out/InOut arguments (format §3). */
export function assignableNames(document: JsonObject): string[] {
  const names: string[] = [];
  for (const row of rows(document, 'variables')) {
    if (isObject(row) && typeof row.name === 'string') {
      names.push(row.name);
    }
  }

  for (const row of rows(document, 'arguments')) {
    if (isObject(row) && typeof row.name === 'string' && (row.direction === 'Out' || row.direction === 'InOut')) {
      names.push(row.name);
    }
  }

  return names;
}

/**
 * Where a diagnostic belongs: a node (and property), an argument or variable row, or the workflow (and field). A node is
 * named by its client key (exact even when ids are missing, invalid or duplicated) and by its id when it has one.
 */
export type DiagnosticTarget =
  | { readonly kind: 'node'; readonly key?: string; readonly nodeId?: string; readonly property?: string }
  | { readonly kind: 'row'; readonly list: DataList; readonly index: number }
  | { readonly kind: 'workflow'; readonly field?: string };

/** The JSON path of a node, as the loader writes it in diagnostics (`$.root.children[2].slots.case:1.5`). */
export function nodeJsonPath(path: readonly Step[]): string {
  return '$.root' + path.map((step) => ('children' in step ? `.children[${step.children}]` : `.slots.${step.slot}`)).join('');
}

// A node's own fields, including format 1.1's transitions and layout (ADR-0037).
const nodeRemainder = (rest: string) =>
  rest === '' ||
  ['.id', '.type', '.displayName', '.properties', '.children', '.slots', '.transitions', '.layout'].includes(rest) ||
  rest.startsWith('.properties.') ||
  rest.startsWith('.children[') ||
  rest.startsWith('.slots.') ||
  rest.startsWith('.transitions[') ||
  rest.startsWith('.layout.');

const located = new WeakMap<JsonObject, WeakMap<Diagnostic, DiagnosticTarget>>();
const pathMaps = new WeakMap<JsonObject, ReadonlyMap<string, NodeEntry>>();

/** Every node of the document by its JSON path (computed once per document version). */
function nodePaths(document: JsonObject): ReadonlyMap<string, NodeEntry> {
  let map = pathMaps.get(document);
  if (map === undefined) {
    map = new Map(indexDocument(document).entries.map((entry) => [nodeJsonPath(entry.path), entry]));
    pathMaps.set(document, map);
  }

  return map;
}

/**
 * Locates a diagnostic of `document` (the document that was validated) exactly like the WPF `DraftValidator` (W9 corpus
 * parity): rows by `$.arguments[i]` / `$.variables[i]`; otherwise the longest node path that prefixes the diagnostic's
 * path and leaves a remainder a node can have (so slot names containing dots stay exact), falling back to the longest
 * prefix; the property is the name after that node's `.properties.`. Without a document it falls back to the node id.
 */
export function diagnosticTarget(diagnostic: Diagnostic, document: JsonObject | undefined): DiagnosticTarget {
  const cache = document === undefined ? undefined : (located.get(document) ?? new WeakMap<Diagnostic, DiagnosticTarget>());
  const cached = cache?.get(diagnostic);
  if (cached) {
    return cached;
  }

  const target = locate(diagnostic, document);
  if (document !== undefined && cache !== undefined) {
    cache.set(diagnostic, target);
    located.set(document, cache);
  }

  return target;
}

function locate(diagnostic: Diagnostic, document: JsonObject | undefined): DiagnosticTarget {
  const path = diagnostic.path;
  const row = /^\$\.(arguments|variables)\[(\d+)\]/.exec(path);
  if (row) {
    return { kind: 'row', list: row[1] as DataList, index: Number(row[2]) };
  }

  if (document !== undefined) {
    // Candidates are the prefixes of the path that end where a dot follows (or at its end): longest first.
    const nodes = nodePaths(document);
    let best: string | undefined;
    let fallback: string | undefined;
    for (let end = path.length; end > 0; end = path.lastIndexOf('.', end - 1)) {
      const candidate = path.slice(0, end);
      if (!nodes.has(candidate)) {
        continue;
      }

      fallback ??= candidate;
      if (nodeRemainder(path.slice(end))) {
        best = candidate;
        break;
      }
    }

    best ??= fallback;
    const entry = best === undefined ? undefined : nodes.get(best);
    if (best !== undefined && entry !== undefined) {
      const rest = path.slice(best.length);
      const property = rest.startsWith('.properties.') ? rest.slice('.properties.'.length).split('.')[0] : undefined;
      return { kind: 'node', key: entry.key, nodeId: typeof entry.node.id === 'string' ? entry.node.id : undefined, property: property || undefined };
    }
  } else if (diagnostic.nodeId) {
    const at = path.lastIndexOf('.properties.');
    const property = at < 0 ? undefined : path.slice(at + '.properties.'.length).split('.')[0];
    return { kind: 'node', nodeId: diagnostic.nodeId, property: property || undefined };
  }

  const field = /^\$\.(\w+)$/.exec(path)?.[1];
  return { kind: 'workflow', field };
}
