// Test doubles for the unit and component tests (never imported by the application).

import { ApiError, type StartRunOptions, type StudioApi } from './api';
import type { EventSourceLike } from './events';
import type { ActivityDescriptor, DebugCommandName, DebugState, ExecutionEvent, ExpressionFunction, NameReferences, ScopeName, GeneratedActivities, JsonObject, PluginReport, RecordedStep, RunStatus, ValidationResult } from './types';

export const helloWorld = `{
  "schemaVersion": "1.0",
  "id": "hello-world",
  "name": "Hello World",
  "version": "1.0.0",
  "arguments": [
    { "name": "userName", "direction": "In", "type": "String", "default": "World" },
    { "name": "greeting", "direction": "Out", "type": "String" }
  ],
  "root": {
    "id": "main",
    "type": "Core.Sequence",
    "children": [
      { "id": "build-greeting", "type": "Core.Assign", "properties": { "to": "greeting", "value": "'Hello, ' + userName + '!'" } },
      { "id": "log-greeting", "type": "Core.Log", "properties": { "message": "greeting" } }
    ]
  }
}
`;

const expression = (name: string, required: boolean) => ({ name, kind: 'Expression' as const, required, allowedValues: [], scopeSlots: [] });

export const catalog: ActivityDescriptor[] = [
  { type: 'Core.Sequence', displayName: 'Sequence', category: 'Control Flow', allowsChildren: true, properties: [], slots: [] },
  {
    type: 'Core.Assign',
    displayName: 'Assign',
    category: 'Data',
    allowsChildren: false,
    properties: [{ name: 'to', kind: 'AssignmentTarget', required: true, allowedValues: [], scopeSlots: [] }, expression('value', true)],
    slots: [],
  },
  {
    type: 'Core.Log',
    displayName: 'Log',
    category: 'Diagnostics',
    description: 'Writes a log message.',
    allowsChildren: false,
    properties: [
      { ...expression('message', true), description: 'The message.' },
      { name: 'level', kind: 'Text', required: false, allowedValues: ['Information', 'Warning', 'Error'], scopeSlots: [] },
    ],
    slots: [],
  },
  {
    type: 'Core.If',
    displayName: 'If',
    category: 'Control Flow',
    allowsChildren: false,
    properties: [expression('condition', true)],
    slots: [
      { name: 'then', required: true, prefix: false },
      { name: 'else', required: false, prefix: false },
    ],
  },
  {
    type: 'Core.InvokeWorkflow',
    displayName: 'Invoke Workflow',
    category: 'Workflow',
    allowsChildren: false,
    properties: [
      { name: 'workflow', kind: 'Text', required: true, allowedValues: [], scopeSlots: [] },
      { name: 'arguments', kind: 'ExpressionMap', required: false, allowedValues: [], scopeSlots: [] },
      { name: 'outputs', kind: 'AssignmentTargetMap', required: false, allowedValues: [], scopeSlots: [] },
    ],
    slots: [],
  },
];

export class FakeEventSource implements EventSourceLike {
  static instances: FakeEventSource[] = [];
  readyState = 1;
  onerror: ((event: Event) => void) | null = null;
  private readonly listeners = new Map<string, ((event: MessageEvent<string>) => void)[]>();

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, listener: (event: MessageEvent<string>) => void): void {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
  }

  close(): void {
    this.readyState = 2;
  }

  emit(event: ExecutionEvent): void {
    for (const listener of this.listeners.get(event.kind) ?? []) {
      listener({ data: JSON.stringify(event) } as MessageEvent<string>);
    }
  }

  /** The server no longer knows the stream (the browser gives up: readyState CLOSED). */
  fail(): void {
    this.readyState = 2;
    this.onerror?.(new Event('error'));
  }
}

