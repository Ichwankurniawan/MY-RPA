// Per-browser UI preferences (UX-2, studio-ux-plan.md): favorite and recent activities, panel sizes. Like drafts.ts,
// storage may be missing or full (private windows, quotas): every access is guarded, and a preference is a convenience.

export interface PreferenceStore {
  /** The stored JSON value, or undefined when missing, unreadable or of the wrong shape. */
  read<T>(key: string, valid: (value: unknown) => value is T): T | undefined;
  write(key: string, value: unknown): void;
}

export const preferenceKeys = {
  favorites: 'myrpa.ui.favorites',
  recent: 'myrpa.ui.recent',
  panes: 'myrpa.ui.panes',
} as const;

/** Preferences in `storage` (the browser's localStorage by default); failures are ignored. */
export function storagePreferences(storage: () => Storage | undefined = () => globalThis.localStorage): PreferenceStore {
  return {
    read(key, valid) {
      try {
        const text = storage()?.getItem(key);
        const value: unknown = text ? JSON.parse(text) : undefined;
        return valid(value) ? value : undefined;
      } catch {
        return undefined;
      }
    },
    write(key, value) {
      try {
        storage()?.setItem(key, JSON.stringify(value));
      } catch {
        // Full or unavailable: the preference lasts for this page only.
      }
    },
  };
}

/** Preferences kept in memory (tests). */
export function memoryPreferences(): PreferenceStore & { readonly values: Map<string, string> } {
  const values = new Map<string, string>();
  return {
    values,
    read(key, valid) {
      const text = values.get(key);
      const value: unknown = text ? JSON.parse(text) : undefined;
      return valid(value) ? value : undefined;
    },
    write(key, value) {
      values.set(key, JSON.stringify(value));
    },
  };
}

/** A list of activity type names. */
export const isTypeList = (value: unknown): value is string[] => Array.isArray(value) && value.every((v) => typeof v === 'string');
