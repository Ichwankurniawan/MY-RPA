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
import { RunEventStream, type EventSourceFactory, type StreamStatus } from './events';
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
}

/** Events kept per run. */
export const maxEvents = 1000;

/** Runs kept in the Recent runs list. */
export const maxRuns = 10;

/** Streamed events applied per animation frame; more wait for the next frame. */
export const maxEventsPerFrame = 250;

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
  private attempts = 0;
  /** Argument texts per file (`project/path`), remembered for the session. */
  private readonly argumentDrafts = new Map<string, Record<string, string>>();
  private lastTimeoutMs?: number;
  /** Events received since the last flush, applied together (ADR-0030: one store update per frame). */
  private pending: ExecutionEvent[] = [];

  constructor(
    private readonly api: StudioApi,
    createSource: EventSourceFactory,
    private readonly schedule: Scheduler = nextFrame,
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
      this.store.set(treeView);
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
  const index = indexDocument(document);
  return key !== undefined && index.byKey.has(key) ? key : index.entries[0]?.key;
}