/** An in-memory server: one project with files and ETags, validation and run results supplied by the test. */
export class FakeApi implements StudioApi {
  files = new Map<string, { text: string; etag: number }>([['hello-world.json', { text: helloWorld, etag: 1 }]]);
  saves: { path: string; text: string; etag: string }[] = [];
  runs: ({ path: string } & StartRunOptions)[] = [];
  cancels: string[] = [];
  startFailure?: ApiError;
  cancelFailure?: ApiError;
  /** Expression functions the fake server knows (ADR-0041). */
  functions: ExpressionFunction[] = [
    { name: 'len', minArguments: 1, maxArguments: 1, signature: 'len(value)', description: 'The length of a String, or the number of items in a List or Dictionary.' },
    { name: 'lower', minArguments: 1, maxArguments: 1, signature: 'lower(text)', description: 'The text in lower case.' },
    { name: 'now', minArguments: 0, maxArguments: 0, signature: 'now()', description: 'The current date and time.' },
    { name: 'substring', minArguments: 2, maxArguments: 3, signature: 'substring(text, start, length?)', description: 'Part of the text from start (0-based) to the end, or length characters.' },
  ];
  /** What `POST /api/expressions/scope` answers (the same names for every path). */
  scopeNames: ScopeName[] = [];
  /** The paths names in scope were asked for. */
  scopeRequests: string[] = [];
  /** What `POST /api/expressions/references` answers, by name (no declaration when missing). */
  referenceAnswers = new Map<string, NameReferences>();
  /** The (path, name) references were asked for. */
  referenceRequests: { path: string; name: string }[] = [];
  /** The documents validated, in order (rename checks the renamed workflow). */
  validated: JsonObject[] = [];
  /** Validation results to answer for documents that contain this text (else `validation`). */
  validationFor?: { readonly contains: string; readonly result: ValidationResult };
  /** Debug commands sent (ADR-0040). */
  debugCommands: { runId: string; command: DebugCommandName }[] = [];
  /** Breakpoint updates sent to running debug runs. */
  breakpointUpdates: { runId: string; breakpoints: readonly string[] }[] = [];
  /** What `GET /api/runs/{id}/debug` answers, per run. */
  debugStates = new Map<string, DebugState>();
  debugCommandFailure?: ApiError;
  validations = 0;
  /** The server's `--open` workflow, if any. */
  openOnStart?: { project: string; path: string };
  subscriptions: { streamId: string; runId: string; afterSequence: number }[] = [];
  streamsCreated = 0;
  signedIn = true;
  validation: ValidationResult = { valid: true, diagnostics: [] };
  runRefusal?: ValidationResult;
  outputs: JsonObject = { greeting: 'Hello, World!' };

  async info() {
    if (!this.signedIn) {
      throw new ApiError(401, 'Open the server with the start link it printed.');
    }

    return { name: 'MyRPA.Server', mode: 'local', workflowSchemaVersions: ['1.0'], projects: ['demo'], open: this.openOnStart };
  }

  async activities() {
    return catalog;
  }

  pluginReport: PluginReport = { plugins: [], diagnostics: [] };

  // Recording (ADR-0039).
  recordingAvailable = true;
  recordingsStarted: string[] = [];
  recordingsStopped: string[] = [];
  recordingSubscriptions: { streamId: string; recordingId: string; afterSequence: number }[] = [];
  generated: { startUrl: string; steps: readonly RecordedStep[] }[] = [];

  async recordingInfo() {
    return this.recordingAvailable ? { available: true } : { available: false, reason: 'Recording needs the browser plugin.' };
  }

  async startRecording(startUrl: string) {
    this.recordingsStarted.push(startUrl);
    return `rec-${this.recordingsStarted.length}`;
  }

  async stopRecording(recordingId: string) {
    this.recordingsStopped.push(recordingId);
  }

  async subscribeRecording(streamId: string, recordingId: string, afterSequence: number) {
    this.recordingSubscriptions.push({ streamId, recordingId, afterSequence });
  }

  /** Like the browser plugin: Open, one node per step, Close; a password is the `password` argument. */
  async generateRecording(startUrl: string, steps: readonly RecordedStep[]): Promise<GeneratedActivities> {
    this.generated.push({ startUrl, steps });
    const nodes: JsonObject[] = [{ id: 'open-1', type: 'Browser.Open', properties: { url: `'${startUrl}'` } }];
    steps.forEach((step, i) => {
      const properties: JsonObject = step.kind === 'navigate' ? { url: `'${step.url}'` } : { selector: step.selector ?? '' };
      if (step.kind === 'type') {
        (properties as Record<string, string>).text = step.secret ? 'password' : `'${step.text ?? ''}'`;
      }

      nodes.push({ id: `${step.kind}-${i + 1}`, type: `Browser.${step.kind}`, properties });
    });
    nodes.push({ id: 'close-1', type: 'Browser.Close' });
    return { nodes, arguments: steps.some((s) => s.secret) ? [{ name: 'password', direction: 'In', type: 'String', required: true }] : [] };
  }

  async plugins() {
    return this.pluginReport;
  }

  async workflows() {
    return [...this.files.keys()].map((path) => ({ path, size: 1, modified: '2026-09-25T00:00:00Z' }));
  }

  async readWorkflow(_project: string, path: string) {
    const file = this.files.get(path);
    if (!file) {
      throw new ApiError(404, 'not found');
    }

    return { text: file.text, etag: `"${file.etag}"` };
  }

  async saveWorkflow(_project: string, path: string, text: string, etag: string) {
    this.saves.push({ path, text, etag });
    const file = this.files.get(path)!;
    if (`"${file.etag}"` !== etag) {
      throw new ApiError(412, 'The file changed since it was read (or already exists).');
    }

    file.text = text;
    file.etag++;
    return `"${file.etag}"`;
  }

