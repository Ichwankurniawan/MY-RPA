// Crash recovery (W6, ADR-0031): the unsaved document of a file is kept in this browser's local storage, so a closed tab
// or a crash does not lose it. It is offered back when the file is opened again. Storage may be missing or full
// (private windows, quotas): every access is guarded, and recovery is a convenience, never the only copy of a save.

export interface Draft {
  /** The serialized unsaved document (v1.0 JSON). */
  readonly text: string;
  /** The ETag of the file version the edits started from. */
  readonly etag: string;
  /** When the draft was written (ISO 8601). */
  readonly savedAt: string;
}

export interface DraftStore {
  get(project: string, path: string): Draft | undefined;
  put(project: string, path: string, draft: Draft): void;
  remove(project: string, path: string): void;
}

const key = (project: string, path: string) => `myrpa.draft:${project}/${path}`;

/** Drafts in `storage` (the browser's localStorage by default); failures are ignored. */
export function storageDrafts(storage: () => Storage | undefined = () => globalThis.localStorage): DraftStore {
  return {
    get(project, path) {
      try {
        const text = storage()?.getItem(key(project, path));
        const draft = text ? (JSON.parse(text) as Partial<Draft>) : undefined;
        return typeof draft?.text === 'string' && typeof draft.etag === 'string' && typeof draft.savedAt === 'string' ? (draft as Draft) : undefined;
      } catch {
        return undefined;
      }
    },
    put(project, path, draft) {
      try {
        storage()?.setItem(key(project, path), JSON.stringify(draft));
      } catch {
        // Full or unavailable: there is just no recovery copy.
      }
    },
    remove(project, path) {
      try {
        storage()?.removeItem(key(project, path));
      } catch {
        // Unavailable: nothing to remove.
      }
    },
  };
}

/** Drafts kept in memory (tests). */
export function memoryDrafts(): DraftStore & { readonly entries: Map<string, Draft> } {
  const entries = new Map<string, Draft>();
  return {
    entries,
    get: (project, path) => entries.get(key(project, path)),
    put: (project, path, draft) => void entries.set(key(project, path), draft),
    remove: (project, path) => void entries.delete(key(project, path)),
  };
}
