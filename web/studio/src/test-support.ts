// Test doubles for the unit and component tests (never imported by the application).

import { ApiError, type StudioApi } from './api';
import type { EventSourceLike } from './events';
import type { ActivityDescriptor, ExecutionEvent, JsonObject, RunStatus, ValidationResult } from './types';

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
  runs: { path: string; document?: JsonObject }[] = [];
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

    return { name: 'MyRPA.Server', mode: 'local', workflowSchemaVersions: ['1.0'], projects: ['demo'] };
  }

  async activities() {
    return catalog;
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

  async validate() {
    return this.validation;
  }

  async startRun(_project: string, path: string, document?: JsonObject) {
    if (this.runRefusal) {
      throw new ApiError(422, 'invalid', this.runRefusal);
    }

    this.runs.push({ path, document });
    return `run-${this.runs.length}`;
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

/** Lets pending promise callbacks run. */
export const settle = () => new Promise((resolve) => setTimeout(resolve, 0));
