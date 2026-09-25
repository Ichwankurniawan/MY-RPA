// A thin client for MyRPA.Server (docs/architecture/server.md). The browser session is the server's own (ADR-0025):
// the cookie set by the start link, sent automatically on same-origin requests. State-changing requests carry the
// anti-forgery header and JSON bodies; the browser adds the Origin header itself.

import type { ActivityDescriptor, Diagnostic, JsonObject, RunStatus, ServerInfo, ValidationResult, WorkflowFile } from './types';

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

export interface StudioApi {
  info(): Promise<ServerInfo>;
  activities(): Promise<ActivityDescriptor[]>;
  workflows(project: string): Promise<WorkflowFile[]>;
  readWorkflow(project: string, path: string): Promise<{ text: string; etag: string }>;
  /** Saves over the version with `etag` (If-Match); returns the new ETag. 412 when the file changed meanwhile. */
  saveWorkflow(project: string, path: string, text: string, etag: string): Promise<string>;
  validate(document: JsonObject): Promise<ValidationResult>;
  /** Runs the saved file, or `document` as if it were saved at `path`. */
  startRun(project: string, path: string, document?: JsonObject): Promise<string>;
  run(runId: string): Promise<RunStatus>;
  createStream(): Promise<string>;
  subscribe(streamId: string, runId: string, afterSequence: number): Promise<void>;
  /** The SSE address of a stream. */
  streamUrl(streamId: string): string;
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
    validate: async (document) => (await send('POST', '/api/validate', JSON.stringify({ document }))).json() as Promise<ValidationResult>,
    async startRun(project, path, document) {
      const response = await send('POST', '/api/runs', JSON.stringify({ project, path, document }));
      return ((await response.json()) as { runId: string }).runId;
    },
    run: (runId) => get<RunStatus>(`/api/runs/${encodeURIComponent(runId)}`),
    async createStream() {
      const response = await send('POST', '/api/streams');
      return ((await response.json()) as { streamId: string }).streamId;
    },
    async subscribe(streamId, runId, afterSequence) {
      await send('POST', `/api/streams/${encodeURIComponent(streamId)}/subscriptions`, JSON.stringify({ runId, afterSequence }));
    },
    streamUrl: (streamId) => `/api/streams/${encodeURIComponent(streamId)}`,
  };
}
