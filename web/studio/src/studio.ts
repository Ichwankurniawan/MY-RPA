// The Studio's state and commands. The Web Studio owns the editing model; the server owns projects, files,
// validation and execution; the engine knows nothing about this client.

import { ApiError, type StudioApi } from './api';
import {
  createNode,
  deleteRefusal,
  indexDocument,
  isObject,
  keyOf,
  moveNode,
  nodeAt,
  moveRefusal,
  openWorkflow,
  removeNode,
  selectionAfterDelete,
  serialize,
  setDisplayName,
  setProperty,
  type Step,
} from './document';
import { storageDrafts, type DraftStore } from './drafts';
import { RunEventStream, type EventSourceFactory, type StreamStatus } from './events';
import { createStore, type Store } from './store';
import type { ActivityDescriptor, Diagnostic, ExecutionError, ExecutionEvent, Json, JsonObject, PluginReport, PropertyDescriptor, RunStatus, WorkflowFile } from './types';
import { diagnosticTarget, setNodeId, setPropertyValue, type DataList } from './workflowData';
import {
  moveTo,
  moveToRefusal,
  parseNodes,
  place,
  placeRefusal,
  prepareForPaste,
  resolveTarget,
  selectionTarget,
  serializeNodes,
  type KeyedTarget,
  type Target,
} from './placement';

export interface OpenFile {
  readonly project: string;
  readonly path: string;
  /** The server's ETag of the version the document was read from or last saved as. */
  readonly etag: string;
  /** Set when the file cannot be saved back unchanged; editing and saving are disabled. */
  readonly readOnlyReason?: string;
}

/**
 * One press of Run and, once the server accepted it, that run. Validating and NotStarted are the Studio's own steps
 * before the server has a run; Starting means accepted and waiting for the engine (possibly queued). After that the
 * status is the server's: Running, then the final status of `execution.completed` (Succeeded, Failed, Cancelled,
 * TimedOut). The Studio never decides an execution outcome itself (ADR-0030).
 */
export interface RunView {
  /** The attempt's client key (a run id exists only once the server accepted the run). */
  readonly key: string;
  readonly runId?: string;
  readonly project: string;
  readonly path: string;
  /** When Run was pressed (ISO 8601). */
  readonly requestedAt: string;
  readonly status: string;
  /** Why the run never started: the workflow did not validate, or the server refused the request. */
  readonly notStarted?: { readonly reason: 'validation' | 'request'; readonly message: string };
  /** Stop was requested; the outcome is still the server's (shown as Cancelling… until it arrives). */
  readonly cancelRequested: boolean;
  /** The engine's start time (`execution.started`). */
  readonly startedAt?: string;
  readonly durationMs?: number;
  readonly error?: ExecutionError;
  readonly result?: RunStatus['result'];
  /** The run's events, the latest `maxEvents`. */
  readonly events: readonly ExecutionEvent[];
  /** Node id → Running, Succeeded, Failed or Cancelled (the run's own workflow only, never invoked ones). */
  readonly nodeStatus: ReadonlyMap<string, string>;
  /** Nodes started and not yet completed, in start order; the last one is executing now. */
  readonly runningNodes: readonly string[];
  /** Events the server no longer had when they were requested (`stream.gap`). */
  readonly missingEvents: number;
}

/** An input (In or InOut) argument declared by the workflow, for the run dialog. */
export interface RunArgument {
  readonly name: string;
  readonly type: string;
  readonly required: boolean;
  /** The declared default as JSON, for display only. */
  readonly defaultJson?: string;
}

export interface RunDialogState {
  readonly path: string;
  readonly arguments: readonly RunArgument[];
  /** The texts entered the last time this file was run in this session. */
  readonly values: Readonly<Record<string, string>>;
  readonly timeoutMs?: number;
}

export interface RunOptions {
  /** Input arguments as typed text; blank ones are left out so the engine applies the default. */
  readonly argumentText?: Readonly<Record<string, string>>;
  readonly timeoutMs?: number;
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
  /** Recent runs, newest first (at most `maxRuns`, unfinished ones are never dropped). */
  readonly runs: readonly RunView[];
  /** The run shown in the Execution panel and on the tree. */
  readonly currentRunKey?: string;
  /** The current run's node states when it ran the open file; otherwise empty. */
  readonly nodeStatus: ReadonlyMap<string, string>;
  /** Whether the tree shows a run's node states (nodes without one were not executed). */
  readonly treeShowsRun: boolean;
  readonly stream: StreamStatus;
  readonly runDialog?: RunDialogState;
  /** The open in-app dialog of file management (W6), if any. */
  readonly dialog?: StudioDialog;
  /** Loaded plugins and their load diagnostics (`GET /api/plugins`). */
  readonly plugins?: PluginReport;
  /** The empty slot or list gap picked in the designer for the next insert or paste (W7). */
  readonly insertTarget?: KeyedTarget;
  /** A request to show an argument or variable row (from the Problems list); `seq` makes repeats distinct. */
  readonly rowFocus?: { readonly list: DataList; readonly index: number; readonly seq: number };
}

/** The selection key of the workflow itself (its metadata in Properties); never a node key. */
export const workflowKey = '@workflow';

