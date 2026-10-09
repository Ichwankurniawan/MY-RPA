// Rename and usages (ADR-0041, E-3): pure edits on the v1.0 JSON from the places the server found a name. The server
// decides what is a use of which declaration (scopes, strings, members); this only changes those exact characters, and
// refuses when they are not the old name any more (the document changed since the server answered).

import { carryKey, indexDocument, isObject, nodeLabel } from './document';
import type { ActivityDescriptor, Json, JsonObject, NameReference } from './types';
import { nodeJsonPath } from './workflowData';

type Segment = string | number;

/**
 * The JSON path of a value in the diagnostics' form (`$.root.children[1].properties.message`, `$.variables[0].name`) as
 * steps through `document`. Object keys are matched against the document itself, longest first, so keys with dots (a
 * slot such as `case:1.5`) are found exactly. Undefined when the path does not lead to a value.
 */
export function pathSegments(document: JsonObject, path: string): Segment[] | undefined {
  if (!path.startsWith('$')) {
    return undefined;
  }

  const segments: Segment[] = [];
  let at: Json = document;
  let rest = path.slice(1);
  while (rest !== '') {
    if (rest.startsWith('[')) {
      const close = rest.indexOf(']');
      const index = Number(rest.slice(1, close));
      if (close < 0 || !Array.isArray(at) || !Number.isInteger(index) || index < 0 || index >= at.length) {
        return undefined;
      }

      segments.push(index);
      at = at[index];
      rest = rest.slice(close + 1);
    } else if (rest.startsWith('.') && isObject(at)) {
      const object: JsonObject = at;
      const key = Object.keys(object)
        .filter((k) => rest.startsWith(`.${k}`) && ['', '.', '['].includes(rest.charAt(k.length + 1)))
        .sort((a, b) => b.length - a.length)[0];
      if (key === undefined) {
        return undefined;
      }

      segments.push(key);
      at = object[key];
      rest = rest.slice(key.length + 1);
    } else {
      return undefined;
    }
  }

  return segments;
}

function valueAt(document: JsonObject, segments: readonly Segment[]): Json | undefined {
  let at: Json | undefined = document;
  for (const segment of segments) {
    at = typeof segment === 'number' ? (Array.isArray(at) ? at[segment] : undefined) : isObject(at) ? at[segment] : undefined;
  }

  return at;
}

/** `value` with the value at `segments` replaced; everything else is shared, and copied objects keep their client keys. */
function setAt(value: Json, segments: readonly Segment[], replacement: Json): Json {
  if (segments.length === 0) {
    return replacement;
  }

  const [head, ...tail] = segments;
  if (typeof head === 'number' && Array.isArray(value)) {
    const copy = [...value];
    copy[head] = setAt(value[head], tail, replacement);
    return copy;
  }

  if (typeof head === 'string' && isObject(value)) {
    return carryKey(value, { ...value, [head]: setAt(value[head], tail, replacement) });
  }

  return value;
}

/**
 * Renames `oldName` to `newName` at every place in `references` (the declaration and its uses, as the server found
 * them for this document version): one new document, or the reason it cannot be done. Only the reported characters
 * change; a place whose characters are not `oldName` means the document changed meanwhile, and nothing changes.
 */
export function renameReferences(document: JsonObject, references: readonly NameReference[], oldName: string, newName: string): JsonObject | string {
  const byPath = new Map<string, NameReference[]>();
  for (const reference of references) {
    byPath.set(reference.path, [...(byPath.get(reference.path) ?? []), reference]);
  }

  let result: JsonObject = document;
  for (const [path, places] of byPath) {
    const segments = pathSegments(result, path);
    const text = segments === undefined ? undefined : valueAt(result, segments);
    if (segments === undefined || typeof text !== 'string') {
      return 'The workflow changed since its uses were found; try again.';
    }

    // From the end, so earlier offsets stay right.
    let next = text;
    for (const place of [...places].sort((a, b) => b.start - a.start)) {
      if (next.slice(place.start, place.start + place.length) !== oldName) {
        return 'The workflow changed since its uses were found; try again.';
      }

      next = next.slice(0, place.start) + newName + next.slice(place.start + place.length);
    }

    result = setAt(result, segments, next) as JsonObject;
  }

  return result;
}

/** Where a name is used: the activity (its client key and label) and what of it uses the name. */
export interface Usage {
  readonly key: string;
  readonly label: string;
  /** The property, map entry or transition (e.g. `message`, `arguments.who`, `transition 1`). */
  readonly where: string;
}

/** The activities that use a name (declarations left out), in document order; one entry per place. */
export function usagesOf(document: JsonObject, references: readonly NameReference[], catalog: ReadonlyMap<string, ActivityDescriptor>): Usage[] {
  const nodes = indexDocument(document).entries.map((entry) => ({ entry, path: nodeJsonPath(entry.path) }));
  const usages: Usage[] = [];
  for (const reference of references.filter((r) => !r.declaration)) {
    // The deepest node whose path the reference starts with, followed by one of the node's own fields.
    const owner = nodes
      .filter((n) => reference.path.startsWith(`${n.path}.properties.`) || reference.path.startsWith(`${n.path}.transitions[`))
      .sort((a, b) => b.path.length - a.path.length)[0];
    if (owner === undefined) {
      continue;
    }

    const rest = reference.path.slice(owner.path.length + 1);
    const transition = /^transitions\[(\d+)\]/.exec(rest);
    const where = transition ? `transition ${Number(transition[1]) + 1}` : rest.slice('properties.'.length);
    const type = typeof owner.entry.node.type === 'string' ? owner.entry.node.type : '';
    usages.push({ key: owner.entry.key, label: nodeLabel(owner.entry.node, catalog.get(type)), where });
  }

  return usages;
}
