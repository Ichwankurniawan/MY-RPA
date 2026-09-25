// W0 spike: document store with snapshot undo/redo (200 steps, typing coalesced) and per-node subscriptions,
// so selection or run-state changes re-render only the affected blocks.
import { useSyncExternalStore } from 'react';
import { type Path, type WorkflowJson, pathKey } from './model';

const MaxUndo = 200;
const CoalesceMs = 1000;

type Listener = () => void;

class Store {
  doc: WorkflowJson | null = null;
  selected: Path | null = null;
  selectedKey: string | null = null;
  errors = new Map<string, string>();
  private past: { doc: WorkflowJson; key: string | null; at: number }[] = [];
  private future: WorkflowJson[] = [];
  private listeners = new Set<Listener>();
  private lastKey: string | null = null;
  private lastAt = 0;

  subscribe = (listener: Listener) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  private emit() {
    for (const l of this.listeners) l();
  }

  load(doc: WorkflowJson) {
    this.doc = doc;
    this.past = [];
    this.future = [];
    this.selected = null;
    this.selectedKey = null;
    this.emit();
  }

  /** Applies an edit; edits with the same key within a short time (typing) form one undo step. */
  apply(next: WorkflowJson, coalesceKey: string | null = null) {
    if (!this.doc || next === this.doc) return;
    const now = performance.now();
    const merge = coalesceKey !== null && coalesceKey === this.lastKey && now - this.lastAt < CoalesceMs;
    if (!merge) {
      this.past.push({ doc: this.doc, key: coalesceKey, at: now });
      if (this.past.length > MaxUndo) this.past.shift();
    }
    this.lastKey = coalesceKey;
    this.lastAt = now;
    this.future = [];
    this.doc = next;
    this.emit();
  }

  undo() {
    const previous = this.past.pop();
    if (!previous || !this.doc) return;
    this.future.push(this.doc);
    this.doc = previous.doc;
    this.lastKey = null;
    this.emit();
  }

  redo() {
    const next = this.future.pop();
    if (!next || !this.doc) return;
    this.past.push({ doc: this.doc, key: null, at: performance.now() });
    this.doc = next;
    this.lastKey = null;
    this.emit();
  }

  select(path: Path | null) {
    this.selected = path;
    this.selectedKey = path ? pathKey(path) : null;
    this.emit();
  }

  setErrors(errors: Map<string, string>) {
    this.errors = errors;
    this.emit();
  }

  get undoDepth() {
    return this.past.length;
  }
}

export const store = new Store();

export const useDoc = () => useSyncExternalStore(store.subscribe, () => store.doc);
export const useSelected = () => useSyncExternalStore(store.subscribe, () => store.selected);
export const useIsSelected = (key: string) =>
  useSyncExternalStore(store.subscribe, () => store.selectedKey === key);
export const useNodeError = (nodeId: string) => useSyncExternalStore(store.subscribe, () => store.errors.get(nodeId));
