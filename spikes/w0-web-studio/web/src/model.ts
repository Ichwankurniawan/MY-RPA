// W0 spike: the v1.0 workflow JSON edited directly as an immutable tree (no web-specific format).
// Unknown fields are kept because nodes are copied with object spread.

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

export interface NodeJson {
  id: string;
  type: string;
  displayName?: string;
  properties?: Record<string, Json>;
  children?: NodeJson[];
  slots?: Record<string, NodeJson>;
  [unknown: string]: Json | undefined | NodeJson[] | Record<string, NodeJson> | Record<string, Json>;
}

export interface WorkflowJson {
  schemaVersion: string;
  id: string;
  name: string;
  version: string;
  arguments?: Json[];
  variables?: Json[];
  root: NodeJson;
  [unknown: string]: Json | NodeJson | undefined;
}

export type Step = { c: number } | { s: string };
export type Path = readonly Step[];

export const pathKey = (path: Path): string => path.map((s) => ('c' in s ? `c${s.c}` : `s:${s.s}`)).join('/');

export function getNode(root: NodeJson, path: Path): NodeJson {
  let node = root;
  for (const step of path) {
    node = 'c' in step ? node.children![step.c] : node.slots![step.s];
  }
  return node;
}

/** Replaces the node at `path`; only the node's ancestors are copied (structural sharing). */
export function updateNode(root: NodeJson, path: Path, update: (node: NodeJson) => NodeJson): NodeJson {
  if (path.length === 0) {
    return update(root);
  }
  const [step, ...rest] = path;
  if ('c' in step) {
    const children = root.children!.slice();
    children[step.c] = updateNode(children[step.c], rest, update);
    return { ...root, children };
  }
  return { ...root, slots: { ...root.slots, [step.s]: updateNode(root.slots![step.s], rest, update) } };
}

export function setProperty(doc: WorkflowJson, path: Path, name: string, value: Json | undefined): WorkflowJson {
  return {
    ...doc,
    root: updateNode(doc.root, path, (node) => {
      const properties = { ...node.properties };
      if (value === undefined || value === '') {
        delete properties[name];
      } else {
        properties[name] = value;
      }
      return { ...node, properties };
    }),
  };
}

export function removeNode(doc: WorkflowJson, path: Path): WorkflowJson {
  const parent = path.slice(0, -1);
  const last = path[path.length - 1];
  return {
    ...doc,
    root: updateNode(doc.root, parent, (node) =>
      'c' in last
        ? { ...node, children: node.children!.filter((_, i) => i !== last.c) }
        : { ...node, slots: Object.fromEntries(Object.entries(node.slots!).filter(([k]) => k !== last.s)) }),
  };
}

export function insertChild(doc: WorkflowJson, parent: Path, index: number, child: NodeJson): WorkflowJson {
  return {
    ...doc,
    root: updateNode(doc.root, parent, (node) => {
      const children = (node.children ?? []).slice();
      children.splice(index, 0, child);
      return { ...node, children };
    }),
  };
}

/** Moves a child one position up or down inside its list (the keyboard alternative to dragging). */
export function moveWithinList(doc: WorkflowJson, path: Path, delta: -1 | 1): { doc: WorkflowJson; path: Path } | null {
  const last = path[path.length - 1];
  if (!last || !('c' in last)) {
    return null;
  }
  const parent = path.slice(0, -1);
  const siblings = getNode(doc.root, parent).children!;
  const target = last.c + delta;
  if (target < 0 || target >= siblings.length) {
    return null;
  }
  const next = {
    ...doc,
    root: updateNode(doc.root, parent, (node) => {
      const children = node.children!.slice();
      [children[last.c], children[target]] = [children[target], children[last.c]];
      return { ...node, children };
    }),
  };
  return { doc: next, path: [...parent, { c: target }] };
}

export function countNodes(node: NodeJson): number {
  return 1 + (node.children ?? []).reduce((n, c) => n + countNodes(c), 0) + Object.values(node.slots ?? {}).reduce((n, c) => n + countNodes(c), 0);
}

/** Same shape as the Phase 5 baseline: root Sequence, groups of 1 Sequence + 9 Log (3,001 nodes for 3,000). */
export function flatDocument(nodes: number): WorkflowJson {
  const groups = Math.floor(nodes / 10);
  return {
    schemaVersion: '1.0',
    id: 'big',
    name: 'Flat',
    version: '1.0.0',
    root: {
      id: 'main',
      type: 'Core.Sequence',
      children: Array.from({ length: groups }, (_, i) => ({
        id: `seq-${i}`,
        type: 'Core.Sequence',
        children: Array.from({ length: 9 }, (_, j) => ({ id: `log-${i}-${j}`, type: 'Core.Log', properties: { message: `'item ${i}.${j}'` } })),
      })),
    },
  };
}

/** Deeper, mixed structure (If/TryCatch slots, depth up to 7): 10 nodes per group. */
export function nestedDocument(nodes: number): WorkflowJson {
  const groups = Math.floor(nodes / 10);
  const group = (i: number): NodeJson => ({
    id: `grp-${i}`,
    type: 'Core.Sequence',
    children: [
      { id: `set-${i}`, type: 'Core.Assign', properties: { to: 'x', value: 'x + 1' } },
      {
        id: `guard-${i}`,
        type: 'Core.TryCatch',
        slots: {
          try: {
            id: `check-${i}`,
            type: 'Core.If',
            properties: { condition: 'x > 0' },
            slots: {
              then: {
                id: `then-${i}`,
                type: 'Core.Sequence',
                children: [
                  { id: `say-${i}`, type: 'Core.Log', properties: { message: `'positive ' + x` } },
                  { id: `inner-${i}`, type: 'Core.If', properties: { condition: 'x % 2 == 0' }, slots: { then: { id: `even-${i}`, type: 'Core.Log', properties: { message: `'even'` } }, else: { id: `odd-${i}`, type: 'Core.Log', properties: { message: `'odd'` } } } },
                ],
              },
              else: { id: `neg-${i}`, type: 'Core.Log', properties: { message: `'not positive'` } },
            },
          },
          catch: { id: `err-${i}`, type: 'Core.Log', properties: { message: `'failed'` } },
        },
      },
    ],
  });
  return {
    schemaVersion: '1.0',
    id: 'nested',
    name: 'Nested',
    version: '1.0.0',
    variables: [{ name: 'x', type: 'Int', default: 0 }],
    root: { id: 'main', type: 'Core.Sequence', children: Array.from({ length: groups }, (_, i) => group(i)) },
  };
}