  async createWorkflow(_project: string, path: string, text: string) {
    if (this.files.has(path)) {
      throw new ApiError(412, 'The file changed since it was read (or already exists).');
    }

    this.files.set(path, { text, etag: 1 });
    return '"1"';
  }

  async deleteWorkflow(_project: string, path: string, etag: string) {
    const file = this.files.get(path);
    if (!file) {
      throw new ApiError(404, `'${path}' does not exist.`);
    }

    if (`"${file.etag}"` !== etag) {
      throw new ApiError(412, 'The file changed since it was read.');
    }

    this.files.delete(path);
  }

  async moveWorkflow(_project: string, from: string, to: string, etag: string) {
    const file = this.files.get(from);
    if (!file) {
      throw new ApiError(404, `'${from}' does not exist.`);
    }

    if (`"${file.etag}"` !== etag) {
      throw new ApiError(412, 'The file changed since it was read.');
    }

    if (this.files.has(to)) {
      throw new ApiError(409, `'${to}' already exists.`);
    }

    this.files.delete(from);
    this.files.set(to, file);
    return etag;
  }

  async validate(document?: JsonObject) {
    this.validations++;
    if (document !== undefined) {
      this.validated.push(document);
      if (this.validationFor !== undefined && JSON.stringify(document).includes(this.validationFor.contains)) {
        return this.validationFor.result;
      }
    }

    return this.validation;
  }

  async startRun(_project: string, path: string, options: StartRunOptions = {}) {
    if (this.runRefusal) {
      throw new ApiError(422, 'invalid', this.runRefusal);
    }

    if (this.startFailure) {
      throw this.startFailure;
    }

    this.runs.push({ path, ...options });
    return `run-${this.runs.length}`;
  }

  async cancelRun(runId: string) {
    this.cancels.push(runId);
    if (this.cancelFailure) {
      throw this.cancelFailure;
    }
  }

  async expressionFunctions() {
    return this.functions;
  }

  async references(_document: JsonObject, path: string, name: string) {
    this.referenceRequests.push({ path, name });
    return this.referenceAnswers.get(name) ?? { declaration: null, references: [] };
  }

  async namesInScope(_document: JsonObject, path: string) {
    this.scopeRequests.push(path);
    return this.scopeNames;
  }

  async debugState(runId: string): Promise<DebugState> {
    const state = this.debugStates.get(runId);
    if (!state) {
      throw new ApiError(404, `Unknown debug run '${runId}'.`);
    }

    return state;
  }

  async debugCommand(runId: string, command: DebugCommandName) {
    this.debugCommands.push({ runId, command });
    if (this.debugCommandFailure) {
      throw this.debugCommandFailure;
    }
  }

  async setBreakpoints(runId: string, breakpoints: readonly string[]) {
    this.breakpointUpdates.push({ runId, breakpoints: [...breakpoints] });
  }

  async run(runId: string): Promise<RunStatus> {
    return {
      runId,
      state: 'Completed',
      lastSequence: 6,
      result: { status: 'Succeeded', executionId: 'e1', correlationId: runId, workflowId: 'hello-world', durationMs: 12, outputs: this.outputs },
    };
  }

  async createStream() {
    this.streamsCreated++;
    return `stream-${this.streamsCreated}`;
  }

  async subscribe(streamId: string, runId: string, afterSequence: number) {
    this.subscriptions.push({ streamId, runId, afterSequence });
  }

  streamUrl(streamId: string) {
    return `/api/streams/${streamId}`;
  }
}

/** The events of a successful hello-world run. */
export function helloWorldEvents(runId: string): ExecutionEvent[] {
  const base = { runId, time: '2026-09-25T00:00:00Z', executionId: 'e1' };
  return [
    { ...base, sequence: 1, kind: 'execution.started', workflowId: 'hello-world' },
    { ...base, sequence: 2, kind: 'node.started', nodeId: 'main', activityType: 'Core.Sequence' },
    { ...base, sequence: 3, kind: 'node.started', nodeId: 'log-greeting', activityType: 'Core.Log' },
    { ...base, sequence: 4, kind: 'log', nodeId: 'log-greeting', level: 'Information', message: 'Hello, World!' },
    { ...base, sequence: 5, kind: 'node.completed', nodeId: 'log-greeting', activityType: 'Core.Log', status: 'Succeeded' },
    { ...base, sequence: 6, kind: 'node.completed', nodeId: 'main', activityType: 'Core.Sequence', status: 'Succeeded' },
    { ...base, sequence: 7, kind: 'execution.completed', workflowId: 'hello-world', status: 'Succeeded', durationMs: 12 },
  ];
}

/** Applies streamed events at once (the application batches them per animation frame). */
export const immediately = (flush: () => void) => flush();

/** Lets pending promise callbacks run. */
export const settle = () => new Promise((resolve) => setTimeout(resolve, 0));
