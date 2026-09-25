// The Studio's state and commands. The Web Studio owns the editing model; the server owns projects, files,
// validation and execution; the engine knows nothing about this client.

import { ApiError, type StudioApi } from './api';
import {
  createNode,
  deleteRefusal,
  indexDocument,
  insertNode,
  insertionPoint,
  isObject,
  keyOf,
  moveNode,
  moveRefusal,
  openWorkflow,
  removeNode,
  selectionAfterDelete,
  serialize,
  setDisplayName,
  setProperty,
  type Step,
} from './document';
import { RunEventStream, type EventSourceFactory } from './events';
import { createStore, type Store } from './store';
import type { ActivityDescriptor, Diagnostic, ExecutionError, ExecutionEvent, JsonObject, PropertyDescriptor, RunStatus, WorkflowFile } from './types';

export interface OpenFile {
  readonly project: string;
  readonly path: string;
  /** The server's ETag of the version the document was read from or last saved as. */
  readonly etag: string;
  /** Set when the file cannot be saved back unchanged; editing and saving are disabled. */
  readonly readOnlyReason?: string;
}

export interface RunView {
  readonly runId?: string;
  /** Starting, Running, then the engine's final status (Succeeded, Failed, Cancelled, TimedOut), or NotStarted. */
  readonly status: string;
  readonly error?: ExecutionError;
  readonly result?: RunStatus['result'];
}

/** One undo or redo step: a complete document version (structurally shared) and the selection that went with it. */
export interface HistoryEntry {
  readonly document: JsonObject;
  readonly selectedKey?: string;
  /** What the step did, e.g. "Insert Log" (shown on the Undo and Redo buttons). */
  readonly label: string;
}

export interface StudioState {
  readonly connection: 'connecting' | 'ready' | 'signed-out' | 'failed';
  /** The latest status message (announced to screen readers). */
  readonly message?: string;
  readonly activities: readonly ActivityDescriptor[];
  readonly catalog: ReadonlyMap<string, ActivityDescriptor>;
  readonly projects: readonly string[];
  readonly project?: string;
  readonly files: readonly WorkflowFile[];
  readonly file?: OpenFile;
  readonly document?: JsonObject;
  /** The document as last opened or saved; the document is dirty when it is a different object. */
  readonly saved?: JsonObject;
  readonly selectedKey?: string;
  /** Versions before the current one (most recent last), at most `maxUndo`. */
  readonly undo: readonly HistoryEntry[];
  /** Versions undone (most recent last); cleared by any new edit. */
  readonly redo: readonly HistoryEntry[];
  readonly diagnostics?: readonly Diagnostic[];
  /** The document version the diagnostics describe. */
  readonly validated?: JsonObject;
  readonly errorNodeIds: ReadonlySet<string>;
  readonly busy?: 'opening' | 'saving' | 'validating' | 'starting';
  readonly run?: RunView;
  readonly events: readonly ExecutionEvent[];
  /** Node id → Running, Succeeded, Failed or Cancelled for the current run. */
  readonly nodeStatus: ReadonlyMap<string, string>;
}

export const maxEvents = 1000;

/** Undo steps kept per document (ADR-0021: at least 200). */
export const maxUndo = 200;

export const isDirty = (state: StudioState): boolean => state.document !== state.saved;

function selectedPath(state: StudioState): readonly Step[] | undefined {
  return state.document && state.selectedKey ? indexDocument(state.document).byKey.get(state.selectedKey)?.path : undefined;
}

function editRefusal(state: StudioState): string | undefined {
  if (state.document === undefined) {
    return 'Open a workflow first.';
  }

  return state.file?.readOnlyReason !== undefined ? `The workflow is read-only: ${state.file.readOnlyReason}` : undefined;
}

/** Why an activity cannot be inserted at the selection now (undefined when it can). */
export function insertRefusal(state: StudioState): string | undefined {
  const path = selectedPath(state);
  const refusal = editRefusal(state);
  if (refusal !== undefined || path === undefined) {
    return refusal ?? 'Select where to insert.';
  }

  const placement = insertionPoint(state.document!, path, state.catalog);
  return typeof placement === 'string' ? placement : undefined;
}

/** Why the selected node cannot be deleted now (undefined when it can). */
export function deleteRefusalOf(state: StudioState): string | undefined {
  const path = selectedPath(state);
  return editRefusal(state) ?? (path === undefined ? 'Select an activity to delete.' : deleteRefusal(path));
}