/**
 * In-app dialogs of file management (W6, ADR-0031); they replace `window.confirm`.
 * - `unsaved`: the open document has unsaved changes and another file is about to be opened.
 * - `name`: a path for a new file, a rename (`from`) or Save as; `error` is the server's answer to the last try.
 * - `delete`: confirm deleting a file (`dirty`: it is open with unsaved changes).
 * - `conflict`: saving found the file changed on disk (412).
 * - `recover`: a draft of unsaved changes was found for the file just opened (`stale`: the file changed since).
 */
export type StudioDialog =
  | { readonly kind: 'unsaved'; readonly path: string; readonly next: string }
  | { readonly kind: 'name'; readonly purpose: 'new' | 'rename' | 'save-as'; readonly from?: string; readonly initial: string; readonly error?: string }
  | { readonly kind: 'delete'; readonly path: string; readonly dirty: boolean }
  | { readonly kind: 'conflict'; readonly path: string }
  | { readonly kind: 'recover'; readonly path: string; readonly savedAt: string; readonly stale: boolean };

export interface StudioOptions {
  /** Where unsaved documents are kept for crash recovery (the browser's local storage by default). */
  readonly drafts?: DraftStore;
  /** How long typing pauses before the draft is written (ms). */
  readonly draftDelayMs?: number;
  /** How long typing pauses before the server validates the document (ms; undefined: never automatically). */
  readonly validateDelayMs?: number;
}

/** A new workflow's document: valid, with an empty root Sequence; its id comes from the file name. */
export function newWorkflowText(path: string): string {
  const name = path.split('/').at(-1)!.replace(/\.json$/i, '');
  const id = name.replace(/[^A-Za-z0-9\-_.:]/g, '-').slice(0, 128) || 'workflow';
  return serialize({ schemaVersion: '1.0', id, name: name || id, version: '1.0.0', root: { id: 'main', type: 'Core.Sequence' } });
}

/** Why `path` is not a usable workflow path (undefined when it is); the server checks the rest. */
export function pathRefusal(path: string): string | undefined {
  if (!/\.json$/i.test(path)) {
    return 'The file name must end in .json.';
  }

  return path.startsWith('/') || path.includes('\\') || path.split('/').some((s) => s === '' || s === '.' || s === '..' || s.startsWith('.'))
    ? "Use a path inside the project, with '/' between folders, and no '.', '..' or hidden names."
    : undefined;
}

/** Events kept per run. */
export const maxEvents = 1000;

/** Runs kept in the Recent runs list. */
export const maxRuns = 10;

/** Streamed events applied per animation frame; more wait for the next frame. */
export const maxEventsPerFrame = 100;

const noStatus: ReadonlyMap<string, string> = new Map();

/** Whether the run may still change (the Studio is validating, or the server has not finished it). */
export const isActive = (run: RunView): boolean => run.status === 'Validating' || run.status === 'Starting' || run.status === 'Running';

export function currentRun(state: StudioState): RunView | undefined {
  return state.runs.find((run) => run.key === state.currentRunKey);
}

/** Why Run is not available now (undefined when it is). Other runs may still be going on. */
export function runRefusalOf(state: StudioState): string | undefined {
  if (state.document === undefined || state.file === undefined) {
    return 'Open a workflow first.';
  }

  return state.busy !== undefined ? `Wait until ${state.busy} has finished.` : undefined;
}

/** Why `run` cannot be stopped now (undefined when it can). */
export function stopRefusalOf(run: RunView | undefined): string | undefined {
  if (run === undefined) {
    return 'No run to stop.';
  }

  if (run.status === 'NotStarted') {
    return 'The run did not start.';
  }

  if (!isActive(run)) {
    return `The run already finished (${run.status}).`;
  }

  if (run.runId === undefined) {
    return 'The run has not been accepted by the server yet.';
  }

  return run.cancelRequested ? 'Cancelling…' : undefined;
}

/** The workflow's In and InOut arguments, read from the document. */
export function inputArguments(document: JsonObject): RunArgument[] {
  const declared = Array.isArray(document.arguments) ? document.arguments : [];
  return declared.filter(isObject).flatMap((argument) =>
    typeof argument.name === 'string' && (argument.direction === 'In' || argument.direction === 'InOut')
      ? [
          {
            name: argument.name,
            type: typeof argument.type === 'string' ? argument.type : '',
            required: argument.required === true,
            defaultJson: 'default' in argument ? JSON.stringify(argument.default) : undefined,
          },
        ]
      : [],
  );
}

/**
 * A run after a batch of its events (in stream order), with one copy of its event list and node map per batch. Only the
 * run's own workflow (no parent execution) maps to tree nodes; events of invoked workflows are only listed.
 */
export function applyEvents(run: RunView, batch: readonly ExecutionEvent[]): RunView {
  if (batch.length === 0) {
    return run;
  }

  let { status, startedAt, durationMs, error, missingEvents } = run;
  let nodeStatus: Map<string, string> | undefined;
  const runningNodes = [...run.runningNodes];
  for (const event of batch) {
    if (event.kind === 'stream.gap') {
      missingEvents += (event.missingToSequence ?? 0) - (event.missingFromSequence ?? 0) + 1;
      continue;
    }

    if (event.parentExecutionId) {
      continue;
    }

    // The engine has the run once it reports anything of it (also when a gap hid execution.started).
    if (status === 'Starting' && event.kind !== 'log') {
      status = 'Running';
    }

    if (event.kind === 'execution.started') {
      startedAt = event.time;
    } else if (event.kind === 'node.started' && event.nodeId) {
      (nodeStatus ??= new Map(run.nodeStatus)).set(event.nodeId, 'Running');
      runningNodes.push(event.nodeId);
    } else if (event.kind === 'node.completed' && event.nodeId) {
      (nodeStatus ??= new Map(run.nodeStatus)).set(event.nodeId, event.status ?? 'Succeeded');
      const at = runningNodes.lastIndexOf(event.nodeId);
      if (at >= 0) {
        runningNodes.splice(at, 1);
      }
    } else if (event.kind === 'execution.completed') {
      status = event.status ?? 'Succeeded';
      durationMs = event.durationMs;
      error = event.error;
      runningNodes.length = 0;
    }
  }

  const all = run.events.concat(batch);
  const events = all.length > maxEvents ? all.slice(all.length - maxEvents) : all;
  return { ...run, events, status, startedAt, durationMs, error, missingEvents, nodeStatus: nodeStatus ?? run.nodeStatus, runningNodes };
}

