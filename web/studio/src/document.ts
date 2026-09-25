// The Web Studio's editing model (ADR-0021): the canonical v1.0 workflow JSON itself, kept as an immutable tree.
// Unknown fields survive because nothing is mapped into another shape. Edits copy only the path from the edited node
// to the root (structural sharing), so unchanged subtrees keep their identity and their memoized rendering.
// Each node object gets a stable client key (a WeakMap entry, never a JSON field), carried over when an edit replaces it.

import type { ActivityDescriptor, Json, JsonObject, PropertyDescriptor } from './types';

/** A step from a node to one of its child nodes. */
export type Step = { readonly children: number } | { readonly slot: string };

export interface NodeEntry {
  readonly key: string;
  readonly node: JsonObject;
  /** Steps from the document's `root` node; empty for the root itself. */
  readonly path: readonly Step[];
  readonly depth: number;
  /** The slot name when the node sits in a slot. */
  readonly slot?: string;
}

export interface DocumentIndex {
  /** Every node in document order (depth first). */
  readonly entries: readonly NodeEntry[];
  readonly byKey: ReadonlyMap<string, NodeEntry>;
  /** Workflow node id → client key (first occurrence; duplicate ids are a validation error). */
  readonly byNodeId: ReadonlyMap<string, string>;
}

export type OpenResult =
  | { readonly ok: true; readonly document: JsonObject; readonly readOnlyReason?: string }
  | { readonly ok: false; readonly error: string };

const keys = new WeakMap<object, string>();
let nextKey = 0;

/** The node's stable client key. */
export function keyOf(node: JsonObject): string {
  let key = keys.get(node);
  if (key === undefined) {
    key = `n${++nextKey}`;
    keys.set(node, key);
  }

  return key;
}

export function isObject(value: Json | undefined): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * Parses a workflow file. Files the browser cannot write back unchanged open read-only instead of being altered silently:
 * number forms JavaScript would rewrite (`1.50`, `1e3`, integers beyond 2^53) and objects whose integer-like keys
 * JavaScript would reorder. JSON comments and trailing commas (accepted by the engine) cannot be parsed here yet.
 */
export function openWorkflow(text: string): OpenResult {
  let document: Json;
  try {
    document = JSON.parse(text) as Json;
  } catch (error) {
    return { ok: false, error: `The Web Studio cannot parse this file yet (comments and trailing commas are not supported): ${(error as Error).message}` };
  }

  if (!isObject(document)) {
    return { ok: false, error: 'A workflow file must be a JSON object.' };
  }

  const lossyNumber = findLossyNumber(text);
  if (lossyNumber !== undefined) {
    return { ok: true, document, readOnlyReason: `The number ${lossyNumber} cannot be saved unchanged by the Web Studio yet.` };
  }

  if (hasReorderedKeys(document)) {
    return { ok: true, document, readOnlyReason: 'An object with numeric keys would be saved in a different order.' };
  }

  return { ok: true, document };
}

/** The first number literal whose text JavaScript would write differently, if any. */
export function findLossyNumber(text: string): string | undefined {
  const outsideStrings = text.replace(/"(?:[^"\\]|\\.)*"/g, '""');
  for (const match of outsideStrings.matchAll(/-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/g)) {
    if (String(Number(match[0])) !== match[0]) {
      return match[0];
    }
  }

  return undefined;
}

function hasReorderedKeys(value: Json): boolean {
  if (Array.isArray(value)) {
    return value.some(hasReorderedKeys);
  }

  if (!isObject(value)) {
    return false;
  }

  const names = Object.keys(value);
  if (names.length > 1 && names.some((name) => /^(?:0|[1-9]\d*)$/.test(name))) {
    return true;
  }

  return Object.values(value).some(hasReorderedKeys);
}

/** The file content to save: the document as JSON (two-space indentation). Client keys are not part of it. */
export function serialize(document: JsonObject): string {
  return `${JSON.stringify(document, null, 2)}\n`;
}

/** The node's child nodes: `children` in order, then `slots` in document order. Malformed entries are skipped. */
export function childSteps(node: JsonObject): { step: Step; node: JsonObject; slot?: string }[] {
  const result: { step: Step; node: JsonObject; slot?: string }[] = [];
  const children = node.children;
  if (Array.isArray(children)) {
    children.forEach((child, index) => {
      if (isObject(child)) {
        result.push({ step: { children: index }, node: child });
      }
    });
  }

  const slots = node.slots;
  if (isObject(slots)) {
    for (const [name, child] of Object.entries(slots)) {
      if (isObject(child)) {
        result.push({ step: { slot: name }, node: child, slot: name });
      }
    }
  }

  return result;
}

