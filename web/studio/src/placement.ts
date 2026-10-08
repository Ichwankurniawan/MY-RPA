// Placing activities anywhere (W7, ADR-0033): into a list at an index or into a named slot (including prefix slots such
// as Switch `case:<value>`), moving across containers, and the clipboard. The rules follow the activity catalog and the
// WPF Studio's DraftEdits (the behavioral reference): a list belongs to an activity that allows children, a slot holds
// one activity, a node cannot move into itself. Every function is pure; a refusal is a reason, never a partial change.

import { indexDocument, isObject, keyOf, nodeAt, updateNode, type Step } from './document';
import type { ActivityDescriptor, Json, JsonObject, SlotDescriptor } from './types';

/** A position in a parent: a list index (0 … count), or a named slot that must be empty. */
export type Position = { readonly index: number } | { readonly slot: string };

/** Where a node goes: a position under the node at `parentPath`. */
export interface Target {
  readonly parentPath: readonly Step[];
  readonly position: Position;
}

type Catalog = ReadonlyMap<string, ActivityDescriptor>;

const activityOf = (node: JsonObject, catalog: Catalog) => (typeof node.type === 'string' ? catalog.get(node.type) : undefined);
const nameOf = (node: JsonObject, catalog: Catalog) => activityOf(node, catalog)?.displayName ?? (typeof node.type === 'string' ? node.type : 'This node');
const samePath = (a: readonly Step[], b: readonly Step[]) => JSON.stringify(a) === JSON.stringify(b);

/** The catalog slot a name belongs to: an exact name, or a prefix slot whose prefix it extends (`case:` → `case:a`). */
export function slotDescriptor(activity: ActivityDescriptor, name: string): SlotDescriptor | undefined {
  return activity.slots.find((s) => (s.prefix ? name.startsWith(s.name) && name.length > s.name.length : s.name === name));
}

/** Why nothing can be placed at `target` (undefined when it can). */
export function placeRefusal(document: JsonObject, target: Target, catalog: Catalog): string | undefined {
  let parent: JsonObject;
  try {
    parent = nodeAt(document, target.parentPath);
  } catch {
    return 'That place no longer exists.';
  }

  const activity = activityOf(parent, catalog);
  if (activity === undefined) {
    return `${nameOf(parent, catalog)} is not in the activity catalog, so its list and slots are unknown.`;
  }

  if ('index' in target.position) {
    if (!activity.allowsChildren) {
      return `${activity.displayName} does not contain a list of activities.`;
    }

    const count = Array.isArray(parent.children) ? parent.children.length : 0;
    return target.position.index < 0 || target.position.index > count ? 'The position is outside the list.' : undefined;
  }

  const name = target.position.slot;
  if (slotDescriptor(activity, name) === undefined) {
    return `${activity.displayName} has no slot '${name}'.`;
  }

  return isObject(parent.slots) && name in parent.slots ? `The slot '${name}' already contains an activity.` : undefined;
}

/** Places `node` at `target` (which must be allowed); returns the new document and the node's path. */
export function place(document: JsonObject, target: Target, node: JsonObject): { document: JsonObject; path: Step[] } {
  const { parentPath, position } = target;
  const updated = updateNode(document, parentPath, (parent) => {
    if ('index' in position) {
      const children = Array.isArray(parent.children) ? parent.children : [];
      return { ...parent, children: [...children.slice(0, position.index), node, ...children.slice(position.index)] };
    }

    return { ...parent, slots: { ...(isObject(parent.slots) ? parent.slots : {}), [position.slot]: node } };
  });
  const step: Step = 'index' in position ? { children: position.index } : { slot: position.slot };
  return { document: updated, path: [...parentPath, step] };
}

const isAncestorOrSelf = (ancestor: readonly Step[], path: readonly Step[]) => ancestor.length <= path.length && samePath(ancestor, path.slice(0, ancestor.length));

