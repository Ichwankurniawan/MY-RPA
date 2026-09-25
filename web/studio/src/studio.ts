// The Studio's state and commands. The Web Studio owns the editing model; the server owns projects, files,
// validation and execution; the engine knows nothing about this client.

import { ApiError, type StudioApi } from './api';
import { indexDocument, keyOf, openWorkflow, serialize, setDisplayName, setProperty, isObject } from './document';
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

export const isDirty = (state: StudioState): boolean => state.document !== state.saved;

export class Studio {
  readonly store: Store<StudioState>;
  private readonly events: RunEventStream;

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
      this.store.set({
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
    this.edit(key, (document, path) => setProperty(document, path, descriptor, text));
  }

  editDisplayName(key: string, text: string): void {
    this.edit(key, (document, path) => setDisplayName(document, path, text));
  }

  private edit(key: string, apply: (document: JsonObject, path: Parameters<typeof setProperty>[1]) => JsonObject): void {
    const { document, file } = this.state;
    if (document === undefined || file === undefined || file.readOnlyReason !== undefined) {
      return;
    }

    const entry = indexDocument(document).byKey.get(key);
    if (entry !== undefined) {
      this.store.set({ document: apply(document, entry.path) });
    }
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
