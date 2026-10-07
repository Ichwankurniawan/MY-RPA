// Workflow-level authoring (W4B, ADR-0032): workflow metadata, arguments, variables, node ids, typed property values,
// and where a validation diagnostic belongs. Like document.ts, every edit is a pure function on the v1.0 JSON that
// copies only what changes and keeps unknown fields. Validation stays on the server (WorkflowLoader); the checks here
// only keep the editors honest (for example a default must be JSON before it can be stored).

import { isObject, updateNode, type Step } from './document';
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

/** Where a diagnostic belongs: a node (and property), an argument or variable row, or the workflow (and field). */
export type DiagnosticTarget =
  | { readonly kind: 'node'; readonly nodeId: string; readonly property?: string }
  | { readonly kind: 'row'; readonly list: DataList; readonly index: number }
  | { readonly kind: 'workflow'; readonly field?: string };

/** Locates a diagnostic, like the WPF `DraftValidator`: rows by `$.arguments[i]`, properties by `.properties.<name>`. */
export function diagnosticTarget(diagnostic: Diagnostic): DiagnosticTarget {
  const row = /^\$\.(arguments|variables)\[(\d+)\]/.exec(diagnostic.path);
  if (row) {
    return { kind: 'row', list: row[1] as DataList, index: Number(row[2]) };
  }

  if (diagnostic.nodeId) {
    const at = diagnostic.path.indexOf('.properties.');
    const property = at < 0 ? undefined : diagnostic.path.slice(at + '.properties.'.length).split('.')[0];
    return { kind: 'node', nodeId: diagnostic.nodeId, property: property || undefined };
  }

  const field = /^\$\.(\w+)$/.exec(diagnostic.path)?.[1];
  return { kind: 'workflow', field };
}
