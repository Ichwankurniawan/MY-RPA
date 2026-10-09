// The Studio's state and commands. The Web Studio owns the editing model; the server owns projects, files,
// validation and execution; the engine knows nothing about this client.

import { ApiError, type StudioApi } from './api';
import {
  createNode,
  deleteRefusal,
  childSteps,
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
import type { ActivityDescriptor, DebugCommandName, DebugValue, Diagnostic, ExecutionError, ExpressionFunction, ExecutionEvent, Json, JsonObject, PluginReport, PropertyDescriptor, RecordedStep, RecordingEvent, RunStatus, NameReference, ScopeName, WorkflowFile } from './types';
import { isBreakpointMap, isTypeList, preferenceKeys, storagePreferences, type PreferenceStore } from './preferences';
import { renameReferences, usagesOf, type Usage } from './rename';
import { diagnosticTarget, setNodeId, setPropertyValue, type DataList } from './workflowData';
import {
  addTransition,
  addTransitionRefusal,
  asListNodes,
  asPastedSteps,
  detachStep,
  graphParentPath,
  isGraphNode,
  moveTransition,
  moveTransitionRefusal,
  removeTransition,
  setLayout,
  setStart,
  setStartRefusal,
  updateTransition,
  withGraphSchema,
  type Point,
} from './graph';
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
  /** A debug run (ADR-0040). */
  readonly debug?: boolean;
  /** Where the debug run is paused: from `debug.paused` until `debug.resumed` or the end of the run. */
  readonly paused?: PausedView;
}

/** Where a debug run is paused. The values come from the server on request, never from the event stream (ADR-0040). */
export interface PausedView {
  readonly nodeId: string;
  /** breakpoint, step or pause. */
  readonly reason: string;
  /** Paused in a workflow the run invoked: its node is not in this document. */
  readonly invoked: boolean;
  readonly workflowId?: string;
  /** The `debug.paused` event's sequence, so values fetched for an earlier pause are never shown for a later one. */
  readonly sequence: number;
  readonly values?: readonly DebugValue[];
}

/** How a debug run starts: pausing at the file's breakpoints (Debug, F6), or before its first activity (Step into, F11). */
export type DebugStart = 'breakpoints' | 'step';

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
  /** Set when the dialog starts a debug run. */
  readonly debug?: DebugStart;
}

export interface RunOptions {
  /** Input arguments as typed text; blank ones are left out so the engine applies the default. */
  readonly argumentText?: Readonly<Record<string, string>>;
  readonly timeoutMs?: number;
  /** Runs it as a debug run (ADR-0040). */
  readonly debug?: DebugStart;
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
  /** Client keys of the nodes with error diagnostics (located like WPF; exact even for duplicate or invalid ids). */
  readonly errorNodeKeys: ReadonlySet<string>;
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
  /** Favorite activity types, in the order they were added (UX-2; remembered per browser). */
  readonly favorites: readonly string[];
  /** The last activity types inserted, most recent first, at most `maxRecent` (UX-2; remembered per browser). */
  readonly recentActivities: readonly string[];
  /** The bottom panel's tab (UX-2): Execution when a run starts; Problems after Validate or a run refused by validation. */
  readonly outputTab: OutputTab;
  /** Panel sizes (px, only once resized) and hidden panels (UX-2; remembered per browser). */
  readonly panes: Panes;
  /** Containers whose children are hidden in the designer (UX-3; never an ancestor of the selection). */
  readonly collapsed: ReadonlySet<string>;
  /** Graph containers (by key) shown as a list of steps instead of the canvas (G-2): the keyboard-first view. */
  readonly graphLists: ReadonlySet<string>;
  /** The node the designer shows in place of the whole workflow (a flowchart step opened from the canvas, G-2). */
  readonly designerScope?: string;
  /** A transition to focus in Properties (an arrow was clicked on the canvas); `seq` repeats the request. */
  readonly transitionFocus?: { readonly key: string; readonly index: number; readonly seq: number };
  /** The Recorder tab's recording (ADR-0039). */
  readonly recorder?: RecorderState;
  /** The designer's zoom (UX-3): 1 is 100 %. */
  readonly zoom: number;
  /** Breakpoints per file (`project/path` → node ids; ADR-0040). Remembered per browser, never in the workflow file. */
  readonly breakpoints: Readonly<Record<string, readonly string[]>>;
  /** The expression functions, for completion (ADR-0041); empty until the server answered. */
  readonly expressionFunctions: readonly ExpressionFunction[];
}

/** The designer's zoom range and step (UX-3). */
export const zoomLimits = { min: 0.5, max: 2, step: 0.1 } as const;

/** The panels around the designer that can be resized and hidden. */
export type PaneName = 'toolbox' | 'properties' | 'bottom';

export interface Panes {
  readonly toolbox?: number;
  readonly properties?: number;
  readonly bottom?: number;
  readonly hidden: readonly PaneName[];
}

/** The smallest and largest size of each panel (px). */
export const paneLimits: Record<PaneName, { readonly min: number; readonly max: number }> = {
  toolbox: { min: 160, max: 520 },
  properties: { min: 240, max: 680 },
  bottom: { min: 120, max: 900 },
};

const paneNames: readonly PaneName[] = ['toolbox', 'properties', 'bottom'];

const isPanes = (value: unknown): value is Panes => {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const v = value as Record<string, unknown>;
  return (
    Array.isArray(v.hidden) &&
    v.hidden.every((h) => paneNames.includes(h as PaneName)) &&
    paneNames.every((name) => v[name] === undefined || (typeof v[name] === 'number' && Number.isFinite(v[name])))
  );
};

/** The tabs of the bottom panel. */
export type OutputTab = 'problems' | 'variables' | 'arguments' | 'execution' | 'recording';

/** One recorded step as the user keeps it (ADR-0039): the chosen selector and, for typing, the edited text. */
export interface RecordedItem {
  readonly step: RecordedStep;
  readonly selector?: string;
  readonly text?: string;
}

/** The Recorder tab (Phase 6): a recording being set up, running or ended, and its steps. */
export interface RecorderState {
  readonly phase: 'setup' | 'starting' | 'recording' | 'ended';
  readonly startUrl: string;
  readonly id?: string;
  readonly items: readonly RecordedItem[];
  readonly endReason?: string;
  /** Why recording is not possible on this server (no browser plugin), when known. */
  readonly unavailable?: string;
  readonly error?: string;
}