/** Moving a node into the gap right before or after itself, or into its own slot, changes nothing. */
function isNoOp(from: readonly Step[], target: Target): boolean {
  const last = from[from.length - 1];
  if (!samePath(from.slice(0, -1), target.parentPath)) {
    return false;
  }

  return 'children' in last
    ? 'index' in target.position && (target.position.index === last.children || target.position.index === last.children + 1)
    : 'slot' in target.position && target.position.slot === last.slot;
}

/**
 * Why the node at `from` cannot move to `target` (undefined when it can; a move to where it already is is allowed and
 * changes nothing). `target` is described as seen before the move.
 */
export function moveToRefusal(document: JsonObject, from: readonly Step[], target: Target, catalog: Catalog): string | undefined {
  if (from.length === 0) {
    return 'The root activity cannot be moved.';
  }

  if (isAncestorOrSelf(from, target.parentPath)) {
    return 'An activity cannot be moved into itself.';
  }

  return isNoOp(from, target) ? undefined : placeRefusal(document, target, catalog);
}

/** Moves the node at `from` (with its subtree, keeping its client key) to `target`; returns the document and new path. */
export function moveTo(document: JsonObject, from: readonly Step[], target: Target, catalog: Catalog): { document: JsonObject; path: Step[] } {
  const refusal = moveToRefusal(document, from, target, catalog);
  if (refusal !== undefined) {
    throw new Error(refusal);
  }

  if (isNoOp(from, target)) {
    return { document, path: [...from] };
  }

  const node = nodeAt(document, from);
  const last = from[from.length - 1];
  const fromParent = from.slice(0, -1);
  const removed = updateNode(document, fromParent, (parent) => {
    if ('children' in last) {
      return { ...parent, children: (parent.children as Json[]).filter((_, i) => i !== last.children) };
    }

    const { [last.slot]: _gone, ...slots } = parent.slots as JsonObject;
    return { ...parent, slots };
  });

  // Removing the node shifts its later siblings (and the paths through them) back by one.
  const parentPath = target.parentPath.map((step, depth): Step =>
    'children' in last && 'children' in step && depth === fromParent.length && samePath(target.parentPath.slice(0, depth), fromParent) && step.children > last.children
      ? { children: step.children - 1 }
      : step,
  );
  let position = target.position;
  if ('index' in position && 'children' in last && samePath(parentPath, fromParent) && position.index > last.children) {
    position = { index: position.index - 1 };
  }

  return place(removed, { parentPath, position }, node);
}

/**
 * Where an inserted or pasted activity goes for the selection at `path` (as the WPF Studio): into the selected node's
 * list (at the end) when it holds one; else into its first empty fixed slot; else right after it in its parent's list.
 */
export function selectionTarget(document: JsonObject, path: readonly Step[], catalog: Catalog): Target | string {
  const node = nodeAt(document, path);
  const activity = activityOf(node, catalog);
  if (activity?.allowsChildren) {
    return { parentPath: path, position: { index: Array.isArray(node.children) ? node.children.length : 0 } };
  }

  const slots = isObject(node.slots) ? node.slots : {};
  const empty = activity?.slots.find((s) => !s.prefix && !(s.name in slots));
  if (empty) {
    return { parentPath: path, position: { slot: empty.name } };
  }

  const last = path.at(-1);
  if (last === undefined) {
    return `${nameOf(node, catalog)} has no list or empty slot to insert into.`;
  }

  if (!('children' in last)) {
    return `The selected activity fills the slot '${last.slot}'. Select a list, a container with an empty slot, or an empty slot, to insert.`;
  }

  const target: Target = { parentPath: path.slice(0, -1), position: { index: last.children + 1 } };
  return placeRefusal(document, target, catalog) ?? target;
}

// The clipboard (as the WPF DraftClipboard): text `{"myrpaNodes":"1.0","nodes":[node, …]}`, nodes in the workflow JSON
// format, so copied activities are readable. Client keys are never fields, so they are never copied.

export const clipboardMarker = 'myrpaNodes';

export function serializeNodes(nodes: readonly JsonObject[]): string {
  return JSON.stringify({ [clipboardMarker]: '1.0', nodes }, null, 2);
}

