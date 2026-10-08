// Graph workflows in the Studio (G-2, ADR-0037): flowchart steps, their transitions and canvas positions, edited as pure
// functions on the v1.1 JSON with refusal reasons, like every other edit (ADR-0029). The engine owns what transitions
// mean (`ExecuteStepAsync`); the Studio only edits them, places them and draws them.

import { carryKey, childSteps, indexDocument, isObject, keyOf, nodeAt, updateNode, type Step } from './document';
import type { ActivityDescriptor, ExecutionEvent, Json, JsonObject } from './types';

type Catalog = ReadonlyMap<string, ActivityDescriptor>;

/** Size of a step card on the canvas (CSS keeps the card at this size; arrows end at its edges). */
export const stepSize = { width: 220, height: 84 } as const;

const grid = { left: 40, top: 40, column: 260, row: 150 } as const;

export const isGraphActivity = (activity: ActivityDescriptor | undefined): boolean => activity?.childLayout === 'Graph';

export function isGraphNode(node: JsonObject, catalog: Catalog): boolean {
  return typeof node.type === 'string' && isGraphActivity(catalog.get(node.type));
}

/** The node's transitions (objects only; unknown fields are kept by the edits below). */
export function transitionsOf(node: JsonObject): JsonObject[] {
  return Array.isArray(node.transitions) ? node.transitions.filter(isObject) : [];
}

const targetOf = (transition: JsonObject) => (typeof transition.to === 'string' ? transition.to : undefined);
const idOf = (node: JsonObject) => (typeof node.id === 'string' ? node.id : undefined);

/** The graph container's steps (its `children`), in order: the first is the start step. */
export function stepsOf(graph: JsonObject): JsonObject[] {
  return Array.isArray(graph.children) ? graph.children.filter(isObject) : [];
}

/** When the node at `path` is a step of a graph container: the container's path; otherwise undefined. */
export function graphParentPath(document: JsonObject, path: readonly Step[], catalog: Catalog): readonly Step[] | undefined {
  const last = path.at(-1);
  if (last === undefined || !('children' in last)) {
    return undefined;
  }

  const parentPath = path.slice(0, -1);
  return isGraphNode(nodeAt(document, parentPath), catalog) ? parentPath : undefined;
}

// ---------------------------------------------------------------------------------------------------------------------
// Edits. Each refusal says why; each edit assumes its refusal passed.

/** Why a transition from the step at `path` to the sibling `to` cannot be added (undefined when it can). */
export function addTransitionRefusal(document: JsonObject, path: readonly Step[], to: string, catalog: Catalog): string | undefined {
  const parentPath = graphParentPath(document, path, catalog);
  if (parentPath === undefined) {
    return 'Only a step of a flowchart can have transitions.';
  }

  if (!stepsOf(nodeAt(document, parentPath)).some((step) => idOf(step) === to)) {
    return `'${to}' is not a step of the same flowchart.`;
  }

  const node = nodeAt(document, path);
  return transitionsOf(node).some((t) => targetOf(t) === to && t.when === undefined)
    ? `${idOf(node) ?? 'This step'} already goes to ${to} without a condition.`
    : undefined;
}

/** Appends a transition `{ to }` (the lowest priority) to the step at `path`. */
export function addTransition(document: JsonObject, path: readonly Step[], to: string): JsonObject {
  return updateNode(document, path, (node) => ({ ...node, transitions: [...(Array.isArray(node.transitions) ? node.transitions : []), { to }] }));
}

/**
 * Sets fields of transition `index`: `to` (required), `when` and `label` (an empty text removes the field, so a
 * transition without a condition is always taken). Other fields of the transition are kept.
 */
export function updateTransition(document: JsonObject, path: readonly Step[], index: number, changes: { readonly to?: string; readonly when?: string; readonly label?: string }): JsonObject {
  return updateNode(document, path, (node) => {
    const list = (node.transitions as Json[]).slice();
    const current = list[index] as JsonObject;
    const next: Record<string, Json> = { ...current };
    for (const [field, value] of Object.entries(changes) as [string, string | undefined][]) {
      if (value === undefined) {
        continue;
      }

      if (value === '' && field !== 'to') {
        delete next[field];
      } else {
        next[field] = value;
      }
    }

    list[index] = next;
    return { ...node, transitions: list };
  });
}

