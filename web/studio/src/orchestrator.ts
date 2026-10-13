// The orchestrator's management API (ADR-0049, ADR-0051): /api/orchestrator/*. Same origin and browser session as the
// rest of the Studio (the session is the local owner, an administrator); the Server enforces every role.

import { ApiError } from './api';

export type JobState = 'Queued' | 'Assigned' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled' | 'TimedOut' | 'Interrupted';
export type RunnerStatus = 'Offline' | 'Available' | 'Busy' | 'Paused' | 'Error' | 'Disabled';
export type Role = 'Viewer' | 'Operator' | 'Developer' | 'Admin';
export const roles: readonly Role[] = ['Viewer', 'Operator', 'Developer', 'Admin'];
export const jobStates: readonly JobState[] = ['Queued', 'Assigned', 'Running', 'Succeeded', 'Failed', 'Cancelled', 'TimedOut', 'Interrupted'];

/** Whether `role` includes the rights of `needed` (roles are ordered). */
export const hasRole = (role: Role | undefined, needed: Role): boolean => role !== undefined && roles.indexOf(role) >= roles.indexOf(needed);

export const isActive = (state: JobState): boolean => state === 'Queued' || state === 'Assigned' || state === 'Running';

export interface JobError {
  readonly code: string;
  readonly message: string;
  readonly errorType?: string;
  readonly nodeId?: string;
  readonly activityType?: string;
}

export interface JobSummary {
  readonly jobId: string;
  readonly processId: string;
  readonly processName: string;
  readonly version: number;
  readonly state: JobState;
  readonly createdAt: string;
  readonly assignedAt?: string;
  readonly startedAt?: string;
  readonly completedAt?: string;
  readonly durationMs?: number;
  readonly runnerId?: string;
  readonly runnerName?: string;
  readonly source: string;
  readonly scheduleId?: string;
  /** The entry point (workflow of the package) the job starts. */
  readonly entryPath?: string;
  readonly submittedBy: string;
  readonly attempts: number;
  readonly cancelRequested: boolean;
  readonly error?: JobError;
}

export interface JobAttempt {
  readonly number: number;
  readonly runnerId: string;
  readonly runnerName?: string;
  readonly assignedAt: string;
  readonly startedAt?: string;
  readonly endedAt?: string;
  readonly outcome?: string;
  readonly reason?: string;
}

export interface JobDetail {
  readonly job: JobSummary;
  readonly packageSha256: string;
  readonly entryPath: string;
  readonly priority: number;
  readonly targetRunnerId?: string;
  readonly targetGroup?: string;
  readonly timeoutMs?: number;
  readonly correlationId: string;
  readonly retryOfJobId?: string;
  readonly scheduledFor?: string;
  readonly executionId?: string;
  readonly eventCount: number;
  readonly eventsDropped: number;
  readonly cancelRequestedBy?: string;
  readonly requiredActivities: readonly string[];
  readonly requiredPlugins: readonly { id: string; version: string }[];
  readonly attempts: readonly JobAttempt[];
  /** Business data: present for operators and above only. */
  readonly arguments?: unknown;
  readonly outputs?: unknown;
  readonly dataHidden: boolean;
  /** Why a queued job waits: per Runner, eligible or what it lacks. */
  readonly waiting?: readonly string[];
}

/** An execution event as stored with a job (ADR-0023 shape). */
export interface JobEvent {
  readonly attempt: number;
  readonly sequence: number;
  readonly event: {
    readonly kind: string;
    readonly time: string;
    readonly nodeId?: string;
    readonly activityType?: string;
    readonly status?: string;
    readonly level?: string;
    readonly message?: string;
    readonly parentExecutionId?: string;
    readonly error?: { readonly code: string; readonly message: string };
  };
}

export interface Bucket {
  readonly start: string;
  readonly succeeded: number;
  readonly failed: number;
  readonly cancelled: number;
  readonly interrupted: number;
}

export interface Overview {
  readonly range: string;
  readonly bucketMinutes: number;
  readonly total: number;
  readonly byState: Readonly<Record<JobState, number>>;
  readonly buckets: readonly Bucket[];
  readonly runners: { readonly total: number; readonly byStatus: Readonly<Record<RunnerStatus, number>>; readonly activeJobs: number; readonly capacity: number };
  readonly recent: readonly JobSummary[];
  readonly failures: readonly JobSummary[];
}