/** The nodes in clipboard text, or undefined when the text is not MyRPA activities. */
export function parseNodes(text: string): JsonObject[] | undefined {
  if (!text.trimStart().startsWith('{')) {
    return undefined;
  }

  try {
    const value = JSON.parse(text) as Json;
    if (!isObject(value) || !(clipboardMarker in value) || !Array.isArray(value.nodes) || value.nodes.length === 0 || !value.nodes.every(isObject)) {
      return undefined;
    }

    return value.nodes as JsonObject[];
  } catch {
    return undefined;
  }
}

const baseName = (type: Json | undefined) => (typeof type === 'string' ? type.slice(type.lastIndexOf('.') + 1).toLowerCase() : '') || 'node';

/**
 * Gives every pasted node (and descendant) whose id is already used — in the document or earlier in the pasted set — a
 * new unique id `<type name>-N` (as the WPF DraftClipboard), so pasting never creates duplicate ids. Flowchart
 * transitions (ADR-0037) follow the renamed ids; a transition to a step that was not copied is dropped.
 */
export function prepareForPaste(nodes: readonly JsonObject[], document: JsonObject): JsonObject[] {
  const renames = new Map<string, string>();
  const renamed = renameForPaste(nodes, document, renames);
  const retarget = (node: JsonObject): JsonObject => {
    const copy: Record<string, Json> = { ...node };
    if (Array.isArray(node.transitions)) {
      const kept = node.transitions.flatMap((t) => (isObject(t) && typeof t.to === 'string' && renames.has(t.to) ? [{ ...t, to: renames.get(t.to)! }] : []));
      if (kept.length > 0) {
        copy.transitions = kept;
      } else {
        delete copy.transitions;
      }
    }

    if (Array.isArray(node.children)) {
      copy.children = node.children.map((c) => (isObject(c) ? retarget(c) : c));
    }

    if (isObject(node.slots)) {
      copy.slots = Object.fromEntries(Object.entries(node.slots).map(([k, v]) => [k, isObject(v) ? retarget(v) : v]));
    }

    return copy;
  };
  return renamed.map(retarget);
}

/** Renames as described above; `renames` receives every pasted node's original id → its id in the document. */
function renameForPaste(nodes: readonly JsonObject[], document: JsonObject, renames: Map<string, string>): JsonObject[] {
  const used = new Set(indexDocument(document).entries.map((entry) => entry.node.id).filter((id): id is string => typeof id === 'string'));
  const next = (type: Json | undefined) => {
    const base = baseName(type);
    let n = 1;
    while (used.has(`${base}-${n}`)) {
      n++;
    }

    return `${base}-${n}`;
  };
  const rename = (node: JsonObject): JsonObject => {
    const id = typeof node.id === 'string' && node.id !== '' && !used.has(node.id) ? node.id : next(node.type);
    used.add(id);
    if (typeof node.id === 'string' && !renames.has(node.id)) {
      renames.set(node.id, id);
    }
    const copy: Record<string, Json> = { ...node, id };
    if (Array.isArray(node.children)) {
      copy.children = node.children.map((c) => (isObject(c) ? rename(c) : c));
    }

    if (isObject(node.slots)) {
      copy.slots = Object.fromEntries(Object.entries(node.slots).map(([k, v]) => [k, isObject(v) ? rename(v) : v]));
    }

    return copy;
  };
  return nodes.map(rename);
}

/** The target of a list gap or empty slot, addressed by the parent's client key (paths change; keys do not). */
export interface KeyedTarget {
  readonly parentKey: string;
  readonly position: Position;
}

/** Resolves a keyed target in the current document (undefined when the parent is gone). */
export function resolveTarget(document: JsonObject, target: KeyedTarget): Target | undefined {
  const entry = indexDocument(document).byKey.get(target.parentKey);
  return entry ? { parentPath: entry.path, position: target.position } : undefined;
}

/** The keyed form of a target (for state that outlives one document version). */
export function keyedTarget(document: JsonObject, target: Target): KeyedTarget {
  return { parentKey: keyOf(nodeAt(document, target.parentPath)), position: target.position };
}