/** Removes transition `index`; an empty list removes the field. */
export function removeTransition(document: JsonObject, path: readonly Step[], index: number): JsonObject {
  return updateNode(document, path, (node) => {
    const list = (node.transitions as Json[]).filter((_, i) => i !== index);
    if (list.length > 0) {
      return { ...node, transitions: list };
    }

    const { transitions: _removed, ...rest } = node;
    return rest;
  });
}

/** Why transition `index` cannot move by `delta` (its priority), if it cannot. */
export function moveTransitionRefusal(node: JsonObject, index: number, delta: -1 | 1): string | undefined {
  const count = Array.isArray(node.transitions) ? node.transitions.length : 0;
  const to = index + delta;
  return to < 0 ? 'This transition is already checked first.' : to >= count ? 'This transition is already checked last.' : undefined;
}

/** Swaps transition `index` with its neighbour: transitions are checked in order. */
export function moveTransition(document: JsonObject, path: readonly Step[], index: number, delta: -1 | 1): JsonObject {
  return updateNode(document, path, (node) => {
    const list = (node.transitions as Json[]).slice();
    [list[index], list[index + delta]] = [list[index + delta], list[index]];
    return { ...node, transitions: list };
  });
}

/** Places the node on the canvas at whole-pixel `x`, `y` (not below 0); other layout fields are kept. */
export function setLayout(document: JsonObject, path: readonly Step[], x: number, y: number): JsonObject {
  return updateNode(document, path, (node) => ({
    ...node,
    layout: { ...(isObject(node.layout) ? node.layout : {}), x: Math.max(0, Math.round(x)), y: Math.max(0, Math.round(y)) },
  }));
}

/** Why the step at `path` cannot become the start step, if it cannot. */
export function setStartRefusal(document: JsonObject, path: readonly Step[], catalog: Catalog): string | undefined {
  if (graphParentPath(document, path, catalog) === undefined) {
    return 'Only a step of a flowchart can be its start step.';
  }

  return (path.at(-1) as { children: number }).children === 0 ? 'This is already the start step.' : undefined;
}

/** Makes the step at `path` the start step (the first child); the others keep their order. */
export function setStart(document: JsonObject, path: readonly Step[]): { document: JsonObject; path: Step[] } {
  const parentPath = path.slice(0, -1);
  const from = (path.at(-1) as { children: number }).children;
  const updated = updateNode(document, parentPath, (graph) => {
    const children = (graph.children as Json[]).slice();
    const [step] = children.splice(from, 1);
    return { ...graph, children: [step, ...children] };
  });
  return { document: updated, path: [...parentPath, { children: 0 }] };
}

/** Removes the transitions of the graph container at `graphPath` whose target is `id` (and of no other node). */
function withoutTransitionsTo(document: JsonObject, graphPath: readonly Step[], id: string): JsonObject {
  return updateNode(document, graphPath, (graph) => {
    let changed = false;
    const children = (graph.children as Json[]).map((child) => {
      if (!isObject(child) || !transitionsOf(child).some((t) => targetOf(t) === id)) {
        return child;
      }

      changed = true;
      const list = (child.transitions as Json[]).filter((t) => !(isObject(t) && targetOf(t) === id));
      const { transitions: _removed, ...rest } = child;
      return carryKey(child, list.length > 0 ? { ...child, transitions: list } : rest);
    });
    return changed ? { ...graph, children } : graph;
  });
}

/** How many transitions of other steps go to the step at `path`. */
export function incomingCount(document: JsonObject, path: readonly Step[]): number {
  const self = nodeAt(document, path);
  const id = idOf(self);
  const parent = nodeAt(document, path.slice(0, -1));
  return id === undefined ? 0 : stepsOf(parent).reduce((n, step) => n + (step === self ? 0 : transitionsOf(step).filter((t) => targetOf(t) === id).length), 0);
}

/**
 * Detaches the step at `path` from its graph (before it is deleted or moved out): the transitions that go to it are
 * removed, and so are its own (they name siblings it leaves behind). Its position stays at `path`.
 */
export function detachStep(document: JsonObject, path: readonly Step[]): JsonObject {
  const id = idOf(nodeAt(document, path));
  const withoutIncoming = id === undefined ? document : withoutTransitionsTo(document, path.slice(0, -1), id);
  return updateNode(withoutIncoming, path, (node) => {
    if (!('transitions' in node)) {
      return node;
    }

    const { transitions: _removed, ...rest } = node;
    return rest;
  });
}