export interface ProcessView {
  readonly processId: string;
  readonly name: string;
  readonly project: string;
  readonly createdAt: string;
  readonly latestVersion: number;
  readonly deployedVersion?: number;
  readonly deployedAt?: string;
  readonly deployedBy?: string;
  readonly enabled: boolean;
  readonly targetGroup?: string;
  readonly targetRunnerId?: string;
  readonly priority: number;
  readonly timeoutMs?: number;
  readonly status: 'Published' | 'Deployed' | 'Disabled';
}

export interface PackageArgument {
  readonly name: string;
  readonly direction: string;
  readonly type: string;
  readonly required: boolean;
  readonly secret: boolean;
  /** The default as JSON (absent: none); an empty Run now field means this value. */
  readonly default?: string | null;
}

/** A workflow of a package that a job may start. */
export interface PackageEntryPoint {
  readonly path: string;
  readonly workflowId: string;
  readonly name: string;
  readonly workflowVersion?: string;
  readonly schemaVersion: string;
  readonly arguments: readonly PackageArgument[];
}

export interface VersionView {
  readonly version: number;
  readonly sha256: string;
  readonly size: number;
  readonly format: string;
  readonly project: string;
  readonly entryPoints: readonly PackageEntryPoint[];
  readonly publishedAt: string;
  readonly publishedBy: string;
  readonly notes?: string;
  readonly files: readonly { path: string; sha256: string; size: number }[];
  readonly activities: readonly string[];
  readonly plugins: readonly { id: string; version: string }[];
  readonly excluded: readonly string[];
}

export interface ScheduleView {
  readonly scheduleId: string;
  readonly processId: string;
  readonly entryPath: string;
  readonly name: string;
  readonly kind: 'Once' | 'Interval' | 'Cron';
  readonly cron?: string;
  readonly intervalMinutes?: number;
  readonly startAt?: string;
  readonly timeZone: string;
  readonly enabled: boolean;
  readonly misfire: 'RunOnce' | 'Skip';
  readonly overlap: 'Skip' | 'Allow';
  readonly arguments: unknown;
  readonly createdAt: string;
  readonly createdBy: string;
  readonly lastFireAt?: string;
  readonly nextFireAt?: string;
  readonly lastOutcome?: string;
}

export interface ProcessListItem {
  readonly process: ProcessView;
  readonly latest?: { version: number; sha256: string; publishedAt: string; publishedBy: string };
  readonly schedules: number;
  readonly enabledSchedules: number;
  readonly lastJob?: { jobId: string; state: JobState; createdAt: string; completedAt?: string; version: number };
}

export interface ProcessDetail {
  readonly process: ProcessView;
  readonly versions: readonly VersionView[];
  readonly schedules: readonly ScheduleView[];
  readonly jobs: { readonly total: number; readonly items: readonly JobSummary[] };
  readonly eligibleRunners?: readonly { runnerId: string; name: string; status: RunnerStatus; reason: string }[];
}

export interface RunnerView {
  readonly runnerId: string;
  readonly name: string;
  readonly group?: string;
  readonly status: RunnerStatus;
  readonly activeJobs: number;
  readonly capacity: number;
  readonly maxConcurrency: number;
  readonly paused: boolean;
  readonly disabled: boolean;
  readonly createdAt: string;
  readonly registeredAt?: string;
  readonly lastSeenAt?: string;
  readonly version?: string;
  readonly operatingSystem?: string;
  readonly architecture?: string;
  readonly machineName?: string;
  readonly service: boolean;
  readonly pluginCount: number;
  readonly activityCount: number;
  readonly pluginErrors: number;
}

export interface RunnerDetail {
  readonly runner: RunnerView;
  readonly plugins: readonly { id: string; version: string }[];
  readonly activities: readonly string[];
  readonly pluginErrors: readonly string[];
  readonly jobs: readonly JobSummary[];
}

export interface AccessTokenView {
  readonly tokenId: string;
  readonly name: string;
  readonly role: Role;
  readonly createdAt: string;
  readonly createdBy: string;
  readonly revokedAt?: string;
}

export interface Policy {
  readonly maxAttempts: number;
  readonly maxEventsPerJob: number;
  readonly offlineAfterSeconds: number;
  readonly defaultTimeoutMinutes?: number;
}

export interface AuditEntry {
  readonly id: number;
  readonly time: string;
  readonly actor: string;
  readonly action: string;
  readonly target?: string;
  readonly details?: string;
}