/** Why the selected node cannot move up (-1) or down (+1) now (undefined when it can). */
export function moveRefusalOf(state: StudioState, delta: -1 | 1): string | undefined {
  const path = selectedPath(state);
  return editRefusal(state) ?? (path === undefined ? 'Select an activity to move.' : moveRefusal(state.document!, path, delta));
}

export class Studio {
  readonly store: Store<StudioState>;
  private readonly events: RunEventStream;
  /** Consecutive edits with the same key (typing in one field) form one undo step; anything else ends the group. */
  private mergeKey?: string;

  constructor(
    private readonly api: StudioApi,
    createSource: EventSourceFactory,
  ) {
    this.store = createStore<StudioState>({
      connection: 'connecting',
      activities: [],
      catalog: new Map(),
      projects: [],
      files: [],
      undo: [],
      redo: [],
      errorNodeIds: new Set(),
      events: [],
      nodeStatus: new Map(),
    });
    this.events = new RunEventStream(api, createSource, (event) => this.receive(event), (message) => this.say(message));
  }

  private get state(): StudioState {
    return this.store.get();
  }

  private say(message: string): void {
    this.store.set({ message });
  }

  async connect(): Promise<void> {
    try {
      const info = await this.api.info();
      const activities = await this.api.activities();
      this.store.set({
        connection: 'ready',
        projects: info.projects,
        activities,
        catalog: new Map(activities.map((activity) => [activity.type, activity])),
        message: `Connected to ${info.name} (${info.mode} mode).`,
      });
      if (info.projects.length > 0) {
        await this.selectProject(info.projects[0]);
      }
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        this.store.set({ connection: 'signed-out', message: error.message });
      } else {
        this.store.set({ connection: 'failed', message: `Cannot reach MyRPA.Server: ${(error as Error).message}` });
      }
    }
  }

  async selectProject(project: string): Promise<void> {
    try {
      const files = await this.api.workflows(project);
      this.store.set({ project, files });
    } catch (error) {
      this.say(`Cannot list the project's workflows: ${(error as Error).message}`);
    }
  }

  /** Opens a workflow file of the current project. The caller confirms discarding unsaved changes first. */
  async open(path: string): Promise<void> {
    const project = this.state.project;
    if (project === undefined) {
      return;
    }

    this.store.set({ busy: 'opening' });
    try {
      const { text, etag } = await this.api.readWorkflow(project, path);
      const opened = openWorkflow(text);
      if (!opened.ok) {
        this.store.set({ busy: undefined, message: `${path}: ${opened.error}` });
        return;
      }

      const root = opened.document.root;
      this.mergeKey = undefined;
      this.store.set({
        undo: [],
        redo: [],
        busy: undefined,
        file: { project, path, etag, readOnlyReason: opened.readOnlyReason },
        document: opened.document,
        saved: opened.document,
        selectedKey: isObject(root) ? keyOf(root) : undefined,
        diagnostics: undefined,
        validated: undefined,
        errorNodeIds: new Set(),
        message: opened.readOnlyReason ? `Opened ${path} read-only: ${opened.readOnlyReason}` : `Opened ${path}.`,
      });
    } catch (error) {
      this.store.set({ busy: undefined, message: `Cannot open ${path}: ${(error as Error).message}` });
    }
  }

  select(key: string): void {
    if (this.state.selectedKey !== key) {
      this.mergeKey = undefined;
      this.store.set({ selectedKey: key });
    }
  }

  /** Selects the node a diagnostic belongs to, if it names one. */
  selectNodeId(nodeId: string): void {
    const document = this.state.document;
    const key = document ? indexDocument(document).byNodeId.get(nodeId) : undefined;
    if (key !== undefined) {
      this.select(key);
    }
  }

  editProperty(key: string, descriptor: PropertyDescriptor, text: string): void {
    this.edit(key, `Edit ${descriptor.name}`, `property:${key}:${descriptor.name}`, (document, path) => setProperty(document, path, descriptor, text));
  }

  editDisplayName(key: string, text: string): void {
    this.edit(key, 'Edit display name', `displayName:${key}`, (document, path) => setDisplayName(document, path, text));
  }

  private edit(key: string, label: string, mergeKey: string, apply: (document: JsonObject, path: readonly Step[]) => JsonObject): void {
    const { document } = this.state;
    const entry = document ? indexDocument(document).byKey.get(key) : undefined;
    if (document !== undefined && entry !== undefined && editRefusal(this.state) === undefined) {
      this.commit(apply(document, entry.path), this.state.selectedKey, label, mergeKey);
    }
  }

  /**
   * Records an edit: the current version goes onto the undo list (unless the edit continues the same typing group) and
   * the redo list is cleared. Versions share every unchanged subtree, so a step costs only the copied path.
   */
  private commit(next: JsonObject, selectedKey: string | undefined, label: string, mergeKey?: string): void {
    const state = this.state;
    if (state.document === undefined || next === state.document) {
      return;
    }

    const merge = mergeKey !== undefined && mergeKey === this.mergeKey && state.undo.length > 0;
    const undo = merge ? state.undo : [...state.undo, { document: state.document, selectedKey: state.selectedKey, label }].slice(-maxUndo);
    this.mergeKey = mergeKey;
    this.store.set({ document: next, selectedKey, undo, redo: [] });
  }

  /** Inserts a new activity of `type` at the selection (see `insertionPoint`) and selects it. */
  insertActivity(type: string): void {
    const state = this.state;
    const activity = state.catalog.get(type);
    const refusal = activity === undefined ? `'${type}' is not in the activity catalog.` : insertRefusal(state);
    if (activity === undefined || refusal !== undefined) {
      this.say(`Cannot insert: ${refusal}`);
      return;
    }

    const document = state.document!;
    const placement = insertionPoint(document, selectedPath(state)!, state.catalog);
    if (typeof placement === 'string') {
      return;
    }

    const node = createNode(document, activity);
    this.commit(insertNode(document, placement, node).document, keyOf(node), `Insert ${activity.displayName}`);
    this.say(`Inserted ${activity.displayName} as ${node.id as string}.`);
  }

  /** Deletes the selected node; the next sibling, else the previous one, else the parent is selected. */
  deleteSelected(): void {
    const state = this.state;
    const refusal = deleteRefusalOf(state);
    if (refusal !== undefined) {
      this.say(`Cannot delete: ${refusal}`);
      return;
    }

    const document = state.document!;
    const path = selectedPath(state)!;
    const id = nodeIdOf(document, state.selectedKey!);
    const next = selectionAfterDelete(document, path);
    this.commit(removeNode(document, path), keyOf(next), `Delete ${id}`);
    this.say(`Deleted ${id}.`);
  }

  /** Moves the selected node up (-1) or down (+1) within its list; it stays selected. */
  moveSelected(delta: -1 | 1): void {
    const state = this.state;
    const refusal = moveRefusalOf(state, delta);
    if (refusal !== undefined) {
      this.say(`Cannot move: ${refusal}`);
      return;
    }

    const id = nodeIdOf(state.document!, state.selectedKey!);
    const direction = delta < 0 ? 'up' : 'down';
    this.commit(moveNode(state.document!, selectedPath(state)!, delta).document, state.selectedKey, `Move ${id} ${direction}`);
    this.say(`Moved ${id} ${direction}.`);
  }

  /** Restores the version before the last edit, with the selection it had. */
  undo(): void {
    const state = this.state;
    const entry = state.undo.at(-1);
    if (entry === undefined || state.document === undefined) {
      return;
    }

    this.mergeKey = undefined;
    this.store.set({
      document: entry.document,
      selectedKey: existingKey(entry.document, entry.selectedKey),
      undo: state.undo.slice(0, -1),
      redo: [...state.redo, { document: state.document, selectedKey: state.selectedKey, label: entry.label }],
      message: `Undid: ${entry.label}.`,
    });
  }

  /** Reapplies the last undone edit. */
  redo(): void {
    const state = this.state;
    const entry = state.redo.at(-1);
    if (entry === undefined || state.document === undefined) {
      return;
    }

    this.mergeKey = undefined;
    this.store.set({
      document: entry.document,
      selectedKey: existingKey(entry.document, entry.selectedKey),
      undo: [...state.undo, { document: state.document, selectedKey: state.selectedKey, label: entry.label }],
      redo: state.redo.slice(0, -1),
      message: `Redid: ${entry.label}.`,
    });
  }

  async validate(): Promise<void> {
    const document = this.state.document;
    if (document === undefined) {
      return;
    }

    this.store.set({ busy: 'validating' });
    try {
      const result = await this.api.validate(document);
      this.showDiagnostics(result.diagnostics, document);
      const errors = result.diagnostics.filter((d) => d.severity === 'Error').length;
      this.store.set({
        busy: undefined,
        message: result.valid
          ? `Valid${result.diagnostics.length > 0 ? ` with ${result.diagnostics.length} warning(s)` : ''}.`
          : `${errors} error(s) found.`,
      });
    } catch (error) {
      this.store.set({ busy: undefined, message: `Validation failed: ${(error as Error).message}` });
    }
  }

  private showDiagnostics(diagnostics: readonly Diagnostic[], document: JsonObject): void {
    const errorNodeIds = new Set(diagnostics.filter((d) => d.severity === 'Error' && d.nodeId).map((d) => d.nodeId as string));
    this.store.set({ diagnostics, validated: document, errorNodeIds });
  }

  /** Saves over the version that was opened (ETag/If-Match). A 412 means the file changed on disk meanwhile. */
  async save(): Promise<void> {
    const { document, file } = this.state;
    if (document === undefined || file === undefined || file.readOnlyReason !== undefined) {
      return;
    }

    this.mergeKey = undefined;
    this.store.set({ busy: 'saving' });
    try {
      const etag = await this.api.saveWorkflow(file.project, file.path, serialize(document), file.etag);
      this.store.set((state) => ({ busy: undefined, file: { ...file, etag }, saved: document, message: `Saved ${file.path}${state.document === document ? '' : ' (newer edits are not saved yet)'}.` }));
    } catch (error) {
      const message =
        error instanceof ApiError && error.status === 412
          ? `Not saved: ${file.path} changed on disk since it was opened. Reopen it to see the current version.`
          : `Not saved: ${(error as Error).message}`;
      this.store.set({ busy: undefined, message });
    }
  }

  /** Runs the file; an unsaved document runs as if it were saved at its path. Events arrive on the tab's stream. */
  async run(): Promise<void> {
    const state = this.state;
    const { document, file } = state;
    if (document === undefined || file === undefined) {
      return;
    }

    this.store.set({ busy: 'starting', run: { status: 'Starting' }, events: [], nodeStatus: new Map() });
    let runId: string;
    try {
      runId = await this.api.startRun(file.project, file.path, isDirty(state) ? document : undefined);
    } catch (error) {
      if (error instanceof ApiError && error.diagnostics) {
        this.showDiagnostics(error.diagnostics, document);
        this.store.set({ busy: undefined, run: { status: 'NotStarted' }, message: 'Not started: the workflow has validation errors.' });
      } else {
        this.store.set({ busy: undefined, run: { status: 'NotStarted' }, message: `Not started: ${(error as Error).message}` });
      }

      return;
    }

    this.store.set({ busy: undefined, run: { runId, status: 'Running' }, message: `Run ${runId} started.` });
    try {
      await this.events.follow(runId);
    } catch (error) {
      this.say(`Cannot follow run ${runId}: ${(error as Error).message}`);
    }
  }

  private receive(event: ExecutionEvent): void {
    const run = this.state.run;
    if (run?.runId !== event.runId) {
      return;
    }

    const isRun = !event.parentExecutionId;
    this.store.set((state) => {
      const events = state.events.length >= maxEvents ? [...state.events.slice(1 - maxEvents), event] : [...state.events, event];
      let nodeStatus = state.nodeStatus;
      if (event.nodeId && (event.kind === 'node.started' || event.kind === 'node.completed') && isRun) {
        nodeStatus = new Map(nodeStatus).set(event.nodeId, event.kind === 'node.started' ? 'Running' : (event.status ?? 'Succeeded'));
      }

      if (isRun && event.kind === 'execution.completed') {
        return {
          events,
          nodeStatus,
          run: { ...run, status: event.status ?? 'Succeeded', error: event.error },
          message: `Run ${event.status ?? 'finished'}${event.error ? `: ${event.error.message}` : ''}.`,
        };
      }

      return { events, nodeStatus };
    });

    if (isRun && event.kind === 'execution.completed') {
      void this.fetchResult(event.runId);
    }
  }

  private async fetchResult(runId: string): Promise<void> {
    try {
      const status = await this.api.run(runId);
      this.store.set((state) => (state.run?.runId === runId ? { run: { ...state.run, result: status.result } } : {}));
    } catch {
      // The status and error are already known from the stream; outputs are optional.
    }
  }
}

function nodeIdOf(document: JsonObject, key: string): string {
  const id = indexDocument(document).byKey.get(key)?.node.id;
  return typeof id === 'string' ? id : 'the activity';
}

/** `key` when that node exists in `document`, else the root's key. */
function existingKey(document: JsonObject, key: string | undefined): string | undefined {
  const index = indexDocument(document);
  return key !== undefined && index.byKey.has(key) ? key : index.entries[0]?.key;
}