/** Top-level pasted nodes outside a graph lose their transitions and position (they would not be valid there). */
export function asListNodes(nodes: readonly JsonObject[]): JsonObject[] {
  return nodes.map((node) => {
    const { transitions: _t, layout: _l, ...rest } = node;
    return 'transitions' in node || 'layout' in node ? rest : node;
  });
}

/** Top-level pasted steps keep their transitions among themselves; positions move a little so copies do not hide originals. */
export function asPastedSteps(nodes: readonly JsonObject[]): JsonObject[] {
  return nodes.map((node) => {
    const layout = isObject(node.layout) && typeof node.layout.x === 'number' && typeof node.layout.y === 'number' ? node.layout : undefined;
    return layout ? { ...node, layout: { ...layout, x: (layout.x as number) + 24, y: (layout.y as number) + 24 } } : node;
  });
}

// ---------------------------------------------------------------------------------------------------------------------
// The schema version (format §8): a file that uses graphs declares 1.1; the Studio raises it in the same edit.

/** Whether the document uses a graph container, transitions or a layout (format 1.1 features). */
export function usesGraphs(document: JsonObject, catalog: Catalog): boolean {
  return indexDocument(document).entries.some((e) => 'transitions' in e.node || 'layout' in e.node || isGraphNode(e.node, catalog));
}

/** The document with `schemaVersion` 1.1 when it is 1.0 and uses graphs; otherwise the same document. */
export function withGraphSchema(document: JsonObject, catalog: Catalog): JsonObject {
  return document.schemaVersion === '1.0' && usesGraphs(document, catalog) ? { ...document, schemaVersion: '1.1' } : document;
}

// ---------------------------------------------------------------------------------------------------------------------
// Canvas geometry.

export interface Point {
  readonly x: number;
  readonly y: number;
}

const positionsCache = new WeakMap<JsonObject, ReadonlyMap<string, Point>>();

const layoutOf = (node: JsonObject): Point | undefined =>
  isObject(node.layout) && typeof node.layout.x === 'number' && typeof node.layout.y === 'number' && Number.isFinite(node.layout.x) && Number.isFinite(node.layout.y)
    ? { x: node.layout.x, y: node.layout.y }
    : undefined;

/**
 * Where each step (by client key) sits on the canvas: its `layout`, or for steps without one a place in rows by their
 * distance from the start step (unreachable steps in a last row). Computed once per version of the graph container.
 */
export function canvasPositions(graph: JsonObject): ReadonlyMap<string, Point> {
  const cached = positionsCache.get(graph);
  if (cached) {
    return cached;
  }

  const steps = stepsOf(graph);
  const byId = new Map(steps.flatMap((step) => (idOf(step) === undefined ? [] : [[idOf(step)!, step] as const])));
  const level = new Map<JsonObject, number>();
  const queue = steps.length > 0 ? [steps[0]] : [];
  if (steps.length > 0) {
    level.set(steps[0], 0);
  }

  while (queue.length > 0) {
    const step = queue.shift()!;
    for (const transition of transitionsOf(step)) {
      const next = byId.get(targetOf(transition) ?? '');
      if (next && !level.has(next)) {
        level.set(next, level.get(step)! + 1);
        queue.push(next);
      }
    }
  }

  const deepest = Math.max(-1, ...level.values());
  const used = new Map<number, number>();
  const positions = new Map<string, Point>();
  for (const step of steps) {
    const placed = layoutOf(step);
    if (placed) {
      positions.set(keyOf(step), placed);
      continue;
    }

    const row = level.get(step) ?? deepest + 1;
    const column = used.get(row) ?? 0;
    used.set(row, column + 1);
    positions.set(keyOf(step), { x: grid.left + column * grid.column, y: grid.top + row * grid.row });
  }

  positionsCache.set(graph, positions);
  return positions;
}

/** The canvas size that holds every step, with room for arrows that loop around the right side and for new steps below. */
export function canvasSize(positions: Iterable<Point>): { width: number; height: number } {
  let width = 0;
  let height = 0;
  for (const p of positions) {
    width = Math.max(width, p.x + stepSize.width);
    height = Math.max(height, p.y + stepSize.height);
  }

  return { width: width + 160, height: height + 180 };
}