/** Why the recorded steps cannot be inserted now (undefined when they can). */
export function recordingInsertRefusal(state: StudioState): string | undefined {
  const recorder = state.recorder;
  if (recorder === undefined || recorder.items.length === 0) {
    return 'Record some steps first.';
  }

  if (recorder.phase === 'starting') {
    return 'The recording is starting.';
  }

  const target = insertionTargetOf(state);
  if (typeof target === 'string') {
    return target;
  }

  return 'slot' in target.position ? `The slot '${target.position.slot}' holds one activity; select a list (for example a Sequence) to insert the recording.` : undefined;
}

/** How many recently inserted activity types are remembered. */
export const maxRecent = 10;

/** The selection key of the workflow itself (its metadata in Properties); never a node key. */
export const workflowKey = '@workflow';

/**
 * In-app dialogs of file management (W6, ADR-0031); they replace `window.confirm`.
 * - `unsaved`: the open document has unsaved changes and another file is about to be opened.
 * - `name`: a path for a new file, a rename (`from`) or Save as; `error` is the server's answer to the last try.
 * - `delete`: confirm deleting a file (`dirty`: it is open with unsaved changes).
 * - `conflict`: saving found the file changed on disk (412).
 * - `recover`: a draft of unsaved changes was found for the file just opened (`stale`: the file changed since).
 * - `rename-name`: renaming a variable, argument or local and every use of it (ADR-0041); `references` once the server
 *   found them for `document`.
 * - `usages`: where a variable, argument or local is used.
 */
export type StudioDialog =
  | { readonly kind: 'unsaved'; readonly path: string; readonly next: string }
  | { readonly kind: 'name'; readonly purpose: 'new' | 'rename' | 'save-as'; readonly from?: string; readonly initial: string; readonly error?: string }
  | { readonly kind: 'delete'; readonly path: string; readonly dirty: boolean }
  | { readonly kind: 'conflict'; readonly path: string }
  | { readonly kind: 'recover'; readonly path: string; readonly savedAt: string; readonly stale: boolean }
  | {
      readonly kind: 'rename-name';
      readonly name: string;
      readonly path: string;
      readonly document: JsonObject;
      readonly references?: readonly NameReference[];
      readonly busy?: boolean;
      readonly error?: string;
    }
  | { readonly kind: 'usages'; readonly name: string; readonly usages?: readonly Usage[]; readonly error?: string };

