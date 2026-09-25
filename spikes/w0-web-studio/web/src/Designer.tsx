// W0 spike: hierarchical nested-block designer. Blocks re-render only when their own node object changes
// (structural sharing + memo); selection and errors are per-block subscriptions.
import React, { createContext, memo, useContext, type KeyboardEvent } from 'react';
import { DndContext, KeyboardSensor, PointerSensor, useDraggable, useDroppable, useSensor, useSensors, type DragEndEvent } from '@dnd-kit/core';
import { type NodeJson, type Path, type WorkflowJson, getNode, insertChild, moveWithinList, pathKey, removeNode } from './model';
import { store, useIsSelected, useNodeError } from './store';

export interface Descriptor {
  type: string;
  displayName: string;
  category: string;
  description?: string;
  allowsChildren: boolean;
  properties: { name: string; kind: string; required: boolean; description?: string; allowedValues: string[] }[];
  slots: { name: string; required: boolean; prefix: boolean }[];
}

// ?nodnd: replace dnd-kit with plain pointer hit-testing (document.elementFromPoint), to attribute costs.
export const DND = !new URLSearchParams(location.search).has('nodnd');
const noDrag = { attributes: {}, listeners: undefined, setNodeRef: () => {}, isDragging: false };

export const CatalogContext = createContext<Map<string, Descriptor>>(new Map());

type ZoneData = { parent: Path; index: number };
type DragData = { path: Path };

export function moveNode(doc: WorkflowJson, from: Path, parent: Path, index: number): WorkflowJson | null {
  const fromKey = pathKey(from);
  const parentKey = pathKey(parent);
  if (from.length === 0 || parentKey === fromKey || parentKey.startsWith(fromKey + '/')) {
    return null; // not into itself
  }
  const node = getNode(doc.root, from);
  const last = from[from.length - 1];
  let target = index;
  if ('c' in last && pathKey(from.slice(0, -1)) === parentKey && last.c < index) {
    target--; // removal shifts later siblings
  }
  // A removal before the target parent in the same list shifts the parent's own path.
  const removed = removeNode(doc, from);
  const adjusted = adjustAfterRemoval(parent, from);
  return insertChild(removed, adjusted, target, node);
}

function adjustAfterRemoval(path: Path, removed: Path): Path {
  const parent = removed.slice(0, -1);
  const last = removed[removed.length - 1];
  if (!('c' in last) || path.length <= parent.length || pathKey(path.slice(0, parent.length)) !== pathKey(parent)) {
    return path;
  }
  const step = path[parent.length];
  if ('c' in step && step.c > last.c) {
    return [...path.slice(0, parent.length), { c: step.c - 1 }, ...path.slice(parent.length + 1)];
  }
  return path;
}

export function Designer({ doc }: { doc: WorkflowJson }) {
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 4 } }), useSensor(KeyboardSensor));
  const onDragEnd = (event: DragEndEvent) => {
    const drag = event.active.data.current as DragData | undefined;
    const zone = event.over?.data.current as ZoneData | undefined;
    if (!drag || !zone || !store.doc) return;
    const next = moveNode(store.doc, drag.path, zone.parent, zone.index);
    if (next) store.apply(next);
  };
  const tree = (
    <div role="tree" aria-label="Workflow designer" className="designer" onPointerDown={DND ? undefined : startPointerDrag}>
      <Block node={doc.root} path={[]} pathId="" />
    </div>
  );
  return DND ? <DndContext sensors={sensors} onDragEnd={onDragEnd}>{tree}</DndContext> : tree;
}

