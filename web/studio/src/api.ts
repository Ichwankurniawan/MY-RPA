// A thin client for MyRPA.Server (docs/architecture/server.md). The browser session is the server's own (ADR-0025):
// the cookie set by the start link, sent automatically on same-origin requests. State-changing requests carry the
// anti-forgery header and JSON bodies; the browser adds the Origin header itself.

import type { ActivityDescriptor, DebugCommandName, DebugState, Diagnostic, ExpressionFunction, GeneratedActivities, JsonObject, PluginReport, RecordedStep, RunStatus, ScopeName, ServerInfo, ValidationResult, WorkflowFile } from './types';

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly body?: unknown,
  ) {
    super(message);
  }

  /** Diagnostics from a 422 run response (the workflow did not validate). */
  get diagnostics(): Diagnostic[] | undefined {
    const body = this.body as Partial<ValidationResult> | undefined;
    return this.status === 422 && Array.isArray(body?.diagnostics) ? body.diagnostics : undefined;
  }
}

/** Options of `POST /api/runs` beyond the file. */
export interface StartRunOptions {
  /** An unsaved document, run as if it were saved at `path`. */
  readonly document?: JsonObject;
  /** Input arguments as typed text, parsed by the server like the CLI's `--arg` (ADR-0030). */
  readonly argumentText?: Readonly<Record<string, string>>;
  readonly timeoutMs?: number;
  /** Starts a debug run (ADR-0040): pause before these node ids of the workflow, or before its first node. */
  readonly debug?: { readonly breakpoints: readonly string[]; readonly pauseAtStart?: boolean };
}

export interface StudioApi {
  info(): Promise<ServerInfo>;
  activities(): Promise<ActivityDescriptor[]>;
  plugins(): Promise<PluginReport>;
  workflows(project: string): Promise<WorkflowFile[]>;
  readWorkflow(project: string, path: string): Promise<{ text: string; etag: string }>;
  /** Saves over the version with `etag` (If-Match); returns the new ETag. 412 when the file changed meanwhile. */
  saveWorkflow(project: string, path: string, text: string, etag: string): Promise<string>;
  /** Creates a new file (If-None-Match: *); returns its ETag. 412 when the file already exists. */
  createWorkflow(project: string, path: string, text: string): Promise<string>;
  /** Deletes the version with `etag` (If-Match). 412 when the file changed meanwhile, 404 when it is gone. */
  deleteWorkflow(project: string, path: string, etag: string): Promise<void>;
  /** Renames or moves the version with `etag` within the project; returns its ETag (unchanged). 409 when `to` exists. */
  moveWorkflow(project: string, from: string, to: string, etag: string): Promise<string>;
  validate(document: JsonObject): Promise<ValidationResult>;
  /** The expression functions with their signatures (ADR-0041). */
  expressionFunctions(): Promise<ExpressionFunction[]>;
  /** The names visible at `path` of `document`, as the server's validation sees them (ADR-0041). */
  namesInScope(document: JsonObject, path: string): Promise<ScopeName[]>;
  /** Runs the saved file, or `options.document` as if it were saved at `path`. 422 (invalid) and 400 mean not started. */
  startRun(project: string, path: string, options?: StartRunOptions): Promise<string>;
  run(runId: string): Promise<RunStatus>;
  /** Requests cooperative cancellation (202). 409 when the run already finished. The outcome arrives on the stream. */
  cancelRun(runId: string): Promise<void>;
  /** A debug run's paused state with its values, and its breakpoints (ADR-0040). 404 for runs that are not this session's debug runs. */
  debugState(runId: string): Promise<DebugState>;
  /** Continues, steps or pauses a debug run (202). 409 when it is not paused (or, for pause, already paused or finished). */
  debugCommand(runId: string, command: DebugCommandName): Promise<void>;
  /** Replaces a debug run's breakpoints (node ids of its workflow), from its next node on. */
  setBreakpoints(runId: string, breakpoints: readonly string[]): Promise<void>;
  createStream(): Promise<string>;
  subscribe(streamId: string, runId: string, afterSequence: number): Promise<void>;
  /** The SSE address of a stream. */
  streamUrl(streamId: string): string;
  /** Whether this server can record (it needs the browser plugin, ADR-0039). */
  recordingInfo(): Promise<{ available: boolean; reason?: string | null }>;
  /** Opens a recording browser at the URL; returns the recording id. 409 while another recording runs. */
  startRecording(startUrl: string): Promise<string>;
  stopRecording(recordingId: string): Promise<void>;
  subscribeRecording(streamId: string, recordingId: string, afterSequence: number): Promise<void>;
  /** The browser plugin's activities for the steps the user kept (the Studio knows no browser activity). */
  generateRecording(startUrl: string, steps: readonly RecordedStep[]): Promise<GeneratedActivities>;
}

const antiForgery = { 'X-MyRPA-Request': '1' };

function filePath(project: string, path: string): string {
  return `/api/projects/${encodeURIComponent(project)}/workflows/${path.split('/').map(encodeURIComponent).join('/')}`;
}