export interface StudioOptions {
  /** Where unsaved documents are kept for crash recovery (the browser's local storage by default). */
  readonly drafts?: DraftStore;
  /** How long typing pauses before the draft is written (ms). */
  readonly draftDelayMs?: number;
  /** How long typing pauses before the server validates the document (ms; undefined: never automatically). */
  readonly validateDelayMs?: number;
  /** Where favorites and recent activities are remembered (the browser's local storage by default). */
  readonly preferences?: PreferenceStore;
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

/** The key of a file in `StudioState.breakpoints`. */
export const fileKeyOf = (file: { readonly project: string; readonly path: string }): string => `${file.project}/${file.path}`;

/** The open file's breakpoints (node ids). */
export function breakpointsOf(state: StudioState): readonly string[] {
  return state.file === undefined ? [] : (state.breakpoints[fileKeyOf(state.file)] ?? []);
}

/** The names of the debug commands, as on their buttons. */
export const debugCommandLabels: Readonly<Record<DebugCommandName, string>> = {
  continue: 'Continue',
  pause: 'Pause',
  stepInto: 'Step into',
  stepOver: 'Step over',
  stepOut: 'Step out',
};

/** Why `command` cannot be sent to `run` now (undefined when it can). */
export function debugRefusalOf(run: RunView | undefined, command: DebugCommandName): string | undefined {
  if (run?.debug !== true) {
    return 'Start a debug run first (Debug, F6).';
  }

  if (!isActive(run)) {
    return `The debug run already finished (${run.status}).`;
  }

  if (run.runId === undefined || run.status !== 'Running') {
    return 'The debug run has not started yet.';
  }

  if (run.cancelRequested) {
    return 'Cancelling…';
  }

  if (command === 'pause') {
    return run.paused !== undefined ? 'The run is already paused.' : undefined;
  }

  return run.paused === undefined ? 'The run is not paused.' : undefined;
}

/** Why a breakpoint cannot be toggled on the node with `key` (undefined when it can). */
export function breakpointRefusalOf(state: StudioState, key: string | undefined): string | undefined {
  if (state.document === undefined || state.file === undefined) {
    return 'Open a workflow first.';
  }

  if (key === undefined || key === workflowKey) {
    return 'Select an activity first.';
  }

  const id = indexDocument(state.document).byKey.get(key)?.node.id;
  return typeof id === 'string' && id !== '' ? undefined : 'The activity needs an id to have a breakpoint.';
}

const collapsibleCache = new WeakMap<JsonObject, { readonly view: readonly unknown[]; readonly keys: readonly string[] }>();

/**
 * The containers that show a collapse toggle in the designer now (UX-3): below the shown root (the workflow's root, or
 * the flowchart step opened in its place), holding activities, and not inside a flowchart shown as a canvas (its steps
 * open instead). Computed once per document and view.
 */
export function collapsibleKeysOf(state: StudioState): readonly string[] {
  const { document, catalog, graphLists, designerScope } = state;
  if (document === undefined) {
    return [];
  }

  const view = [catalog, graphLists, designerScope];
  const cached = collapsibleCache.get(document);
  if (cached !== undefined && cached.view.every((v, i) => v === view[i])) {
    return cached.keys;
  }

  const root = designerScope !== undefined ? indexDocument(document).byKey.get(designerScope)?.node : isObject(document.root) ? document.root : undefined;
  const keys: string[] = [];
  const visit = (node: JsonObject, shownRoot: boolean) => {
    const children = childSteps(node);
    if (children.length === 0) {
      return;
    }

    if (!shownRoot) {
      keys.push(keyOf(node));
    }

    if (!isGraphNode(node, catalog) || graphLists.has(keyOf(node))) {
      children.forEach((child) => visit(child.node, false));
    }
  };
  if (root !== undefined) {
    visit(root, true);
  }

  collapsibleCache.set(document, { view, keys });
  return keys;
}

/** Why Expand all does nothing now (undefined when it would expand something). */
export function expandAllRefusalOf(state: StudioState): string | undefined {
  if (state.document === undefined) {
    return 'Open a workflow first.';
  }

  return collapsibleKeysOf(state).some((key) => state.collapsed.has(key)) ? undefined : 'Nothing is collapsed.';
}

/** Why Collapse all does nothing now (undefined when it would collapse something). The selection always stays visible. */
export function collapseAllRefusalOf(state: StudioState): string | undefined {
  const { document } = state;
  if (document === undefined) {
    return 'Open a workflow first.';
  }

  const keys = collapsibleKeysOf(state);
  if (keys.length === 0) {
    const index = indexDocument(document);
    const root = state.designerScope !== undefined ? index.byKey.get(state.designerScope)?.node : isObject(document.root) ? document.root : undefined;
    return root !== undefined && isGraphNode(root, state.catalog) && !state.graphLists.has(keyOf(root))
      ? 'Nothing to collapse: the steps of a flowchart open from the canvas (or show them with List view).'
      : 'Nothing to collapse: no activity here holds other activities.';
  }

  const path = state.selectedKey !== undefined ? indexDocument(document).byKey.get(state.selectedKey)?.path : undefined;
  const kept = new Set(path === undefined ? [] : Array.from({ length: path.length }, (_, depth) => keyOf(nodeAt(document, path.slice(0, depth)))));
  return keys.some((key) => !state.collapsed.has(key) && !kept.has(key)) ? undefined : 'Everything is already collapsed (the selection stays visible).';
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

  let { status, startedAt, durationMs, error, missingEvents, paused } = run;
  let nodeStatus: Map<string, string> | undefined;
  const runningNodes = [...run.runningNodes];
  // The paused node shows as Paused until it runs (or the run ends without running it).
  const unpause = () => {
    if (paused !== undefined && !paused.invoked && (nodeStatus ?? run.nodeStatus).get(paused.nodeId) === 'Paused') {
      (nodeStatus ??= new Map(run.nodeStatus)).delete(paused.nodeId);
    }

    paused = undefined;
  };
  for (const event of batch) {
    if (event.kind === 'stream.gap') {
      missingEvents += (event.missingToSequence ?? 0) - (event.missingFromSequence ?? 0) + 1;
      continue;
    }

    if (event.kind === 'debug.paused') {
      unpause();
      paused = { nodeId: event.nodeId ?? '', reason: event.reason ?? 'pause', invoked: Boolean(event.parentExecutionId), workflowId: event.workflowId, sequence: event.sequence };
      if (!event.parentExecutionId && event.nodeId) {
        (nodeStatus ??= new Map(run.nodeStatus)).set(event.nodeId, 'Paused');
      }

      continue;
    }

    if (event.kind === 'debug.resumed') {
      unpause();
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
      unpause();
      status = event.status ?? 'Succeeded';
      durationMs = event.durationMs;
      error = event.error;
      runningNodes.length = 0;
    }
  }

  const all = run.events.concat(batch);
  const events = all.length > maxEvents ? all.slice(all.length - maxEvents) : all;
  return { ...run, events, status, startedAt, durationMs, error, missingEvents, paused, nodeStatus: nodeStatus ?? run.nodeStatus, runningNodes };
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

/** Why the document cannot be edited at all now (no file open, or a read-only file); undefined when it can. */
export function editRefusal(state: StudioState): string | undefined {
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

  // With the workflow itself selected (its details in Properties), inserts go into the root, as for a selected root.
  const path = state.selectedKey === workflowKey ? [] : selectedPath(state);
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
  if (path !== undefined && state.document !== undefined && graphParentPath(state.document, path, state.catalog) !== undefined) {
    return 'Steps of a flowchart are placed on its canvas, and their order does not matter (except the start step: use Set as start step).';
  }

  return editRefusal(state) ?? (path === undefined ? 'Select an activity to move.' : moveRefusal(state.document!, path, delta));
}

/** Why a transition from the step with `key` to the step with id `to` cannot be added now (undefined when it can). */
export function addTransitionRefusalOf(state: StudioState, key: string, to: string): string | undefined {
  const entry = state.document ? indexDocument(state.document).byKey.get(key) : undefined;
  return editRefusal(state) ?? (entry === undefined ? 'That step no longer exists.' : addTransitionRefusal(state.document!, entry.path, to, state.catalog));
}

/** Why the step with `key` cannot become the start step now (undefined when it can). */
export function setStartRefusalOf(state: StudioState, key: string): string | undefined {
  const entry = state.document ? indexDocument(state.document).byKey.get(key) : undefined;
  return editRefusal(state) ?? (entry === undefined ? 'That step no longer exists.' : setStartRefusal(state.document!, entry.path, state.catalog));
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
  /** Names in scope per document version and path (ADR-0041). */
  private readonly scopeCache = new WeakMap<JsonObject, Map<string, Promise<readonly ScopeName[]>>>();

  private readonly drafts: DraftStore;
  private readonly draftDelayMs: number;
  private readonly preferences: PreferenceStore;
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
    this.preferences = options.preferences ?? storagePreferences();
    this.store = createStore<StudioState>({
      favorites: this.preferences.read(preferenceKeys.favorites, isTypeList) ?? [],
      recentActivities: (this.preferences.read(preferenceKeys.recent, isTypeList) ?? []).slice(0, maxRecent),
      outputTab: 'problems',
      panes: this.preferences.read(preferenceKeys.panes, isPanes) ?? { hidden: [] },
      collapsed: new Set(),
      graphLists: new Set(),
      zoom: 1,
      connection: 'connecting',
      activities: [],
      catalog: new Map(),
      projects: [],
      files: [],
      undo: [],
      redo: [],
      errorNodeKeys: new Set(),
      runs: [],
      nodeStatus: noStatus,
      treeShowsRun: false,
      stream: 'idle',
      breakpoints: this.preferences.read(preferenceKeys.breakpoints, isBreakpointMap) ?? {},
      expressionFunctions: [],
    });
    this.events = new RunEventStream(
      api,
      createSource,
      (event) => this.receive(event),
      (message) => this.say(message),
      (stream) => this.store.set({ stream }),
      (event) => this.receiveRecording(event),
    );

    // Crash recovery: a changed document is written as a draft once typing pauses; saving or discarding removes it.
    let document = this.state.document;
    let selectedKey = this.state.selectedKey;
    this.store.subscribe(() => {
      if (this.state.selectedKey !== selectedKey || this.state.document !== document) {
        selectedKey = this.state.selectedKey;
        this.revealSelection();
      }

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

  /**
   * The names visible at `path` of the open document (ADR-0041), from the server's validation. Asked once per document
   * version and path (a field gets them when it gains focus); empty when the server cannot answer.
   */
  namesInScope(path: string): Promise<readonly ScopeName[]> {
    const document = this.state.document;
    if (document === undefined) {
      return Promise.resolve([]);
    }

    let byPath = this.scopeCache.get(document);
    if (byPath === undefined) {
      byPath = new Map();
      this.scopeCache.set(document, byPath);
    }

    let names = byPath.get(path);
    if (names === undefined) {
      names = this.api.namesInScope(document, path).catch(() => {
        byPath.delete(path);
        return [];
      });
      byPath.set(path, names);
    }

    return names;
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
      // Completion works without them (names still come per field); never a reason not to connect.
      const expressionFunctions = await this.api.expressionFunctions().catch(() => []);
      const problems = plugins?.diagnostics.length ?? 0;
      this.store.set({
        connection: 'ready',
        projects: info.projects,
        activities,
        catalog: new Map(activities.map((activity) => [activity.type, activity])),
        plugins,
        expressionFunctions,
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
        errorNodeKeys: new Set(),
        designerScope: undefined,
        transitionFocus: undefined,
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

  /** Collapses a container in the designer, or expands it (UX-3). */
  toggleCollapsed(key: string): void {
    const collapsed = new Set(this.state.collapsed);
    if (!collapsed.delete(key)) {
      collapsed.add(key);
    }

    this.store.set({ collapsed });
  }

  /**
   * Collapses every container the designer shows below the root (the root stays open: it is the workflow). The
   * containers above the selection stay open, so the selection stays visible.
   */
  collapseAll(): void {
    const keys = collapsibleKeysOf(this.state);
    if (keys.length > 0) {
      this.store.set({ collapsed: new Set([...this.state.collapsed, ...keys]) });
      this.revealSelection();
    }
  }

  /** Expands every container. */
  expandAll(): void {
    this.store.set({ collapsed: new Set() });
  }

  /** Sets the designer's zoom, kept within its limits (UX-3). */
  setZoom(zoom: number): void {
    const clamped = Math.round(Math.min(zoomLimits.max, Math.max(zoomLimits.min, zoom)) * 100) / 100;
    if (clamped !== this.state.zoom) {
      this.store.set({ zoom: clamped });
    }
  }

  /**
   * The selection is never hidden (after a select, an insert, a paste, an undo…): containers above it are expanded, and
   * a node inside a flowchart step shown on a canvas is shown by opening that step (G-2); a selection outside the opened
   * step closes it.
   */
  private revealSelection(): void {
    const { document, selectedKey, collapsed, designerScope, catalog, graphLists } = this.state;
    if (document === undefined || selectedKey === undefined || selectedKey === workflowKey) {
      return;
    }

    const path = indexDocument(document).byKey.get(selectedKey)?.path;
    if (path === undefined) {
      return;
    }

    const ancestors = Array.from({ length: path.length }, (_, depth) => nodeAt(document, path.slice(0, depth)));
    const ancestorKeys = ancestors.map(keyOf);
    if (ancestorKeys.some((key) => collapsed.has(key))) {
      this.store.set({ collapsed: new Set([...collapsed].filter((key) => !ancestorKeys.includes(key))) });
    }

    // The deepest ancestor that is a step on a canvas (its parent is a graph container not shown as a list).
    let canvasStep: string | undefined;
    for (let depth = 1; depth < ancestors.length; depth++) {
      const parent = ancestors[depth - 1];
      if (isGraphNode(parent, catalog) && !graphLists.has(keyOf(parent))) {
        canvasStep = ancestorKeys[depth];
      }
    }

    const scope = canvasStep ?? (designerScope !== undefined && (designerScope === selectedKey || ancestorKeys.includes(designerScope)) ? designerScope : undefined);
    if (scope !== designerScope) {
      this.store.set({ designerScope: scope });
    }
  }

  // Flowcharts (G-2, ADR-0037). Every edit is one undo step; refusals are said, never half-applied.

  /** Shows a graph container's steps as a list (keyboard-first) or on the canvas. */
  toggleGraphView(key: string): void {
    const graphLists = new Set(this.state.graphLists);
    if (!graphLists.delete(key)) {
      graphLists.add(key);
    }

    this.store.set({ graphLists });
    this.revealSelection();
  }

  /** Shows a flowchart step (for example a Sequence) in the designer in place of the whole workflow, and selects it. */
  openStep(key: string): void {
    this.store.set({ designerScope: key });
    this.select(key);
  }

  /** Shows the whole workflow again; the opened step stays selected. */
  closeScope(): void {
    this.store.set({ designerScope: undefined });
  }

  /** Selects a step and asks Properties to focus one of its transitions (an arrow was clicked). */
  focusTransition(key: string, index: number): void {
    this.select(key);
    this.store.set({ transitionFocus: { key, index, seq: (this.state.transitionFocus?.seq ?? 0) + 1 } });
  }

  private graphEdit(key: string, label: string, apply: (document: JsonObject, path: readonly Step[]) => JsonObject, mergeKey?: string): boolean {
    const { document } = this.state;
    const entry = document ? indexDocument(document).byKey.get(key) : undefined;
    const refusal = editRefusal(this.state) ?? (entry === undefined ? 'That step no longer exists.' : undefined);
    if (refusal !== undefined) {
      this.say(`Cannot edit: ${refusal}`);
      return false;
    }

    this.commit(apply(document!, entry!.path), this.state.selectedKey, label, mergeKey);
    return true;
  }

  /** Adds a transition from the step with `key` to its sibling with id `to` (checked after its existing ones). */
  addTransition(key: string, to: string): void {
    const refusal = addTransitionRefusalOf(this.state, key, to);
    if (refusal !== undefined) {
      this.say(`Cannot connect: ${refusal}`);
      return;
    }

    const from = nodeIdOf(this.state.document!, key);
    if (this.graphEdit(key, `Connect ${from} to ${to}`, (document, path) => addTransition(document, path, to))) {
      this.say(`Connected ${from} to ${to}.`);
    }
  }

  /** Edits a transition's target, condition or label; typing in one field merges into one undo step. */
  editTransition(key: string, index: number, changes: { readonly to?: string; readonly when?: string; readonly label?: string }): void {
    const field = Object.keys(changes).join(',');
    this.graphEdit(key, `Edit transition ${index + 1}`, (document, path) => updateTransition(document, path, index, changes), `transition:${key}:${index}:${field}`);
  }

  removeTransitionAt(key: string, index: number): void {
    if (this.graphEdit(key, `Remove transition ${index + 1}`, (document, path) => removeTransition(document, path, index))) {
      this.say(`Removed transition ${index + 1} of ${nodeIdOf(this.state.document!, key)}.`);
    }
  }

  /** Moves a transition earlier (-1) or later (+1): transitions are checked in order. */
  moveTransitionAt(key: string, index: number, delta: -1 | 1): void {
    const entry = this.state.document ? indexDocument(this.state.document).byKey.get(key) : undefined;
    const refusal = entry === undefined ? 'That step no longer exists.' : moveTransitionRefusal(entry.node, index, delta);
    if (refusal !== undefined) {
      this.say(`Cannot move: ${refusal}`);
      return;
    }

    this.graphEdit(key, `Move transition ${index + 1} ${delta < 0 ? 'up' : 'down'}`, (document, path) => moveTransition(document, path, index, delta));
  }

  /** Places a node on its flowchart's canvas (one undo step per drag). */
  moveStep(key: string, at: Point): void {
    this.graphEdit(key, `Move ${nodeIdOf(this.state.document!, key)} on the canvas`, (document, path) => setLayout(document, path, at.x, at.y));
  }

  /** Makes a step the start step of its flowchart. */
  setStartStep(key: string): void {
    const refusal = setStartRefusalOf(this.state, key);
    if (refusal !== undefined) {
      this.say(`Cannot set the start step: ${refusal}`);
      return;
    }

    const document = this.state.document!;
    const path = indexDocument(document).byKey.get(key)!.path;
    const id = nodeIdOf(document, key);
    this.commit(setStart(document, path).document, key, `Start at ${id}`);
    this.say(`${id} is now the start step.`);
  }

  // Recording (Phase 6, ADR-0039): the server opens a visible browser; its steps arrive on the tab's event stream; the
  // browser plugin turns the kept steps into its activities, inserted here as one undo step.

  /** Shows the Recorder tab, with a new recording to set up when none is open. */
  openRecorder(): void {
    if (this.state.recorder === undefined) {
      this.store.set({ recorder: { phase: 'setup', startUrl: 'https://', items: [] } });
      void this.api.recordingInfo().then(
        (info) => this.updateRecorder((r) => ({ ...r, unavailable: info.available ? undefined : (info.reason ?? 'Recording is not available on this server.') })),
        () => undefined,
      );
    }

    this.store.set({ outputTab: 'recording' });
  }

  setRecorderUrl(startUrl: string): void {
    this.updateRecorder((r) => (r.phase === 'setup' ? { ...r, startUrl, error: undefined } : r));
  }

  /** Opens the recording browser at the start URL; the steps then appear in the Recorder tab. */
  async startRecording(): Promise<void> {
    const recorder = this.state.recorder;
    if (recorder === undefined || recorder.phase !== 'setup') {
      return;
    }

    const url = recorder.startUrl.trim();
    if (!/^https?:\/\/[^/\s]+/i.test(url)) {
      this.updateRecorder((r) => ({ ...r, error: 'Enter the address of a web page (http:// or https://).' }));
      return;
    }

    this.updateRecorder((r) => ({ ...r, phase: 'starting', startUrl: url, error: undefined, items: [] }));
    try {
      const id = await this.api.startRecording(url);
      this.updateRecorder((r) => ({ ...r, phase: 'recording', id }));
      this.say('Recording: do the steps in the browser window that opened, then press Stop here.');
      await this.events.followRecording(id);
    } catch (error) {
      this.updateRecorder((r) => ({ ...r, phase: 'setup', error: (error as Error).message }));
    }
  }

  /** Stops the recording and closes its browser; the steps stay to be reviewed and inserted. */
  async stopRecording(): Promise<void> {
    const id = this.state.recorder?.id;
    if (id !== undefined && this.state.recorder?.phase === 'recording') {
      try {
        await this.api.stopRecording(id);
      } catch (error) {
        if (!(error instanceof ApiError && error.status === 404)) {
          this.say(`Cannot stop the recording: ${(error as Error).message}`);
        }
      }
    }
  }

  chooseRecordedSelector(sequence: number, selector: string): void {
    this.updateItem(sequence, (item) => ({ ...item, selector }));
  }

  editRecordedText(sequence: number, text: string): void {
    this.updateItem(sequence, (item) => ({ ...item, text }));
  }

  removeRecordedStep(sequence: number): void {
    this.updateRecorder((r) => ({ ...r, items: r.items.filter((item) => item.step.sequence !== sequence) }));
  }

  /** Closes the Recorder's recording (stopping it first) without inserting anything. */
  async discardRecording(): Promise<void> {
    await this.stopRecording();
    this.store.set({ recorder: undefined });
  }

  /**
   * Inserts the kept steps where an insert would go (W7: the picked place or the selection), as the browser plugin's
   * activities: Open, the steps, Close; a recorded password becomes the In argument `password`. One undo step.
   */
  async insertRecording(): Promise<void> {
    const refusal = recordingInsertRefusal(this.state);
    const recorder = this.state.recorder;
    if (refusal !== undefined || recorder === undefined) {
      this.say(`Cannot insert the recording: ${refusal}`);
      return;
    }

    await this.stopRecording();
    let generated;
    try {
      generated = await this.api.generateRecording(
        recorder.startUrl,
        recorder.items.map(({ step, selector, text }) => ({ ...step, selector: selector ?? step.selector, text: text ?? step.text })),
      );
    } catch (error) {
      this.say(`Cannot insert the recording: ${(error as Error).message}`);
      return;
    }

    const state = this.state;
    const target = insertionTargetOf(state);
    if (typeof target === 'string' || 'slot' in target.position || state.document === undefined) {
      this.say(`Cannot insert the recording: ${recordingInsertRefusal(state) ?? 'the place to insert is gone.'}`);
      return;
    }

    let document = state.document;
    const start = target.position.index;
    const nodes = prepareForPaste(generated.nodes, document);
    nodes.forEach((node, i) => {
      document = place(document, { parentPath: target.parentPath, position: { index: start + i } }, node).document;
    });
    const existing = new Set((Array.isArray(document.arguments) ? document.arguments : []).flatMap((a) => (isObject(a) && typeof a.name === 'string' ? [a.name] : [])));
    const added = generated.arguments.filter((a) => typeof a.name === 'string' && !existing.has(a.name));
    if (added.length > 0) {
      document = { ...document, arguments: [...(Array.isArray(document.arguments) ? document.arguments : []), ...added] };
    }

    this.commit(document, keyOf(nodes[0]), `Insert recording (${nodes.length} activities)`);
    this.store.set({ recorder: undefined, insertTarget: undefined });
    this.say(`Inserted the recording: ${nodes.length} activities${added.length > 0 ? `, and the argument ${added.map((a) => a.name as string).join(', ')} (supply it when running)` : ''}.`);
  }

  private receiveRecording(event: RecordingEvent): void {
    const recorder = this.state.recorder;
    if (recorder === undefined || recorder.id !== event.recordingId) {
      return;
    }

    if (event.kind === 'recording.ended') {
      this.updateRecorder((r) => ({ ...r, phase: 'ended', endReason: event.endReason }));
      return;
    }

    const step = event.step;
    if (step === undefined) {
      return;
    }

    this.updateRecorder((r) => {
      const at = step.replaces == null ? -1 : r.items.findIndex((item) => item.step.sequence === step.replaces);
      if (at < 0) {
        return { ...r, items: [...r.items, { step }] };
      }

      const items = r.items.slice();
      items[at] = { step };
      return { ...r, items };
    });
  }

  private updateRecorder(update: (recorder: RecorderState) => RecorderState): void {
    const recorder = this.state.recorder;
    if (recorder !== undefined) {
      this.store.set({ recorder: update(recorder) });
    }
  }

  private updateItem(sequence: number, update: (item: RecordedItem) => RecordedItem): void {
    this.updateRecorder((r) => ({ ...r, items: r.items.map((item) => (item.step.sequence === sequence ? update(item) : item)) }));
  }

  /** Shows a tab of the bottom panel. */
  showOutput(tab: OutputTab): void {
    this.store.set({ outputTab: tab });
  }

  /** Sets a panel's size (px, kept within its limits; remembered per browser). */
  setPaneSize(name: PaneName, size: number): void {
    const limits = paneLimits[name];
    this.updatePanes({ ...this.state.panes, [name]: Math.round(Math.min(limits.max, Math.max(limits.min, size))) });
  }

  /** Shows a hidden panel, or hides a shown one (remembered per browser). */
  togglePane(name: PaneName): void {
    const hidden = this.state.panes.hidden.includes(name) ? this.state.panes.hidden.filter((h) => h !== name) : [...this.state.panes.hidden, name];
    this.updatePanes({ ...this.state.panes, hidden });
  }

  private updatePanes(panes: Panes): void {
    this.store.set({ panes });
    this.preferences.write(preferenceKeys.panes, panes);
  }

  /** Adds an activity type to the favorites, or removes it (remembered per browser). */
  toggleFavorite(type: string): void {
    const favorites = this.state.favorites.includes(type) ? this.state.favorites.filter((t) => t !== type) : [...this.state.favorites, type];
    this.store.set({ favorites });
    this.preferences.write(preferenceKeys.favorites, favorites);
  }

  private rememberRecent(type: string): void {
    const recentActivities = [type, ...this.state.recentActivities.filter((t) => t !== type)].slice(0, maxRecent);
    this.store.set({ recentActivities });
    this.preferences.write(preferenceKeys.recent, recentActivities);
  }

  /** Selects the workflow itself: Properties shows its metadata. */
  selectWorkflow(): void {
    this.select(workflowKey);
  }

  /** Goes to where a diagnostic belongs: its node (and property), its argument or variable row, or the workflow. */
  goToDiagnostic(diagnostic: Diagnostic): void {
    const target = diagnosticTarget(diagnostic, this.state.validated);
    if (target.kind === 'node') {
      // Keys survive edits, so the node is found even after the document changed since validation.
      const document = this.state.document;
      if (target.key !== undefined && document !== undefined && indexDocument(document).byKey.has(target.key)) {
        this.select(target.key);
      } else if (target.nodeId !== undefined) {
        this.selectNodeId(target.nodeId);
      }
    } else if (target.kind === 'row') {
      this.store.set((state) => ({ outputTab: target.list, rowFocus: { list: target.list, index: target.index, seq: (state.rowFocus?.seq ?? 0) + 1 } }));
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
    // A workflow that gains a flowchart (or transitions, or positions) declares format 1.1 in the same step (ADR-0037).
    this.store.set({ document: withGraphSchema(next, state.catalog), selectedKey, undo, redo: [] });
  }

  /** Inserts a new activity of `type` at the selection (see `insertionPoint`), or at `at`, and selects it. */
  insertActivity(type: string, at?: Target, layout?: Point): void {
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
    const placed = place(document, target, node);
    this.commit(layout ? setLayout(placed.document, placed.path, layout.x, layout.y) : placed.document, keyOf(node), `Insert ${activity.displayName}`);
    this.rememberRecent(type);
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
    // Into a flowchart, copied steps keep the transitions among themselves (shifted a little); elsewhere they lose them.
    const intoGraph = isGraphNode(nodeAt(document, target.parentPath), state.catalog) && 'index' in target.position;
    const renamed = prepareForPaste(nodes, document);
    const pasted = intoGraph ? asPastedSteps(renamed) : asListNodes(renamed);
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

  /**
   * Moves the node with `key` (and its subtree) to `target` — across containers and into slots (W7). A flowchart step
   * that leaves its flowchart loses its transitions and the ones that went to it; `layout` places it on a canvas.
   */
  moveNodeTo(key: string, target: Target, layout?: Point): void {
    const refusal = this.moveRefusalTo(key, target);
    const state = this.state;
    if (refusal !== undefined) {
      this.say(`Cannot move: ${refusal}`);
      return;
    }

    const entry = indexDocument(state.document!).byKey.get(key)!;
    const leavesGraph = graphParentPath(state.document!, entry.path, state.catalog) !== undefined && JSON.stringify(entry.path.slice(0, -1)) !== JSON.stringify(target.parentPath);
    let moved = moveTo(leavesGraph ? detachStep(state.document!, entry.path) : state.document!, entry.path, target, state.catalog);
    if (layout) {
      moved = { document: setLayout(moved.document, moved.path, layout.x, layout.y), path: moved.path };
    }

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
    // A flowchart step takes the transitions that went to it along (ADR-0037: no transition may name a missing step).
    const step = graphParentPath(document, path, state.catalog) !== undefined;
    this.commit(removeNode(step ? detachStep(document, path) : document, path), keyOf(next), `Delete ${id}`);
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
        // The explicit Validate shows its result (also "No problems"); background validation never switches the tab.
        outputTab: 'problems',
        message: result.valid
          ? `Valid${result.diagnostics.length > 0 ? ` with ${result.diagnostics.length} warning(s)` : ''}.`
          : `${errors} error(s) found.`,
      });
    } catch (error) {
      this.store.set({ busy: undefined, message: `Validation failed: ${(error as Error).message}` });
    }
  }

  private showDiagnostics(diagnostics: readonly Diagnostic[], document: JsonObject): void {
    const errorNodeKeys = new Set<string>();
    for (const diagnostic of diagnostics) {
      const target = diagnosticTarget(diagnostic, document);
      if (diagnostic.severity === 'Error' && target.kind === 'node' && target.key !== undefined) {
        errorNodeKeys.add(target.key);
      }
    }

    this.store.set({ diagnostics, validated: document, errorNodeKeys });
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
      this.store.set({ file: undefined, document: undefined, saved: undefined, selectedKey: undefined, undo: [], redo: [], diagnostics: undefined, validated: undefined, errorNodeKeys: new Set() });
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

  /**
   * Run (F5): asks for the workflow's input arguments first when it declares any, else runs right away. With `debug`,
   * the run is a debug run (ADR-0040).
   */
  async requestRun(debug?: DebugStart): Promise<void> {
    const refusal = this.runRefusal();
    if (refusal !== undefined) {
      this.say(`Cannot run: ${refusal}`);
      return;
    }

    const { document, file } = this.state;
    const inputs = inputArguments(document!);
    if (inputs.length === 0) {
      await this.run({ debug });
      return;
    }

    const draft = this.argumentDrafts.get(`${file!.project}/${file!.path}`) ?? {};
    this.store.set({ runDialog: { path: file!.path, arguments: inputs, values: draft, timeoutMs: this.lastTimeoutMs, debug } });
  }

  closeRunDialog(): void {
    this.store.set({ runDialog: undefined });
  }

  /** Start from the run dialog: remembers the texts, sends the non-blank ones (blank keeps the default). */
  async startFromDialog(values: Readonly<Record<string, string>>, timeoutMs: number | undefined): Promise<void> {
    const { file, document, runDialog } = this.state;
    if (file === undefined || document === undefined) {
      return;
    }

    const debug = runDialog?.debug;
    this.argumentDrafts.set(`${file.project}/${file.path}`, { ...values });
    this.lastTimeoutMs = timeoutMs;
    // Only arguments the workflow declares now: a remembered text of a renamed or removed argument is never sent.
    const declared = new Set(inputArguments(document).map((a) => a.name));
    const argumentText = Object.fromEntries(Object.entries(values).filter(([name, text]) => declared.has(name) && text.trim() !== ''));
    this.store.set({ runDialog: undefined });
    await this.run({ argumentText: Object.keys(argumentText).length > 0 ? argumentText : undefined, timeoutMs, debug });
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
      debug: options.debug !== undefined ? true : undefined,
    });
    this.store.set({ busy: 'starting', message: `Validating ${file.path}…`, outputTab: 'execution' });

    try {
      const validation = await this.api.validate(document);
      this.showDiagnostics(validation.diagnostics, document);
      if (!validation.valid) {
        const errors = validation.diagnostics.filter((d) => d.severity === 'Error').length;
        this.notStarted(key, 'validation', `Not started: the workflow has ${errors} validation error(s). Fix the problems listed, then run again.`);
        this.showOutput('problems');
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
        debug: options.debug === undefined ? undefined : { breakpoints: breakpointsOf(state), pauseAtStart: options.debug === 'step' },
      });
    } catch (error) {
      if (error instanceof ApiError && error.diagnostics) {
        this.showDiagnostics(error.diagnostics, document);
        this.notStarted(key, 'validation', 'Not started: the workflow has validation errors.');
        this.showOutput('problems');
      } else {
        this.notStarted(key, 'request', `Not started: ${(error as Error).message}`);
      }

      return;
    }

    this.updateRun(key, { runId });
    this.store.set({ busy: undefined, message: `${options.debug ? 'Debug run' : 'Run'} ${runId} started.` });
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

  /**
   * Rename… (ADR-0041): asks the server where the name at `path` is declared and used, then shows the Rename dialog.
   * `path` is a place where the name is visible (its row, or a slot that sees a local).
   */
  async openRename(path: string, name: string): Promise<void> {
    const document = this.state.document;
    const refusal = editRefusal(this.state);
    if (document === undefined || refusal !== undefined) {
      this.say(`Cannot rename: ${refusal}`);
      return;
    }

    this.store.set({ dialog: { kind: 'rename-name', name, path, document } });
    let error: string | undefined;
    let references: readonly NameReference[] | undefined;
    try {
      const found = await this.api.references(document, path, name);
      if (found.declaration === null) {
        error = `'${name}' is not declared here, so it cannot be renamed.`;
      } else {
        references = found.references;
      }
    } catch (e) {
      error = `Cannot find the uses of '${name}': ${(e as Error).message}`;
    }

    const dialog = this.state.dialog;
    if (dialog?.kind === 'rename-name' && dialog.document === document) {
      this.store.set({ dialog: { ...dialog, references, error } });
    }
  }

  /**
   * Applies the Rename dialog: the declaration and every use in one undo step. The server validates the renamed
   * workflow first; a rename that would add a problem (an invalid name, a name already declared, a name a local would
   * hide) is refused with the server's own message, and nothing changes.
   */
  async applyRename(newName: string): Promise<void> {
    const dialog = this.state.dialog;
    const document = this.state.document;
    if (dialog?.kind !== 'rename-name' || dialog.references === undefined || document === undefined) {
      return;
    }

    const name = newName.trim();
    const refuse = (error: string) => this.store.set({ dialog: { ...dialog, busy: false, error } });
    if (name === dialog.name) {
      this.closeDialog();
      return;
    }

    if (document !== dialog.document) {
      refuse('The workflow changed since its uses were found; open Rename again.');
      return;
    }

    const renamed = renameReferences(document, dialog.references, dialog.name, name);
    if (typeof renamed === 'string') {
      refuse(renamed);
      return;
    }

    this.store.set({ dialog: { ...dialog, busy: true, error: undefined } });
    try {
      const [before, after] = await Promise.all([this.api.validate(document), this.api.validate(renamed)]);
      const known = new Set(before.diagnostics.filter((d) => d.severity === 'Error').map((d) => `${d.code}@${d.path}`));
      const added = after.diagnostics.filter((d) => d.severity === 'Error' && !known.has(`${d.code}@${d.path}`));
      if (added.length > 0) {
        refuse(`Not renamed: ${added[0].message}`);
        return;
      }
    } catch (e) {
      refuse(`Not renamed: the server could not check the new name (${(e as Error).message}).`);
      return;
    }

    if (this.state.document !== document) {
      refuse('The workflow changed while the new name was checked; open Rename again.');
      return;
    }

    const uses = dialog.references.filter((r) => !r.declaration).length;
    this.commit(renamed, this.state.selectedKey, `Rename ${dialog.name} to ${name}`);
    this.closeDialog();
    this.say(`Renamed ${dialog.name} to ${name} (the declaration and ${uses} use${uses === 1 ? '' : 's'}).`);
  }

  /** Usages (ADR-0041): the activities that use the name visible at `path`, to jump to. */
  async openUsages(path: string, name: string): Promise<void> {
    const document = this.state.document;
    if (document === undefined) {
      return;
    }

    this.store.set({ dialog: { kind: 'usages', name } });
    let usages: readonly Usage[] | undefined;
    let error: string | undefined;
    try {
      const found = await this.api.references(document, path, name);
      usages = found.declaration === null ? [] : usagesOf(document, found.references, this.state.catalog);
    } catch (e) {
      error = `Cannot find the uses of '${name}': ${(e as Error).message}`;
    }

    if (this.state.dialog?.kind === 'usages' && this.state.dialog.name === name) {
      this.store.set({ dialog: { kind: 'usages', name, usages, error } });
    }
  }

  /** Selects (and reveals) an activity chosen in the Usages dialog. */
  goToUsage(key: string): void {
    this.closeDialog();
    this.select(key);
  }

  /** Debug (F6): runs the open file pausing at its breakpoints; `step` pauses before its first activity (ADR-0040). */
  debug(start: DebugStart = 'breakpoints'): Promise<void> {
    return this.requestRun(start);
  }

  /** Continue, Pause, Step into, Step over or Step out of the current debug run. */
  async debugCommand(command: DebugCommandName, key: string | undefined = this.state.currentRunKey): Promise<void> {
    const run = this.state.runs.find((r) => r.key === key);
    const refusal = debugRefusalOf(run, command);
    const label = debugCommandLabels[command];
    if (run === undefined || refusal !== undefined) {
      this.say(`Cannot ${label.toLowerCase()}: ${refusal}`);
      return;
    }

    try {
      await this.api.debugCommand(run.runId!, command);
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        return; // The run moved on (or finished) meanwhile; the stream says where it is.
      }

      this.say(`Cannot ${label.toLowerCase()}: ${(error as Error).message}`);
    }
  }

  /**
   * Sets or removes a breakpoint on an activity (F9) of the open file. Breakpoints are remembered per browser and file,
   * never saved in the workflow; debug runs of this file that are still going on follow the change.
   */
  toggleBreakpoint(key: string | undefined = this.state.selectedKey): void {
    const state = this.state;
    const refusal = breakpointRefusalOf(state, key);
    if (refusal !== undefined) {
      this.say(`Cannot set a breakpoint: ${refusal}`);
      return;
    }

    const file = state.file!;
    const id = indexDocument(state.document!).byKey.get(key!)!.node.id as string;
    const current = breakpointsOf(state);
    const added = !current.includes(id);
    const next = added ? [...current, id] : current.filter((b) => b !== id);
    const breakpoints = { ...state.breakpoints };
    if (next.length === 0) {
      delete breakpoints[fileKeyOf(file)];
    } else {
      breakpoints[fileKeyOf(file)] = next;
    }

    this.store.set({ breakpoints, message: added ? `Breakpoint set on ${id}.` : `Breakpoint removed from ${id}.` });
    this.preferences.write(preferenceKeys.breakpoints, breakpoints);
    for (const run of state.runs) {
      if (run.debug && run.runId !== undefined && isActive(run) && run.project === file.project && run.path === file.path) {
        this.api.setBreakpoints(run.runId, next).catch((error: Error) => this.say(`Cannot update the breakpoints of run ${run.runId}: ${error.message}`));
      }
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
      if (run !== undefined && event.kind === 'debug.paused') {
        this.paused(run, event);
      }

      if (run !== undefined && !event.parentExecutionId && event.kind === 'execution.completed') {
        const where = event.error?.nodeId ? ` at ${event.error.nodeId}` : '';
        this.say(`Run ${event.runId} ${event.status ?? 'finished'}${event.error ? `${where}: ${event.error.message}` : ''}.`);
        void this.fetchResult(run.key, event.runId);
      }
    }
  }

  /** A debug run paused: say where, show the node when it is in the open file, and fetch the values in scope. */
  private paused(run: RunView, event: ExecutionEvent): void {
    const file = this.state.file;
    const where = event.parentExecutionId ? `${event.nodeId} (in ${event.workflowId})` : event.nodeId;
    this.say(`Run ${event.runId} paused before ${where}: ${event.reason ?? 'pause'}.`);
    if (!event.parentExecutionId && event.nodeId && file !== undefined && run.project === file.project && run.path === file.path) {
      this.selectNodeId(event.nodeId);
    }

    void this.fetchPaused(run.key, event.runId, event.sequence);
  }

  private async fetchPaused(key: string, runId: string, sequence: number): Promise<void> {
    try {
      const state = await this.api.debugState(runId);
      const at = state.paused;
      if (at !== null) {
        this.updateRun(key, (run) =>
          run.paused?.sequence === sequence && run.paused.nodeId === at.nodeId ? { ...run, paused: { ...run.paused, values: at.values } } : run,
        );
      }
    } catch {
      // Where the run paused is known from the stream; the values are shown when they can be read.
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