const Block = memo(
  function Block({ node, path, pathId }: { node: NodeJson; path: Path; pathId: string }) {
    const catalog = useContext(CatalogContext);
    const descriptor = catalog.get(node.type);
    const selected = useIsSelected(pathId);
    const error = useNodeError(node.id);
    // Stable for the page's lifetime, so the hook order never changes between renders.
    const { attributes, listeners, setNodeRef, isDragging } = DND ? useDraggable({ id: `node:${pathId}`, data: { path } satisfies DragData, disabled: path.length === 0 }) : noDrag;
    const summaryProperty = descriptor?.properties.find((p) => p.required && node.properties?.[p.name] !== undefined) ?? descriptor?.properties[0];
    const summary = summaryProperty ? node.properties?.[summaryProperty.name] : undefined;

    const onKeyDown = (e: KeyboardEvent) => {
      if (e.target !== e.currentTarget || !store.doc) return;
      if (e.altKey && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) {
        const moved = moveWithinList(store.doc, path, e.key === 'ArrowUp' ? -1 : 1);
        if (moved) {
          store.apply(moved.doc);
          store.select(moved.path);
        }
        e.preventDefault();
      } else if (e.key === 'Delete' && path.length > 0) {
        store.apply(removeNode(store.doc, path));
        store.select(path.slice(0, -1));
        e.preventDefault();
      }
    };

    return (
      <div
        ref={setNodeRef}
        {...attributes}
        role="treeitem"
        aria-selected={selected}
        aria-label={`${descriptor?.displayName ?? node.type} ${node.id}`}
        tabIndex={selected ? 0 : -1}
        className={`block${selected ? ' selected' : ''}${error ? ' error' : ''}${descriptor ? '' : ' unknown'}${isDragging ? ' dragging' : ''}`}
        data-path={pathId}
        onClick={(e) => {
          e.stopPropagation();
          store.select(path);
        }}
        {...listeners}
        onKeyDown={(e) => {
          onKeyDown(e);
          if (!e.defaultPrevented) listeners?.onKeyDown?.(e);
        }}
      >
        <div className="head">
          <span className="title">{node.displayName ?? descriptor?.displayName ?? node.type}</span>
          <span className="type">{node.type}</span>
        </div>
        {summary !== undefined && <div className="summary">{summaryProperty!.name}: {String(summary)}</div>}
        {error && <div className="err">{error}</div>}
        {descriptor?.allowsChildren && (
          <div role="group" className="children">
            {(node.children ?? []).map((child, i) => (
              <ChildEntry key={i} child={child} path={path} pathId={pathId} index={i} />
            ))}
            <Zone parent={path} parentId={pathId} index={node.children?.length ?? 0} hint={(node.children?.length ?? 0) === 0} />
          </div>
        )}
        {Object.entries(node.slots ?? {}).map(([name, child]) => (
          <div key={name} className="slot" role="group" aria-label={name}>
            <div className="slot-label">{name}</div>
            <Block node={child} path={[...path, { s: name }]} pathId={`${pathId}${pathId ? '/' : ''}s:${name}`} />
          </div>
        ))}
      </div>
    );
  },
  (a, b) => a.node === b.node && a.pathId === b.pathId,
);

function ChildEntry({ child, path, pathId, index }: { child: NodeJson; path: Path; pathId: string; index: number }) {
  return (
    <>
      <Zone parent={path} parentId={pathId} index={index} hint={false} />
      <Block node={child} path={[...path, { c: index }]} pathId={`${pathId}${pathId ? '/' : ''}c${index}`} />
    </>
  );
}

const Zone = memo(function Zone({ parent, parentId, index, hint }: { parent: Path; parentId: string; index: number; hint: boolean }) {
  const { setNodeRef, isOver } = DND ? useDroppable({ id: `zone:${parentId}:${index}`, data: { parent, index } satisfies ZoneData }) : { setNodeRef: () => {}, isOver: false };
  return <div ref={setNodeRef} data-parent={parentId} data-index={index} className={`zone${isOver ? ' over' : ''}${hint ? ' hint' : ''}`}>{hint ? 'Drop activities here' : null}</div>;
}, (a, b) => a.parentId === b.parentId && a.index === b.index && a.hint === b.hint);

const parsePath = (id: string): Path => (id === '' ? [] : id.split('/').map((p) => (p.startsWith('s:') ? { s: p.slice(2) } : { c: Number(p.slice(1)) })));

/** The dnd-kit alternative: hit-test the zone under the pointer; highlight by class, no React re-render per move. */
function startPointerDrag(down: React.PointerEvent) {
  const block = (down.target as HTMLElement).closest<HTMLElement>('.block');
  if (!block || block.dataset.path === '' || down.button !== 0) return;
  const from = parsePath(block.dataset.path!);
  let over: HTMLElement | null = null;
  let active = false;
  const move = (e: PointerEvent) => {
    if (!active && Math.hypot(e.clientX - down.clientX, e.clientY - down.clientY) < 4) return;
    active = true;
    const zone = document.elementFromPoint(e.clientX, e.clientY)?.closest<HTMLElement>('.zone') ?? null;
    if (zone !== over) {
      over?.classList.remove('over');
      zone?.classList.add('over');
      over = zone;
    }
  };
  const up = () => {
    document.removeEventListener('pointermove', move);
    document.removeEventListener('pointerup', up);
    over?.classList.remove('over');
    if (active && over && store.doc) {
      const next = moveNode(store.doc, from, parsePath(over.dataset.parent!), Number(over.dataset.index));
      if (next) store.apply(next);
    }
  };
  document.addEventListener('pointermove', move);
  document.addEventListener('pointerup', up);
}