export interface ArrowShape {
  /** SVG path (`d`): a cubic Bézier from the source card's edge to the target card's edge. */
  readonly d: string;
  /** Where the label goes (the middle of the curve). */
  readonly labelAt: Point;
}

const round = (n: number) => Math.round(n * 10) / 10;

/** The arrow from a card at `from` to a card at `to` (top-left corners). */
export function arrowShape(from: Point, to: Point, self: boolean): ArrowShape {
  const { width: w, height: h } = stepSize;
  let p0: Point;
  let c1: Point;
  let c2: Point;
  let p3: Point;
  if (self) {
    p0 = { x: from.x + w, y: from.y + h / 2 - 14 };
    p3 = { x: from.x + w, y: from.y + h / 2 + 14 };
    c1 = { x: p0.x + 70, y: p0.y - 40 };
    c2 = { x: p3.x + 70, y: p3.y + 40 };
  } else if (to.y >= from.y + h + 10) {
    // Down: bottom centre to top centre.
    p0 = { x: from.x + w / 2, y: from.y + h };
    p3 = { x: to.x + w / 2, y: to.y };
    const bend = (p3.y - p0.y) / 2;
    c1 = { x: p0.x, y: p0.y + bend };
    c2 = { x: p3.x, y: p3.y - bend };
  } else if (to.y + h + 10 <= from.y) {
    // Back up (a loop): around the right side of both cards.
    p0 = { x: from.x + w, y: from.y + h / 2 };
    p3 = { x: to.x + w, y: to.y + h / 2 };
    const side = Math.max(p0.x, p3.x) + 80;
    c1 = { x: side, y: p0.y };
    c2 = { x: side, y: p3.y };
  } else {
    // Same band: side to side.
    const right = to.x >= from.x;
    p0 = { x: right ? from.x + w : from.x, y: from.y + h / 2 };
    p3 = { x: right ? to.x : to.x + w, y: to.y + h / 2 };
    const bend = (p3.x - p0.x) / 2;
    c1 = { x: p0.x + bend, y: p0.y };
    c2 = { x: p3.x - bend, y: p3.y };
  }

  const labelAt = { x: (p0.x + 3 * c1.x + 3 * c2.x + p3.x) / 8, y: (p0.y + 3 * c1.y + 3 * c2.y + p3.y) / 8 };
  const d = `M${round(p0.x)} ${round(p0.y)} C${round(c1.x)} ${round(c1.y)} ${round(c2.x)} ${round(c2.y)} ${round(p3.x)} ${round(p3.y)}`;
  return { d, labelAt: { x: round(labelAt.x), y: round(labelAt.y) } };
}

/** The text on an arrow: its label, else its condition, else nothing. */
export function arrowText(transition: JsonObject): string {
  const text = typeof transition.label === 'string' && transition.label !== '' ? transition.label : typeof transition.when === 'string' ? transition.when : transition.when === undefined ? '' : JSON.stringify(transition.when);
  return text.length > 28 ? `${text.slice(0, 27)}…` : text;
}

/**
 * The transition last taken in the graph container whose steps have `stepIds`, from a run's events: the last two
 * steps of this container that started (the engine emits no event for the transition itself, ADR-0037 §5). Only
 * events of the run's own workflow count. Returns `from\0to`, or '' when no transition was taken yet.
 */
export function lastTaken(events: readonly ExecutionEvent[], stepIds: ReadonlySet<string>): string {
  let last: string | undefined;
  let previous: string | undefined;
  for (const event of events) {
    if (event.kind === 'node.started' && !event.parentExecutionId && event.nodeId !== undefined && stepIds.has(event.nodeId)) {
      previous = last;
      last = event.nodeId;
    }
  }

  return previous === undefined || last === undefined ? '' : `${previous}\0${last}`;
}

/** The node's steps as `{ key, node, id }` (children only: a graph container has no slots that are steps). */
export function stepEntries(graph: JsonObject): { key: string; node: JsonObject; id: string | undefined }[] {
  return childSteps(graph)
    .filter((child) => 'children' in child.step)
    .map((child) => ({ key: keyOf(child.node), node: child.node, id: idOf(child.node) }));
}