/** A run after one of its events. */
export const applyEvent = (run: RunView, event: ExecutionEvent): RunView => applyEvents(run, [event]);

/** Runs `flush` once before the next paint (in the browser), so a burst of events costs one render. */
export type Scheduler = (flush: () => void) => void;

export const nextFrame: Scheduler = (flush) => (typeof requestAnimationFrame === 'function' ? requestAnimationFrame(() => flush()) : setTimeout(flush, 0));

/** The tree shows the current run's node states only when that run is of the open file. */
function treeView(state: StudioState): Pick<StudioState, 'nodeStatus' | 'treeShowsRun'> {
  const run = currentRun(state);
  const shows = run?.runId !== undefined && run.project === state.file?.project && run.path === state.file.path;
  return { nodeStatus: shows ? run.nodeStatus : noStatus, treeShowsRun: shows };
}

/** At most `maxRuns`: the oldest finished runs go first; unfinished ones stay so their events still have a home. */
function trimRuns(runs: readonly RunView[]): RunView[] {
  const kept = [...runs];
  for (let i = kept.length - 1; i >= 0 && kept.length > maxRuns; i--) {
    if (!isActive(kept[i])) {
      kept.splice(i, 1);
    }
  }

  return kept;
}

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
  const target = insertionTargetOf(state);
  return typeof target === 'string' ? target : undefined;
}

/**
 * Where an insert, paste or toolbox drop with no explicit place goes now: the empty slot or list gap the user picked
 * in the designer, else the place the selection implies (W7, `selectionTarget`); or the reason there is none.
 */