async function failure(response: Response): Promise<ApiError> {
  let body: unknown;
  try {
    body = await response.json();
  } catch {
    body = undefined;
  }

  const message = (body as { error?: string } | undefined)?.error ?? `${response.status} ${response.statusText}`;
  return new ApiError(response.status, message, body);
}

export function httpApi(fetcher: typeof fetch = (input, init) => fetch(input, init)): StudioApi {
  const get = async <T>(url: string): Promise<T> => {
    const response = await fetcher(url, { headers: { Accept: 'application/json' } });
    if (!response.ok) {
      throw await failure(response);
    }

    return (await response.json()) as T;
  };

  const send = async (method: string, url: string, body?: string, headers: Record<string, string> = {}): Promise<Response> => {
    const response = await fetcher(url, {
      method,
      headers: { ...antiForgery, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), ...headers },
      body,
    });
    if (!response.ok) {
      throw await failure(response);
    }

    return response;
  };

  return {
    info: () => get<ServerInfo>('/api/info'),
    activities: async () => (await get<{ activities: ActivityDescriptor[] }>('/api/activities')).activities,
    plugins: () => get<PluginReport>('/api/plugins'),
    workflows: async (project) => (await get<{ workflows: WorkflowFile[] }>(`/api/projects/${encodeURIComponent(project)}/workflows`)).workflows,
    async readWorkflow(project, path) {
      const response = await fetcher(filePath(project, path), { headers: { Accept: 'application/json' } });
      if (!response.ok) {
        throw await failure(response);
      }

      return { text: await response.text(), etag: response.headers.get('ETag') ?? '' };
    },
    async saveWorkflow(project, path, text, etag) {
      const response = await send('PUT', filePath(project, path), text, { 'If-Match': etag });
      return response.headers.get('ETag') ?? '';
    },
    async createWorkflow(project, path, text) {
      const response = await send('PUT', filePath(project, path), text, { 'If-None-Match': '*' });
      return response.headers.get('ETag') ?? '';
    },
    async deleteWorkflow(project, path, etag) {
      await send('DELETE', filePath(project, path), undefined, { 'If-Match': etag });
    },
    async moveWorkflow(project, from, to, etag) {
      const response = await send('POST', `/api/projects/${encodeURIComponent(project)}/move`, JSON.stringify({ from, to }), { 'If-Match': etag });
      return response.headers.get('ETag') ?? etag;
    },
    validate: async (document) => (await send('POST', '/api/validate', JSON.stringify({ document }))).json() as Promise<ValidationResult>,
    expressionFunctions: () => get<ExpressionFunction[]>('/api/expressions/functions'),
    async namesInScope(document, path) {
      const response = await send('POST', '/api/expressions/scope', JSON.stringify({ document, path }));
      return ((await response.json()) as { names: ScopeName[] }).names;
    },
    async startRun(project, path, options = {}) {
      const response = await send('POST', '/api/runs', JSON.stringify({ project, path, ...options }));
      return ((await response.json()) as { runId: string }).runId;
    },
    run: (runId) => get<RunStatus>(`/api/runs/${encodeURIComponent(runId)}`),
    async cancelRun(runId) {
      await send('POST', `/api/runs/${encodeURIComponent(runId)}/cancel`);
    },
    debugState: (runId) => get<DebugState>(`/api/runs/${encodeURIComponent(runId)}/debug`),
    async debugCommand(runId, command) {
      await send('POST', `/api/runs/${encodeURIComponent(runId)}/debug`, JSON.stringify({ command }));
    },
    async setBreakpoints(runId, breakpoints) {
      await send('PUT', `/api/runs/${encodeURIComponent(runId)}/breakpoints`, JSON.stringify({ breakpoints }));
    },
    async createStream() {
      const response = await send('POST', '/api/streams');
      return ((await response.json()) as { streamId: string }).streamId;
    },
    async subscribe(streamId, runId, afterSequence) {
      await send('POST', `/api/streams/${encodeURIComponent(streamId)}/subscriptions`, JSON.stringify({ runId, afterSequence }));
    },
    streamUrl: (streamId) => `/api/streams/${encodeURIComponent(streamId)}`,
    recordingInfo: async () => get<{ available: boolean; reason?: string | null }>('/api/recordings'),
    async startRecording(startUrl) {
      const response = await send('POST', '/api/recordings', JSON.stringify({ startUrl }));
      return ((await response.json()) as { recordingId: string }).recordingId;
    },
    async stopRecording(recordingId) {
      await send('DELETE', `/api/recordings/${encodeURIComponent(recordingId)}`);
    },
    async subscribeRecording(streamId, recordingId, afterSequence) {
      await send('POST', `/api/streams/${encodeURIComponent(streamId)}/subscriptions`, JSON.stringify({ recordingId, afterSequence }));
    },
    async generateRecording(startUrl, steps) {
      const response = await send('POST', '/api/recordings/generate', JSON.stringify({ startUrl, steps }));
      return (await response.json()) as GeneratedActivities;
    },
  };
}