export interface Diagnostics {
  readonly server: { version?: string; startedAt: string; now: string; runnerListener?: string; runnerProtocol: number; heartbeatSeconds: number; leaseSeconds: number };
  readonly store: { provider: string; location: string; sizeBytes: number; schemaVersion: number; counts: Readonly<Record<string, number>> };
  readonly plugins: readonly { id: string; version: string; activities: number }[];
  readonly pluginDiagnostics: readonly { code: string; severity: string; message: string; pluginId?: string }[];
  readonly compatibility: readonly { processId: string; name: string; version: number; plugins: readonly { id: string; version: string }[]; runners: readonly { runnerId: string; name: string; reason: string }[] }[];
}

export interface ScheduleBody {
  readonly name: string;
  /** Job arguments as typed text, converted by the Server like Run now's. */
  readonly argumentText?: Readonly<Record<string, string>>;
  /** The entry point to start (omitted: the deployed package's only entry point). */
  readonly entryPath?: string;
  readonly kind: 'Once' | 'Interval' | 'Cron';
  readonly cron?: string;
  readonly intervalMinutes?: number;
  readonly startAt?: string;
  readonly timeZone: string;
  readonly misfire?: 'RunOnce' | 'Skip';
  readonly overlap?: 'Skip' | 'Allow';
  readonly enabled?: boolean;
}

export interface JobFilter {
  readonly state?: readonly JobState[];
  readonly processId?: string;
  readonly runnerId?: string;
  readonly offset?: number;
  readonly limit?: number;
}

export interface OrchestratorApi {
  me(): Promise<{ name: string; role: Role }>;
  overview(range: '24h' | '7d' | '30d'): Promise<Overview>;
  jobs(filter: JobFilter): Promise<{ total: number; offset: number; jobs: JobSummary[] }>;
  job(jobId: string): Promise<JobDetail>;
  jobEvents(jobId: string): Promise<JobEvent[]>;
  submitJob(body: { processId: string; version?: number; entryPath?: string; argumentText?: Readonly<Record<string, string>>; targetGroup?: string; targetRunnerId?: string }): Promise<JobSummary>;
  cancelJob(jobId: string): Promise<void>;
  retryJob(jobId: string): Promise<JobSummary>;
  processes(): Promise<ProcessListItem[]>;
  process(processId: string): Promise<ProcessDetail>;
  /** Publishes the project as the next version of its process, with the given entry points (workflows a job may start). */
  publish(project: string, entryPoints: readonly string[], notes?: string): Promise<{ process: ProcessView; version: VersionView }>;
  deploy(processId: string, version: number): Promise<ProcessView>;
  undeploy(processId: string): Promise<ProcessView>;
  setProcessEnabled(processId: string, enabled: boolean): Promise<ProcessView>;
  configureProcess(processId: string, settings: { targetGroup?: string; targetRunnerId?: string; priority: number; timeoutMs?: number }): Promise<ProcessView>;
  createSchedule(processId: string, body: ScheduleBody): Promise<ScheduleView>;
  setScheduleEnabled(scheduleId: string, enabled: boolean): Promise<ScheduleView>;
  deleteSchedule(scheduleId: string): Promise<void>;
  previewSchedule(body: ScheduleBody): Promise<{ valid: boolean; error?: string; next?: string[] }>;
  timeZones(): Promise<{ local: string; zones: { id: string; display: string }[] }>;
  runners(): Promise<RunnerView[]>;
  runner(runnerId: string): Promise<RunnerDetail>;
  createRunner(name: string, group: string | undefined, maxConcurrency: number): Promise<{ runnerId: string; name: string; token: string; serverUrl: string }>;
  updateRunner(runnerId: string, change: { group?: string; maxConcurrency?: number; paused?: boolean }): Promise<void>;
  revokeRunner(runnerId: string): Promise<void>;
  rotateRunnerToken(runnerId: string): Promise<{ token: string; serverUrl: string }>;
  tokens(): Promise<AccessTokenView[]>;
  createToken(name: string, role: Role): Promise<{ tokenId: string; token: string }>;
  revokeToken(tokenId: string): Promise<void>;
  policy(): Promise<Policy>;
  setPolicy(policy: Policy): Promise<Policy>;
  audit(offset: number, limit: number): Promise<AuditEntry[]>;
  diagnostics(): Promise<Diagnostics>;
}

const antiForgery = { 'X-MyRPA-Request': '1' };