export function insertionTargetOf(state: StudioState): Target | string {
  const refusal = editRefusal(state);
  if (refusal !== undefined) {
    return refusal;
  }

  const picked = state.insertTarget ? resolveTarget(state.document!, state.insertTarget) : undefined;
  if (picked) {
    return placeRefusal(state.document!, picked, state.catalog) ?? picked;
  }

  const path = selectedPath(state);
  return path === undefined ? 'Select where to insert.' : selectionTarget(state.document!, path, state.catalog);
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
  private attempts = 0;
  /** Argument texts per file (`project/path`), remembered for the session. */
  private readonly argumentDrafts = new Map<string, Record<string, string>>();
  private lastTimeoutMs?: number;
  /** What this tab copied last (Paste without system clipboard text, e.g. the Paste button). */
  private clipboard?: string;
  /** Events received since the last flush, applied together (ADR-0030: one store update per frame). */
  private pending: ExecutionEvent[] = [];

  private readonly drafts: DraftStore;
  private readonly draftDelayMs: number;
  private draftTimer?: ReturnType<typeof setTimeout>;
  private readonly validateDelayMs: number | undefined;
  private validateTimer?: ReturnType<typeof setTimeout>;

  constructor(
    private readonly api: StudioApi,
    createSource: EventSourceFactory,
    private readonly schedule: Scheduler = nextFrame,
    options: StudioOptions = {},
  ) {
    this.drafts = options.drafts ?? storageDrafts();
    this.draftDelayMs = options.draftDelayMs ?? 1000;
    this.validateDelayMs = 'validateDelayMs' in options ? options.validateDelayMs : 300;
    this.store = createStore<StudioState>({
      connection: 'connecting',
      activities: [],
      catalog: new Map(),
      projects: [],
      files: [],
      undo: [],
      redo: [],
      errorNodeIds: new Set(),
      runs: [],
      nodeStatus: noStatus,
      treeShowsRun: false,
      stream: 'idle',
    });
    this.events = new RunEventStream(
      api,
      createSource,
      (event) => this.receive(event),
      (message) => this.say(message),
      (stream) => this.store.set({ stream }),
    );

    // Crash recovery: a changed document is written as a draft once typing pauses; saving or discarding removes it.
    let document = this.state.document;
    this.store.subscribe(() => {
      if (this.state.document !== document) {
        document = this.state.document;
        clearTimeout(this.draftTimer);
        this.draftTimer = setTimeout(() => this.writeDraft(), this.draftDelayMs);
        // Live validation (W4B): the server validates once typing pauses; the result never blocks typing.
        clearTimeout(this.validateTimer);
        if (document !== undefined && this.validateDelayMs !== undefined) {
          this.validateTimer = setTimeout(() => void this.autoValidate(), this.validateDelayMs);
        }
      }
    });
  }

  /** Writes (or, when the document is clean again, removes) the open file's draft now. */
  writeDraft(): void {
    clearTimeout(this.draftTimer);
    const { file, document, saved, dialog } = this.state;
    if (file === undefined || document === undefined || file.readOnlyReason !== undefined || dialog?.kind === 'recover') {
      return;
    }

    if (document === saved) {
      this.drafts.remove(file.project, file.path);
    } else {
      this.drafts.put(file.project, file.path, { text: serialize(document), etag: file.etag, savedAt: new Date().toISOString() });
    }
  }

  private dropDraft(project: string, path: string): void {
    clearTimeout(this.draftTimer);
    this.drafts.remove(project, path);
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
      // Plugin load problems (W5): a plugin that failed to load is not fatal for optional plugins; say so up front.
      const plugins = await this.api.plugins().catch(() => undefined);
      const problems = plugins?.diagnostics.length ?? 0;
      this.store.set({
        connection: 'ready',
        projects: info.projects,
        activities,
        catalog: new Map(activities.map((activity) => [activity.type, activity])),
        plugins,
        message: `Connected to ${info.name} (${info.mode} mode).${problems > 0 ? ` ${problems} plugin problem(s): see the notice above the designer.` : ''}`,
      });
      if (info.open) {
        // Named on the server's command line (--open): open it right away.
        await this.selectProject(info.open.project);
        await this.open(info.open.path);
      } else if (info.projects.length > 0) {
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

      // A draft left by a closed tab or a crash is offered back, unless it holds exactly what is on disk.
      let draft = opened.readOnlyReason === undefined ? this.drafts.get(project, path) : undefined;
      if (draft !== undefined && draft.text === serialize(opened.document)) {
        this.drafts.remove(project, path);
        draft = undefined;
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
        dialog: draft ? { kind: 'recover', path, savedAt: draft.savedAt, stale: draft.etag !== etag } : undefined,
        message: opened.readOnlyReason ? `Opened ${path} read-only: ${opened.readOnlyReason}` : `Opened ${path}.`,
      });
      this.store.set(treeView);
    } catch (error) {
      this.store.set({ busy: undefined, message: `Cannot open ${path}: ${(error as Error).message}` });
    }
  }

  select(key: string): void {
    if (this.state.selectedKey !== key || this.state.insertTarget !== undefined) {
      this.mergeKey = undefined;
      this.store.set({ selectedKey: key, insertTarget: undefined });
    }
  }

  /** Picks an empty slot or list gap as the place for the next insert or paste (W7); undefined clears it. */
  setInsertTarget(target: KeyedTarget | undefined): void {
    this.store.set({ insertTarget: target });
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

  /** Sets any value of a node's property (map, literal, raw JSON); `undefined` removes it. Typing merges per property. */
  editPropertyValue(key: string, name: string, value: Json | undefined, mergeKey = `property:${key}:${name}`): void {
    this.edit(key, `Edit ${name}`, mergeKey, (document, path) => setPropertyValue(document, path, name, value));
  }

  editNodeId(key: string, id: string): void {
    this.edit(key, 'Edit id', `id:${key}`, (document, path) => setNodeId(document, path, id));
  }

  /** A workflow-level edit (metadata, arguments, variables) as one undoable step; typing with one `mergeKey` merges. */
  editWorkflow(label: string, mergeKey: string | undefined, apply: (document: JsonObject) => JsonObject): void {
    const { document } = this.state;
    if (document !== undefined && editRefusal(this.state) === undefined) {
      this.commit(apply(document), this.state.selectedKey, label, mergeKey);
    }
  }

  /** Selects the workflow itself: Properties shows its metadata. */
  selectWorkflow(): void {
    this.select(workflowKey);
  }

  /** Goes to where a diagnostic belongs: its node (and property), its argument or variable row, or the workflow. */
  goToDiagnostic(diagnostic: Diagnostic): void {
    const target = diagnosticTarget(diagnostic);
    if (target.kind === 'node') {
      this.selectNodeId(target.nodeId);
    } else if (target.kind === 'row') {
      this.store.set((state) => ({ rowFocus: { list: target.list, index: target.index, seq: (state.rowFocus?.seq ?? 0) + 1 } }));
    } else {
      this.selectWorkflow();
    }
  }

  /** Validates the document as it is now (after typing pauses); never blocks editing and never says anything. */
  private async autoValidate(): Promise<void> {
    const document = this.state.document;
    if (document === undefined || this.state.connection !== 'ready') {
      return;
    }

    try {
      const result = await this.api.validate(document);
      if (this.state.document === document) {
        this.showDiagnostics(result.diagnostics, document);
      }
    } catch {
      // The explicit Validate and Run report problems; background validation stays quiet.
    }
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
  insertActivity(type: string, at?: Target): void {
    const state = this.state;
    const activity = state.catalog.get(type);
    const target = at ?? insertionTargetOf(state);
    const refusal =
      activity === undefined ? `'${type}' is not in the activity catalog.`
      : typeof target === 'string' ? target
      : (editRefusal(state) ?? placeRefusal(state.document!, target, state.catalog));
    if (activity === undefined || typeof target === 'string' || refusal !== undefined) {
      this.say(`Cannot insert: ${refusal}`);
      return;
    }

    const document = state.document!;
    const node = createNode(document, activity);
    this.commit(place(document, target, node).document, keyOf(node), `Insert ${activity.displayName}`);
    this.store.set({ insertTarget: undefined });
    this.say(`Inserted ${activity.displayName} as ${node.id as string}${'slot' in target.position ? ` into ${target.position.slot}` : ''}.`);
  }

  /** The selected activity as clipboard text (also kept for Paste in this tab); undefined when nothing is selected. */
  copySelected(): string | undefined {
    const state = this.state;
    const path = selectedPath(state);
    if (state.document === undefined || path === undefined) {
      this.say('Select an activity to copy.');
      return undefined;
    }

    const node = nodeAt(state.document, path);
    this.clipboard = serializeNodes([node]);
    this.say(`Copied ${typeof node.id === 'string' ? node.id : 'the activity'}.`);
    return this.clipboard;
  }

  /** Copies, then deletes, the selected activity (the root cannot be cut). */
  cutSelected(): string | undefined {
    const refusal = deleteRefusalOf(this.state);
    if (refusal !== undefined) {
      this.say(`Cannot cut: ${refusal}`);
      return undefined;
    }

    const text = this.copySelected();
    this.deleteSelected();
    return text;
  }

  /** Pastes activities (clipboard text, else what this tab copied last) where an insert would go; ids are renamed. */
  paste(text: string | undefined = this.clipboard): void {
    const nodes = text === undefined ? undefined : parseNodes(text);
    if (nodes === undefined) {
      this.say(text === undefined ? 'Nothing to paste: copy an activity first.' : 'The clipboard does not hold MyRPA activities.');
      return;
    }

    const state = this.state;
    const target = insertionTargetOf(state);
    if (typeof target === 'string') {
      this.say(`Cannot paste: ${target}`);
      return;
    }

    if ('slot' in target.position && nodes.length > 1) {
      this.say(`Cannot paste: the slot '${target.position.slot}' holds one activity, and the clipboard has ${nodes.length}.`);
      return;
    }

    let document = state.document!;
    const pasted = prepareForPaste(nodes, document);
    let last: JsonObject | undefined;
    pasted.forEach((node, i) => {
      const at: Target = 'index' in target.position ? { parentPath: target.parentPath, position: { index: target.position.index + i } } : target;
      document = place(document, at, node).document;
      last = node;
    });
    this.commit(document, keyOf(last!), pasted.length === 1 ? `Paste ${pasted[0].id as string}` : `Paste ${pasted.length} activities`);
    this.store.set({ insertTarget: undefined });
    this.say(`Pasted ${pasted.map((n) => n.id as string).join(', ')}.`);
  }

  /** Why a new activity cannot be placed at `target` (undefined when it can). */
  placeRefusalFor(target: Target): string | undefined {
    const state = this.state;
    return editRefusal(state) ?? placeRefusal(state.document!, target, state.catalog);
  }

  /** Shows a message in the status line. */
  notify(message: string): void {
    this.say(message);
  }

  /** Why the node with `key` cannot move to `target` (undefined when it can). */
  moveRefusalTo(key: string, target: Target): string | undefined {
    const state = this.state;
    const entry = state.document ? indexDocument(state.document).byKey.get(key) : undefined;
    return editRefusal(state) ?? (entry === undefined ? 'That activity no longer exists.' : moveToRefusal(state.document!, entry.path, target, state.catalog));
  }

  /** Moves the node with `key` (and its subtree) to `target` — across containers and into slots (W7). */
  moveNodeTo(key: string, target: Target): void {
    const refusal = this.moveRefusalTo(key, target);
    const state = this.state;
    if (refusal !== undefined) {
      this.say(`Cannot move: ${refusal}`);
      return;
    }

    const entry = indexDocument(state.document!).byKey.get(key)!;
    const moved = moveTo(state.document!, entry.path, target, state.catalog);
    const id = typeof entry.node.id === 'string' ? entry.node.id : 'the activity';
    if (moved.document !== state.document) {
      this.commit(moved.document, key, `Move ${id}`);
      this.say(`Moved ${id}${'slot' in target.position ? ` into ${target.position.slot}` : ''}.`);
    }

    this.select(key);
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
      this.writeDraft();
    } catch (error) {
      if (error instanceof ApiError && error.status === 412) {
        // Changed on disk since it was opened: the user chooses (reload, overwrite, save as); edits are kept meanwhile.
        this.store.set({ busy: undefined, dialog: { kind: 'conflict', path: file.path }, message: `Not saved: ${file.path} changed on disk since it was opened.` });
      } else {
        this.store.set({ busy: undefined, message: `Not saved: ${(error as Error).message}` });
      }
    }
  }

  /** Whether the last save left the document clean (for actions that continue after saving). */
  private get savedClean(): boolean {
    return this.state.document === this.state.saved;
  }

  closeDialog(): void {
    this.store.set({ dialog: undefined });
  }

  /** Opens a file of the current project; with unsaved changes, asks first (Save, Discard, Cancel). */
  async requestOpen(path: string): Promise<void> {
    const { file } = this.state;
    if (isDirty(this.state) && file !== undefined) {
      this.store.set({ dialog: { kind: 'unsaved', path: file.path, next: path } });
      return;
    }

    await this.open(path);
  }

  async resolveUnsaved(choice: 'save' | 'discard' | 'cancel'): Promise<void> {
    const dialog = this.state.dialog;
    const file = this.state.file;
    if (dialog?.kind !== 'unsaved' || file === undefined) {
      return;
    }

    this.closeDialog();
    if (choice === 'cancel') {
      return;
    }

    if (choice === 'save') {
      await this.save();
      if (!this.savedClean) {
        return; // Not saved (a conflict or an error says why): stay on this file.
      }
    } else {
      this.dropDraft(file.project, file.path);
    }

    await this.open(dialog.next);
  }

  /** The current ETag of a project file: the open one's, else read from the server. */
  private async etagOf(path: string): Promise<string> {
    const { file, project } = this.state;
    return file?.project === project && file?.path === path ? file.etag : (await this.api.readWorkflow(project!, path)).etag;
  }

  private async refreshFiles(): Promise<void> {
    if (this.state.project !== undefined) {
      await this.selectProject(this.state.project);
    }
  }

  /** New, Rename and Save as ask for a path first. */
  startName(purpose: 'new' | 'rename' | 'save-as', from?: string): void {
    const taken = new Set(this.state.files.map((f) => f.path.toLowerCase()));
    const unique = (base: string) => {
      for (let i = 1; ; i++) {
        const candidate = i === 1 ? `${base}.json` : `${base}-${i}.json`;
        if (!taken.has(candidate.toLowerCase())) {
          return candidate;
        }
      }
    };
    const current = from ?? this.state.file?.path;
    const initial = purpose === 'new' ? unique('new-workflow') : purpose === 'save-as' && current ? unique(current.replace(/\.json$/i, '') + '-copy') : (current ?? '');
    this.store.set({ dialog: { kind: 'name', purpose, from: purpose === 'rename' ? current : undefined, initial } });
  }

  /** Carries out New, Rename or Save as with the path the user entered; a refusal keeps the dialog with the reason. */
  async submitName(path: string): Promise<void> {
    const dialog = this.state.dialog;
    const project = this.state.project;
    if (dialog?.kind !== 'name' || project === undefined) {
      return;
    }

    const target = path.trim();
    const refusal = pathRefusal(target);
    if (refusal !== undefined) {
      this.store.set({ dialog: { ...dialog, error: refusal } });
      return;
    }

    try {
      if (dialog.purpose === 'new') {
        await this.api.createWorkflow(project, target, newWorkflowText(target));
        this.closeDialog();
        await this.refreshFiles();
        await this.requestOpen(target);
        this.say(`Created ${target}.`);
      } else if (dialog.purpose === 'rename') {
        await this.rename(dialog.from!, target);
      } else {
        await this.saveAs(target);
      }
    } catch (error) {
      // Create answers 412 when the target exists; move answers 409 for that and 412 when the source changed on disk.
      const reason =
        error instanceof ApiError && error.status === 409 ? `'${target}' already exists.`
        : error instanceof ApiError && error.status === 412 ? (dialog.purpose === 'rename' ? `'${dialog.from}' changed on disk since it was read; reopen it first.` : `'${target}' already exists.`)
        : (error as Error).message;
      this.store.set({ dialog: { ...dialog, error: reason } });
    }
  }

  private async rename(from: string, to: string): Promise<void> {
    const project = this.state.project!;
    const etag = await this.etagOf(from);
    const newEtag = await this.api.moveWorkflow(project, from, to, etag);
    this.closeDialog();
    const file = this.state.file;
    if (file?.project === project && file.path === from) {
      this.dropDraft(project, from);
      this.store.set({ file: { ...file, path: to, etag: newEtag } });
      this.writeDraft();
    }

    await this.refreshFiles();
    this.say(`Renamed ${from} to ${to}.`);
  }

  /** Saves the open document as a new file, which becomes the open file; the old file keeps its saved content. */
  private async saveAs(to: string): Promise<void> {
    const { document, file } = this.state;
    if (document === undefined || file === undefined) {
      return;
    }

    if (file.readOnlyReason !== undefined) {
      throw new Error(`The workflow is read-only: ${file.readOnlyReason}`);
    }

    const etag = await this.api.createWorkflow(file.project, to, serialize(document));
    this.closeDialog();
    this.dropDraft(file.project, file.path);
    this.store.set({ file: { project: file.project, path: to, etag }, saved: document });
    await this.refreshFiles();
    this.say(`Saved as ${to}.`);
  }

  startDelete(path: string): void {
    const { file } = this.state;
    this.store.set({ dialog: { kind: 'delete', path, dirty: file?.path === path && isDirty(this.state) } });
  }

  /** Deletes the file (as last read: If-Match). Deleting the open file closes it. */
  async confirmDelete(): Promise<void> {
    const dialog = this.state.dialog;
    const project = this.state.project;
    if (dialog?.kind !== 'delete' || project === undefined) {
      return;
    }

    this.closeDialog();
    try {
      await this.api.deleteWorkflow(project, dialog.path, await this.etagOf(dialog.path));
    } catch (error) {
      this.say(
        error instanceof ApiError && error.status === 412
          ? `Not deleted: ${dialog.path} changed on disk since it was read.`
          : `Not deleted: ${(error as Error).message}`,
      );
      return;
    }

    this.dropDraft(project, dialog.path);
    const file = this.state.file;
    if (file?.project === project && file.path === dialog.path) {
      this.mergeKey = undefined;
      this.store.set({ file: undefined, document: undefined, saved: undefined, selectedKey: undefined, undo: [], redo: [], diagnostics: undefined, validated: undefined, errorNodeIds: new Set() });
      this.store.set(treeView);
    }

    await this.refreshFiles();
    this.say(`Deleted ${dialog.path}.`);
  }

  /** After a save conflict: reload the disk version, overwrite it with this one, or save this one as another file. */
  async resolveConflict(choice: 'reload' | 'overwrite' | 'save-as' | 'cancel'): Promise<void> {
    const { dialog, file } = this.state;
    if (dialog?.kind !== 'conflict' || file === undefined) {
      return;
    }

    this.closeDialog();
    if (choice === 'reload') {
      this.dropDraft(file.project, file.path);
      await this.open(file.path);
    } else if (choice === 'overwrite') {
      try {
        const { etag } = await this.api.readWorkflow(file.project, file.path);
        this.store.set({ file: { ...file, etag } });
        await this.save();
      } catch (error) {
        if (error instanceof ApiError && error.status === 404) {
          // Deleted on disk meanwhile: write it again.
          const etag = await this.api.createWorkflow(file.project, file.path, serialize(this.state.document!));
          this.store.set({ file: { ...file, etag }, saved: this.state.document });
          this.writeDraft();
          await this.refreshFiles();
          this.say(`Saved ${file.path} (it had been deleted on disk).`);
        } else {
          this.say(`Not saved: ${(error as Error).message}`);
        }
      }
    } else if (choice === 'save-as') {
      this.startName('save-as');
    }
  }

  /** The recovery dialog: restore the draft as unsaved edits on top of the file as opened, or discard it. */
  resolveRecovery(choice: 'restore' | 'discard'): void {
    const { dialog, file, document } = this.state;
    if (dialog?.kind !== 'recover' || file === undefined || document === undefined) {
      return;
    }

    const draft = this.drafts.get(file.project, file.path);
    this.closeDialog();
    if (choice === 'discard' || draft === undefined) {
      this.dropDraft(file.project, file.path);
      this.say(`Discarded the unsaved changes of ${file.path}.`);
      return;
    }

    const recovered = openWorkflow(draft.text);
    if (!recovered.ok) {
      this.dropDraft(file.project, file.path);
      this.say(`The unsaved changes of ${file.path} could not be read: ${recovered.error}`);
      return;
    }

    // The restored version is one edit on top of the opened file: dirty, and Undo returns to the file on disk.
    const root = recovered.document.root;
    this.store.set({
      document: recovered.document,
      selectedKey: isObject(root) ? keyOf(root) : undefined,
      undo: [{ document, selectedKey: this.state.selectedKey, label: 'Restore unsaved changes' }],
      redo: [],
      message: `Restored the unsaved changes of ${file.path} from ${new Date(draft.savedAt).toLocaleString()}.`,
    });
  }

  runRefusal(): string | undefined {
    return runRefusalOf(this.state);
  }

  /** Run (F5): asks for the workflow's input arguments first when it declares any, else runs right away. */
  async requestRun(): Promise<void> {
    const refusal = this.runRefusal();
    if (refusal !== undefined) {
      this.say(`Cannot run: ${refusal}`);
      return;
    }

    const { document, file } = this.state;
    const inputs = inputArguments(document!);
    if (inputs.length === 0) {
      await this.run();
      return;
    }

    const draft = this.argumentDrafts.get(`${file!.project}/${file!.path}`) ?? {};
    this.store.set({ runDialog: { path: file!.path, arguments: inputs, values: draft, timeoutMs: this.lastTimeoutMs } });
  }

  closeRunDialog(): void {
    this.store.set({ runDialog: undefined });
  }

  /** Start from the run dialog: remembers the texts, sends the non-blank ones (blank keeps the default). */
  async startFromDialog(values: Readonly<Record<string, string>>, timeoutMs: number | undefined): Promise<void> {
    const { file, document } = this.state;
    if (file === undefined || document === undefined) {
      return;
    }

    this.argumentDrafts.set(`${file.project}/${file.path}`, { ...values });
    this.lastTimeoutMs = timeoutMs;
    // Only arguments the workflow declares now: a remembered text of a renamed or removed argument is never sent.
    const declared = new Set(inputArguments(document).map((a) => a.name));
    const argumentText = Object.fromEntries(Object.entries(values).filter(([name, text]) => declared.has(name) && text.trim() !== ''));
    this.store.set({ runDialog: undefined });
    await this.run({ argumentText: Object.keys(argumentText).length > 0 ? argumentText : undefined, timeoutMs });
  }

  /**
   * Validates the document on the server, and only if it is valid starts it; an unsaved document runs as if it were
   * saved at its path. The run is followed on the tab's one event stream. Other runs keep going.
   */
  async run(options: RunOptions = {}): Promise<void> {
    const refusal = this.runRefusal();
    if (refusal !== undefined) {
      this.say(`Cannot run: ${refusal}`);
      return;
    }

    const state = this.state;
    const document = state.document!;
    const file = state.file!;
    const key = `attempt-${++this.attempts}`;
    this.addRun({
      key,
      project: file.project,
      path: file.path,
      requestedAt: new Date().toISOString(),
      status: 'Validating',
      cancelRequested: false,
      events: [],
      nodeStatus: noStatus,
      runningNodes: [],
      missingEvents: 0,
    });
    this.store.set({ busy: 'starting', message: `Validating ${file.path}…` });

    try {
      const validation = await this.api.validate(document);
      this.showDiagnostics(validation.diagnostics, document);
      if (!validation.valid) {
        const errors = validation.diagnostics.filter((d) => d.severity === 'Error').length;
        this.notStarted(key, 'validation', `Not started: the workflow has ${errors} validation error(s). Fix the problems listed, then run again.`);
        return;
      }
    } catch (error) {
      this.notStarted(key, 'request', `Not started: validation failed: ${(error as Error).message}`);
      return;
    }

    this.updateRun(key, { status: 'Starting' });
    let runId: string;
    try {
      runId = await this.api.startRun(file.project, file.path, {
        document: isDirty(state) ? document : undefined,
        argumentText: options.argumentText,
        timeoutMs: options.timeoutMs,
      });
    } catch (error) {
      if (error instanceof ApiError && error.diagnostics) {
        this.showDiagnostics(error.diagnostics, document);
        this.notStarted(key, 'validation', 'Not started: the workflow has validation errors.');
      } else {
        this.notStarted(key, 'request', `Not started: ${(error as Error).message}`);
      }

      return;
    }

    this.updateRun(key, { runId });
    this.store.set({ busy: undefined, message: `Run ${runId} started.` });
    try {
      await this.events.follow(runId);
    } catch (error) {
      this.say(`Cannot follow run ${runId}: ${(error as Error).message}`);
    }
  }

  /**
   * Stop (Shift+F5): asks the server to cancel the run cooperatively. The run shows Cancelling… until the server
   * reports its final state on the stream; a request is never taken as the outcome.
   */
  async stop(key: string | undefined = this.state.currentRunKey): Promise<void> {
    const run = this.state.runs.find((r) => r.key === key);
    const refusal = stopRefusalOf(run);
    if (run === undefined || refusal !== undefined) {
      this.say(`Cannot stop: ${refusal}`);
      return;
    }

    this.updateRun(run.key, { cancelRequested: true });
    this.say(`Cancelling run ${run.runId}…`);
    try {
      await this.api.cancelRun(run.runId!);
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        return; // It finished meanwhile; its outcome arrives on the stream.
      }

      this.updateRun(run.key, { cancelRequested: false });
      this.say(`Cannot stop run ${run.runId}: ${(error as Error).message}`);
    }
  }

  /** Clears the shown events and logs of a run (in this tab only; the run itself is unaffected). */
  clearLog(key: string | undefined = this.state.currentRunKey): void {
    if (key !== undefined) {
      this.updateRun(key, { events: [], missingEvents: 0 });
    }
  }

  /** Shows another recent run in the Execution panel (and on the tree, if it ran the open file). */
  selectRun(key: string): void {
    if (this.state.runs.some((run) => run.key === key)) {
      this.setRuns(() => ({ currentRunKey: key }));
    }
  }

  private notStarted(key: string, reason: 'validation' | 'request', message: string): void {
    this.updateRun(key, { status: 'NotStarted', notStarted: { reason, message } });
    this.store.set({ busy: undefined, message });
  }

  /** Changes the runs and keeps the tree's node states in step. */
  private setRuns(update: (state: StudioState) => Partial<StudioState>): void {
    this.store.set((state) => {
      const changes = update(state);
      return { ...changes, ...treeView({ ...state, ...changes }) };
    });
  }

  private addRun(run: RunView): void {
    this.setRuns((state) => ({ runs: trimRuns([run, ...state.runs]), currentRunKey: run.key }));
  }

  private updateRun(key: string, change: Partial<RunView> | ((run: RunView) => RunView)): void {
    this.setRuns((state) => ({
      runs: state.runs.map((run) => (run.key !== key ? run : typeof change === 'function' ? change(run) : { ...run, ...change })),
    }));
  }

  private receive(event: ExecutionEvent): void {
    this.pending.push(event);
    if (this.pending.length === 1) {
      this.schedule(() => this.flush());
    }
  }

  /**
   * Applies the queued events, grouped by run in stream order, in one store update. A burst (a fast run's whole event
   * stream arriving at once) is spread over frames, at most `maxEventsPerFrame` per frame, so no frame stalls.
   */
  private flush(): void {
    const batch = this.pending.length > maxEventsPerFrame ? this.pending.splice(0, maxEventsPerFrame) : this.pending;
    this.pending = batch === this.pending ? [] : this.pending;
    if (this.pending.length > 0) {
      this.schedule(() => this.flush());
    }

    const byRun = new Map<string, ExecutionEvent[]>();
    for (const event of batch) {
      const events = byRun.get(event.runId);
      if (events) {
        events.push(event);
      } else {
        byRun.set(event.runId, [event]);
      }
    }

    this.setRuns((state) => ({ runs: state.runs.map((run) => (run.runId !== undefined && byRun.has(run.runId) ? applyEvents(run, byRun.get(run.runId)!) : run)) }));
    for (const event of batch) {
      const run = this.state.runs.find((r) => r.runId === event.runId);
      if (run !== undefined && !event.parentExecutionId && event.kind === 'execution.completed') {
        const where = event.error?.nodeId ? ` at ${event.error.nodeId}` : '';
        this.say(`Run ${event.runId} ${event.status ?? 'finished'}${event.error ? `${where}: ${event.error.message}` : ''}.`);
        void this.fetchResult(run.key, event.runId);
      }
    }
  }

  private async fetchResult(key: string, runId: string): Promise<void> {
    try {
      const status = await this.api.run(runId);
      this.updateRun(key, { result: status.result });
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
  if (key === workflowKey) {
    return key; // the workflow itself is always there
  }

  const index = indexDocument(document);
  return key !== undefined && index.byKey.has(key) ? key : index.entries[0]?.key;
}
