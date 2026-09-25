import { useSyncExternalStore } from 'react';

/** A minimal external store: components subscribe to slices, so a change re-renders only the components whose slice changed. */
export interface Store<S> {
  get(): S;
  set(update: Partial<S> | ((state: S) => Partial<S>)): void;
  subscribe(listener: () => void): () => void;
}

export function createStore<S>(initial: S): Store<S> {
  let state = initial;
  const listeners = new Set<() => void>();
  return {
    get: () => state,
    set(update) {
      state = { ...state, ...(typeof update === 'function' ? update(state) : update) };
      listeners.forEach((listener) => listener());
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
}

/** Subscribes to `selector(state)`; the selector must return a primitive or a reference held by the state. */
export function useStore<S, T>(store: Store<S>, selector: (state: S) => T): T {
  return useSyncExternalStore(store.subscribe, () => selector(store.get()));
}