export function httpOrchestratorApi(fetcher: typeof fetch = (input, init) => fetch(input, init)): OrchestratorApi {
  const base = '/api/orchestrator';
  const fail = async (response: Response): Promise<never> => {
    let body: unknown;
    try {
      body = await response.json();
    } catch {
      body = undefined;
    }

    throw new ApiError(response.status, (body as { error?: string } | undefined)?.error ?? `${response.status} ${response.statusText}`, body);
  };
  const get = async <T>(path: string): Promise<T> => {
    const response = await fetcher(base + path, { headers: { Accept: 'application/json' } });
    return response.ok ? ((await response.json()) as T) : fail(response);
  };
  const send = async <T>(method: string, path: string, body?: unknown): Promise<T> => {
    const response = await fetcher(base + path, {
      method,
      headers: { ...antiForgery, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) {
      return fail(response);
    }

    return (response.status === 204 ? undefined : await response.json()) as T;
  };
  const id = encodeURIComponent;
  return {
    me: () => get('/me'),
    overview: (range) => get(`/overview?range=${range}`),
    jobs(filter) {
      const query = new URLSearchParams();
      if (filter.state && filter.state.length > 0) {
        query.set('state', filter.state.join(','));
      }

      for (const [key, value] of [['processId', filter.processId], ['runnerId', filter.runnerId], ['offset', filter.offset], ['limit', filter.limit]] as const) {
        if (value !== undefined && value !== '') {
          query.set(key, String(value));
        }
      }

      return get(`/jobs?${query.toString()}`);
    },
    job: (jobId) => get(`/jobs/${id(jobId)}`),
    jobEvents: async (jobId) => (await get<{ events: JobEvent[] }>(`/jobs/${id(jobId)}/events?limit=1000`)).events,
    submitJob: async (body) => (await send<{ job: JobSummary }>('POST', '/jobs', body)).job,
    cancelJob: async (jobId) => void (await send('POST', `/jobs/${id(jobId)}/cancel`)),
    retryJob: async (jobId) => (await send<{ job: JobSummary }>('POST', `/jobs/${id(jobId)}/retry`, {})).job,
    processes: async () => (await get<{ processes: ProcessListItem[] }>('/processes')).processes,
    process: (processId) => get(`/processes/${id(processId)}`),
    publish: (project, entryPoints, notes) => send('POST', '/publish', { project, entryPoints, notes }),
    deploy: (processId, version) => send('POST', `/processes/${id(processId)}/deploy`, { version }),
    undeploy: (processId) => send('POST', `/processes/${id(processId)}/undeploy`),
    setProcessEnabled: (processId, enabled) => send('POST', `/processes/${id(processId)}/enabled`, { enabled }),
    configureProcess: (processId, settings) => send('POST', `/processes/${id(processId)}/settings`, settings),
    createSchedule: (processId, body) => send('POST', `/processes/${id(processId)}/schedules`, body),
    setScheduleEnabled: (scheduleId, enabled) => send('POST', `/schedules/${id(scheduleId)}/enabled`, { enabled }),
    deleteSchedule: async (scheduleId) => void (await send('DELETE', `/schedules/${id(scheduleId)}`)),
    previewSchedule(body) {
      const query = new URLSearchParams({ kind: body.kind, timeZone: body.timeZone });
      if (body.cron !== undefined) {
        query.set('cron', body.cron);
      }

      if (body.intervalMinutes !== undefined) {
        query.set('intervalMinutes', String(body.intervalMinutes));
      }

      if (body.startAt !== undefined) {
        query.set('startAt', body.startAt);
      }

      return get(`/schedules/preview?${query.toString()}`);
    },
    timeZones: () => get('/timezones'),
    runners: async () => (await get<{ runners: RunnerView[] }>('/runners')).runners,
    runner: (runnerId) => get(`/runners/${id(runnerId)}`),
    createRunner: (name, group, maxConcurrency) => send('POST', '/runners', { name, group, maxConcurrency }),
    updateRunner: async (runnerId, change) => void (await send('PATCH', `/runners/${id(runnerId)}`, change)),
    revokeRunner: async (runnerId) => void (await send('POST', `/runners/${id(runnerId)}/revoke`)),
    rotateRunnerToken: (runnerId) => send('POST', `/runners/${id(runnerId)}/token`),
    tokens: async () => (await get<{ tokens: AccessTokenView[] }>('/tokens')).tokens,
    createToken: (name, role) => send('POST', '/tokens', { name, role }),
    revokeToken: async (tokenId) => void (await send('POST', `/tokens/${id(tokenId)}/revoke`)),
    policy: () => get('/policy'),
    setPolicy: (policy) => send('PUT', '/policy', policy),
    audit: async (offset, limit) => (await get<{ entries: AuditEntry[] }>(`/audit?offset=${offset}&limit=${limit}`)).entries,
    diagnostics: () => get('/diagnostics'),
  };
}
