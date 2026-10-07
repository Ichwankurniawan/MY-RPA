// The Studio instance for the component tree, and slice subscriptions to its store.

import { createContext, useContext } from 'react';
import { useStore } from './store';
import type { Studio, StudioState } from './studio';

export const StudioContext = createContext<Studio | null>(null);

export function useStudio(): Studio {
  const studio = useContext(StudioContext);
  if (studio === null) {
    throw new Error('No Studio in context.');
  }

  return studio;
}

/** Subscribes to one slice of the Studio's state (see `useStore`). */
export function useStudioState<T>(selector: (state: StudioState) => T): T {
  return useStore(useStudio().store, selector);
}