const indexes = new WeakMap<JsonObject, DocumentIndex>();

/** The document's nodes, computed once per document version. */
export function indexDocument(document: JsonObject): DocumentIndex {
  const cached = indexes.get(document);
  if (cached) {
    return cached;
  }

  const entries: NodeEntry[] = [];
  const visit = (node: JsonObject, path: Step[], depth: number, slot?: string) => {
    entries.push({ key: keyOf(node), node, path, depth, slot });
    for (const child of childSteps(node)) {
      visit(child.node, [...path, child.step], depth + 1, child.slot);
    }
  };

  if (isObject(document.root)) {
    visit(document.root, [], 0);
  }

  const byKey = new Map(entries.map((entry) => [entry.key, entry]));
  const byNodeId = new Map<string, string>();
  for (const entry of entries) {
    const id = entry.node.id;
    if (typeof id === 'string' && !byNodeId.has(id)) {
      byNodeId.set(id, entry.key);
    }
  }

  const index = { entries, byKey, byNodeId };
  indexes.set(document, index);
  return index;
}

/** Replaces the node at `path` with `update(node)`, copying only its ancestors. Keys move to the replacements. */
export function updateNode(document: JsonObject, path: readonly Step[], update: (node: JsonObject) => JsonObject): JsonObject {
  const root = document.root;
  if (!isObject(root)) {
    throw new Error('The document has no root node.');
  }

  return { ...document, root: updateAt(root, path, 0, update) };
}

function updateAt(node: JsonObject, path: readonly Step[], at: number, update: (node: JsonObject) => JsonObject): JsonObject {
  let replacement: JsonObject;
  if (at === path.length) {
    replacement = update(node);
  } else {
    const step = path[at];
    if ('children' in step) {
      const children = node.children as Json[];
      const copy = children.slice();
      copy[step.children] = updateAt(children[step.children] as JsonObject, path, at + 1, update);
      replacement = { ...node, children: copy };
    } else {
      const slots = node.slots as JsonObject;
      replacement = { ...node, slots: { ...slots, [step.slot]: updateAt(slots[step.slot] as JsonObject, path, at + 1, update) } };
    }
  }

  if (replacement !== node) {
    keys.set(replacement, keyOf(node));
  }

  return replacement;
}

/**
 * Sets a string property. An empty value removes an optional property (as if it had never been set); a required
 * property keeps the empty string so validation reports it on the property.
 */
export function setProperty(document: JsonObject, path: readonly Step[], descriptor: PropertyDescriptor, text: string): JsonObject {
  return updateNode(document, path, (node) => {
    const properties = isObject(node.properties) ? node.properties : {};
    if (text === '' && !descriptor.required) {
      if (!(descriptor.name in properties)) {
        return node;
      }

      const { [descriptor.name]: _removed, ...rest } = properties;
      return { ...node, properties: rest };
    }

    return { ...node, properties: { ...properties, [descriptor.name]: text } };
  });
}

/** Sets the node's display name; an empty value removes it. */
export function setDisplayName(document: JsonObject, path: readonly Step[], text: string): JsonObject {
  return updateNode(document, path, (node) => {
    if (text === '') {
      if (!('displayName' in node)) {
        return node;
      }

      const { displayName: _removed, ...rest } = node;
      return rest;
    }

    return { ...node, displayName: text };
  });
}

export type Editability = { readonly editable: true; readonly text: string } | { readonly editable: false; readonly reason: string };

/**
 * Whether the W3 property editor can edit a value without changing its JSON type. Strings of the text-like kinds are
 * editable; literals (numbers, booleans, null) and maps are shown read-only until their editors exist.
 */
export function editability(descriptor: PropertyDescriptor, value: Json | undefined): Editability {
  if (descriptor.kind === 'ExpressionMap' || descriptor.kind === 'AssignmentTargetMap') {
    return { editable: false, reason: 'Map editing is not available yet.' };
  }

  if (value === undefined) {
    return { editable: true, text: '' };
  }

  if (typeof value === 'string') {
    return { editable: true, text: value };
  }

  return { editable: false, reason: `This ${value === null ? 'null' : Array.isArray(value) ? 'list' : typeof value} literal cannot be edited here yet.` };
}

/** The label shown for a node: its display name, else the activity's display name, else its type. */
export function nodeLabel(node: JsonObject, activity: ActivityDescriptor | undefined): string {
  if (typeof node.displayName === 'string' && node.displayName !== '') {
    return node.displayName;
  }

  return activity?.displayName ?? (typeof node.type === 'string' ? node.type : '(no type)');
}
