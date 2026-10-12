import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { ApiError } from './api';
import { App } from './App';
import type { JobDetail, JobEvent, JobSummary, OrchestratorApi, Overview, ProcessDetail, ProcessListItem, Role, RunnerView } from './orchestrator';
import { memoryPreferences } from './preferences';
import { Studio } from './studio';
import { enterStudio, FakeApi, FakeEventSource, immediately, settle } from './test-support';

// Phase 10 operations pages (ADR-0049): Monitoring, Deployments, Robots and Administration show the Server's data and
// call its API; actions are disabled with a reason when the role lacks the right (the Server enforces it as well).

class OrchestratedApi extends FakeApi {
  override async info() {
    return { ...(await super.info()), orchestrator: true };
  }
}

const now = '2026-10-12T08:00:00Z';

const job = (over: Partial<JobSummary> = {}): JobSummary => ({
  jobId: 'job_1',
  processId: 'prc_1',
  processName: 'Invoice check',
  version: 2,
  state: 'Running',
  createdAt: now,
  source: 'manual',
  submittedBy: 'local owner',
  attempts: 1,
  cancelRequested: false,
  runnerId: 'rnr_1',
  runnerName: 'finance-1',
  ...over,
});

const runner = (over: Partial<RunnerView> = {}): RunnerView => ({
  runnerId: 'rnr_1',
  name: 'finance-1',
  group: 'finance',
  status: 'Busy',
  activeJobs: 1,
  capacity: 1,
  maxConcurrency: 1,
  paused: false,
  disabled: false,
  createdAt: now,
  lastSeenAt: now,
  operatingSystem: 'Windows 11',
  architecture: 'X64',
  machineName: 'VM-FIN-01',
  service: false,
  pluginCount: 2,
  activityCount: 120,
  pluginErrors: 0,
  ...over,
});

const process: ProcessListItem = {
  process: { processId: 'prc_1', name: 'Invoice check', project: 'demo', createdAt: now, latestVersion: 2, deployedVersion: 2, enabled: true, priority: 0, status: 'Deployed' },
  latest: { version: 2, sha256: 'b'.repeat(64), publishedAt: now, publishedBy: 'local owner' },
  schedules: 1,
  enabledSchedules: 1,
  lastJob: { jobId: 'job_1', state: 'Running', createdAt: now, version: 2 },
};

const invoiceEntry = {
  path: 'hello-world.json',
  workflowId: 'invoice',
  name: 'Invoice check',
  schemaVersion: '1.0',
  arguments: [
    { name: 'customer', direction: 'In', type: 'String', required: true, secret: false },
    { name: 'password', direction: 'In', type: 'String', required: false, secret: true },
  ],
};
const importEntry = { path: 'orders/import.json', workflowId: 'import', name: 'Import orders', schemaVersion: '1.0', arguments: [{ name: 'batch', direction: 'In', type: 'Int', required: false, secret: false }] };

const version = (n: number) => ({
  version: n,
  sha256: `${n}`.repeat(64),
  size: 900,
  format: 'myrpa-package/2',
  project: 'demo',
  entryPoints: n === 2 ? [invoiceEntry, importEntry] : [invoiceEntry],
  publishedAt: now,
  publishedBy: 'local owner',
  files: [
    { path: 'hello-world.json', sha256: 'x', size: 900 },
    { path: 'orders/import.json', sha256: 'y', size: 400 },
  ],
  activities: ['Core.Log'],
  plugins: n === 2 ? [{ id: 'MyRPA.Ai.Agent', version: '0.1.0' }] : [],
  excluded: [],
});

class FakeOrchestrator implements OrchestratorApi {
  role: Role = 'Admin';
  calls: string[] = [];
  runnerList: RunnerView[] = [runner()];
  jobList: JobSummary[] = [job(), job({ jobId: 'job_0', state: 'Failed', error: { code: 'MYRPA5001', message: 'This Runner lacks plugin X.', errorType: 'DependencyMissing' } })];
  failOverview = false;

  private record<T>(call: string, value: T): Promise<T> {
    this.calls.push(call);
    return Promise.resolve(value);
  }

  me = async () => ({ name: this.role === 'Admin' ? 'local owner' : `token:${this.role}`, role: this.role });
  overview = async (): Promise<Overview> => {
    if (this.failOverview) {
      throw new ApiError(500, 'The store is unavailable.');
    }

    return {
      range: '24h',
      bucketMinutes: 60,
      total: 7,
      byState: { Queued: 1, Assigned: 0, Running: 1, Succeeded: 3, Failed: 1, Cancelled: 1, TimedOut: 0, Interrupted: 0 },
      buckets: [
        { start: '2026-10-12T06:00:00Z', succeeded: 2, failed: 1, cancelled: 0, interrupted: 0 },
        { start: '2026-10-12T07:00:00Z', succeeded: 1, failed: 0, cancelled: 1, interrupted: 0 },
      ],
      runners: { total: 1, byStatus: { Offline: 0, Available: 0, Busy: 1, Paused: 0, Error: 0, Disabled: 0 }, activeJobs: 1, capacity: 1 },
      recent: this.jobList,
      failures: this.jobList.filter((j) => j.state === 'Failed'),
    };
  };
  jobs = async () => ({ total: this.jobList.length, offset: 0, jobs: this.jobList });
  job = async (jobId: string): Promise<JobDetail> => ({
    job: this.jobList.find((j) => j.jobId === jobId)!,
    packageSha256: 'a'.repeat(64),
    entryPath: 'hello-world.json',
    priority: 0,
    correlationId: jobId,
    eventCount: 2,
    eventsDropped: 0,
    requiredActivities: ['Core.Log'],
    requiredPlugins: [],
    attempts: [{ number: 1, runnerId: 'rnr_1', runnerName: 'finance-1', assignedAt: now, startedAt: now }],
    arguments: this.role === 'Viewer' ? undefined : { customer: 'ACME' },
    dataHidden: this.role === 'Viewer',
  });
  jobEvents = async (): Promise<JobEvent[]> => [
    { attempt: 1, sequence: 1, event: { kind: 'execution.started', time: now } },
    { attempt: 1, sequence: 2, event: { kind: 'log', time: now, level: 'Information', message: 'AI.Agent: tool credit_limit succeeded' } },
  ];
  submitJob = (body: { processId: string; entryPath?: string; argumentText?: Readonly<Record<string, string>> }) => this.record(`submit ${body.processId} ${body.entryPath} ${JSON.stringify(body.argumentText ?? {})}`, job({ jobId: 'job_9', state: 'Queued' }));
  cancelJob = (jobId: string) => this.record(`cancel ${jobId}`, undefined);
  retryJob = (jobId: string) => this.record(`retry ${jobId}`, job({ jobId: 'job_10', state: 'Queued' }));
  processes = async () => [process];
  process = async (): Promise<ProcessDetail> => ({
    process: process.process,
    versions: [version(2), version(1)],
    schedules: [{ scheduleId: 'sch_1', processId: 'prc_1', entryPath: 'hello-world.json', name: 'weekdays', kind: 'Cron', cron: '0 9 * * MON-FRI', timeZone: 'Europe/Amsterdam', enabled: true, misfire: 'RunOnce', overlap: 'Skip', arguments: {}, createdAt: now, createdBy: 'local owner', nextFireAt: now }],
    jobs: { total: 1, items: [job()] },
    eligibleRunners: [{ runnerId: 'rnr_1', name: 'finance-1', status: 'Busy', reason: 'eligible' }],
  });
  publish = (project: string, entryPoints: readonly string[]) => this.record(`publish ${project} [${entryPoints.join(', ')}]`, { process: process.process, version: version(3) });
  deploy = (processId: string, v: number) => this.record(`deploy ${processId} v${v}`, process.process);
  undeploy = (processId: string) => this.record(`undeploy ${processId}`, process.process);
  setProcessEnabled = (processId: string, enabled: boolean) => this.record(`enabled ${processId} ${enabled}`, process.process);
  configureProcess = (processId: string) => this.record(`configure ${processId}`, process.process);
  createSchedule = async () => {
    throw new ApiError(400, 'unused');
  };
  setScheduleEnabled = async () => {
    throw new ApiError(400, 'unused');
  };
  deleteSchedule = (scheduleId: string) => this.record(`delete ${scheduleId}`, undefined);
  previewSchedule = async () => ({ valid: true, next: [now] });
  timeZones = async () => ({ local: 'UTC', zones: [{ id: 'UTC', display: 'UTC' }] });
  runners = async () => this.runnerList;
  runner = async () => ({ runner: runner(), plugins: [{ id: 'MyRPA.Ai.Agent', version: '0.1.0' }], activities: ['Core.Log'], pluginErrors: [], jobs: [job()] });
  createRunner = (name: string) => this.record(`create runner ${name}`, { runnerId: 'rnr_2', name, token: 'myrpa_runner_rnr_2.SECRET', serverUrl: 'https://orchestrator:5311/' });
  updateRunner = (runnerId: string, change: { paused?: boolean }) => this.record(`update ${runnerId} ${JSON.stringify(change)}`, undefined);
  revokeRunner = (runnerId: string) => this.record(`revoke ${runnerId}`, undefined);
  rotateRunnerToken = (runnerId: string) => this.record(`rotate ${runnerId}`, { token: 'myrpa_runner_rnr_1.NEW', serverUrl: 'https://orchestrator:5311/' });
  tokens = async () => [];
  createToken = async (name: string, role: Role) => {
    this.calls.push(`token ${name} ${role}`);
    if (name === 'taken') {
      throw new ApiError(409, 'A token named taken exists.');
    }

    return { tokenId: 'tok_1', token: 'myrpa_token_tok_1.SECRET' };
  };
  revokeToken = (tokenId: string) => this.record(`revoke token ${tokenId}`, undefined);
  policy = async () => ({ maxAttempts: 3, maxEventsPerJob: 20000, offlineAfterSeconds: 45 });
  setPolicy = (policy: { maxAttempts: number }) => this.record(`policy ${policy.maxAttempts}`, { maxAttempts: policy.maxAttempts, maxEventsPerJob: 20000, offlineAfterSeconds: 45 });
  audit = async () => [{ id: 1, time: now, actor: 'local owner', action: 'runner.create', target: 'rnr_1', details: 'finance-1' }];
  diagnostics = async () => ({
    server: { version: '0.1.0', startedAt: now, now, runnerListener: 'https://0.0.0.0:5311/', runnerProtocol: 1, heartbeatSeconds: 10, leaseSeconds: 45 },
    store: { provider: 'SQLite', location: 'C:\\data\\orchestration.db', sizeBytes: 40960, schemaVersion: 1, counts: { jobs: 7 } },
    plugins: [],
    pluginDiagnostics: [],
    compatibility: [{ processId: 'prc_1', name: 'Invoice check', version: 2, plugins: [], runners: [{ runnerId: 'rnr_1', name: 'finance-1', reason: 'eligible' }] }],
  });
}

async function renderStudio(orchestrator = new FakeOrchestrator(), api: FakeApi = new OrchestratedApi()) {
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { preferences: memoryPreferences(), validateDelayMs: undefined });
  render(<App studio={studio} orchestrator={orchestrator} />);
  await act(settle);
  return { studio, orchestrator };
}

const rail = () => within(screen.getByRole('navigation', { name: 'Main' }));
const show = async (name: string) => {
  await act(async () => fireEvent.click(rail().getByRole('button', { name })));
  await act(settle);
};
const click = async (element: HTMLElement) => {
  await act(async () => fireEvent.click(element));
  await act(settle);
};

afterEach(cleanup);

describe('Operations pages', () => {
  it('appear in the rail only when the Server runs the orchestrator', async () => {
    await renderStudio();
    expect(rail().getAllByRole('button').map((b) => b.textContent)).toEqual(['Home', 'Studio', 'Deployments', 'Monitoring', 'Robots', 'Admin']);
    // Home shows today's operations with links to the pages.
    const home = within(screen.getByRole('main', { name: 'Laconi Automation' }));
    expect(home.getByRole('list', { name: 'Last 24 hours' }).textContent).toContain('7 jobs in 24 h');
    expect(home.getByRole('button', { name: 'Robots' })).toBeTruthy();
    cleanup();

    await renderStudio(new FakeOrchestrator(), new FakeApi());
    expect(rail().getAllByRole('button').map((b) => b.textContent)).toEqual(['Home', 'Studio']);
    expect(screen.queryByRole('heading', { name: 'Operations' })).toBeNull();
  });

  it('show no editing toolbar', async () => {
    await renderStudio();
    await enterStudio();
    expect(screen.getByRole('toolbar', { name: 'File' })).toBeTruthy();
    for (const name of ['Deployments', 'Monitoring', 'Robots', 'Admin']) {
      await show(name);
      expect(screen.queryByRole('toolbar', { name: 'File' })).toBeNull();
      expect(screen.queryByRole('toolbar', { name: 'Run' })).toBeNull();
    }
  });

  it('Monitoring shows the Server figures, the trend, Runners and jobs, and a job with its events', async () => {
    const { orchestrator } = await renderStudio();
    await show('Monitoring');
    const page = within(screen.getByRole('main', { name: 'Monitoring' }));

    const figures = within(page.getByRole('list', { name: 'Job counts' }));
    expect(figures.getByText('All jobs').previousSibling?.textContent).toBe('7');
    expect(figures.getByText('Succeeded').previousSibling?.textContent).toBe('3');
    expect(figures.getByText('Busy slots').previousSibling?.textContent).toBe('1 / 1');
    expect(page.getByRole('img').getAttribute('aria-label')).toBe('Outcomes in the last 24h: 3 succeeded, 1 failed or timed out, 1 cancelled, 0 interrupted.');
    expect(within(page.getByRole('region', { name: 'Runner availability' })).getByText('finance-1')).toBeTruthy();
    expect(within(page.getByRole('region', { name: 'Recent failures' })).getByText(/MYRPA5001 This Runner lacks plugin X/)).toBeTruthy();

    await click(page.getByRole('button', { name: 'Show job job_1' }));
    const detail = within(page.getByRole('region', { name: 'Job job_1' }));
    expect(detail.getByText(/AI\.Agent: tool credit_limit succeeded/)).toBeTruthy();
    expect(detail.getByText(/"customer": "ACME"/)).toBeTruthy();
    await click(detail.getByRole('button', { name: 'Cancel job' }));
    expect(orchestrator.calls).toContain('cancel job_1');
    expect(detail.getByRole('status').textContent).toBe('Cancellation requested.');
  });

  it('Monitoring says when loading fails, and offers to try again', async () => {
    const orchestrator = new FakeOrchestrator();
    orchestrator.failOverview = true;
    await renderStudio(orchestrator);
    await show('Monitoring');

    expect(screen.getByText(/Could not load: The store is unavailable\./)).toBeTruthy();
    orchestrator.failOverview = false;
    await click(screen.getByRole('button', { name: 'Try again' }));
    expect(within(screen.getByRole('list', { name: 'Job counts' })).getByText('All jobs')).toBeTruthy();
  });

  it('a Viewer sees jobs without their data and cannot cancel, with the reason', async () => {
    const orchestrator = new FakeOrchestrator();
    orchestrator.role = 'Viewer';
    await renderStudio(orchestrator);
    await show('Monitoring');
    await click(screen.getByRole('button', { name: 'Show job job_1' }));

    const detail = within(screen.getByRole('region', { name: 'Job job_1' }));
    const cancel = detail.getByRole('button', { name: 'Cancel job' }) as HTMLButtonElement;
    expect(cancel.disabled).toBe(true);
    expect(cancel.title).toBe('Needs the Operator role');
    expect(detail.getByText('Arguments and outputs are shown to operators and above.')).toBeTruthy();
    expect(detail.queryByText(/ACME/)).toBeNull();
  });

  it('Deployments starts with the projects, then publishes one with its entry points, deploys, rolls back and submits a job', async () => {
    const { orchestrator } = await renderStudio();
    await show('Deployments');
    const projects = within(screen.getByRole('list', { name: 'Projects to deploy' }));
    const card = projects.getByRole('button', { name: 'Deployment of demo' });
    expect(document.getElementById(card.getAttribute('aria-describedby')!)!.textContent).toBe('Deployed v2 deployed · latest v2 · 1/1 schedules on · last job Running');
    await click(card);
    const page = within(screen.getByRole('main', { name: 'Deployments · demo' }));

    const publish = within(page.getByRole('region', { name: 'Publish the project' }));
    const publishButton = publish.getByRole('button', { name: 'Publish' }) as HTMLButtonElement;
    expect(publishButton.title).toBe('Choose at least one entry point');
    await click(publish.getByRole('checkbox', { name: 'hello-world.json' }));
    await click(publishButton);
    expect(orchestrator.calls).toContain('publish demo [hello-world.json]');
    expect(publish.getByRole('status').textContent).toBe('Published demo with entry point hello-world.json.');

    const detail = within(page.getByRole('region', { name: 'Deployment details' }));
    expect(detail.getByText('sha256 222222222222')).toBeTruthy();
    expect(detail.getByText(/plugins: MyRPA\.Ai\.Agent 0\.1\.0/)).toBeTruthy();
    await click(detail.getByRole('button', { name: 'Roll back to this' }));
    expect(orchestrator.calls).toContain('deploy prc_1 v1');

    const secret = detail.getByPlaceholderText('secret: comes from the Runner, never from a job') as HTMLInputElement;
    expect(secret.disabled).toBe(true);
    await act(async () => fireEvent.change(detail.getByLabelText(/customer/), { target: { value: 'ACME' } }));
    await click(detail.getByRole('button', { name: 'Submit job' }));
    expect(orchestrator.calls).toContain('submit prc_1 hello-world.json {"customer":"ACME"}');
    expect(detail.getByText(/entry points hello-world\.json, orders\/import\.json/)).toBeTruthy();

    // Another entry point of the deployed version has its own arguments.
    await act(async () => fireEvent.change(detail.getByLabelText('Entry point to run'), { target: { value: 'orders/import.json' } }));
    expect(detail.queryByLabelText(/customer/)).toBeNull();
    await act(async () => fireEvent.change(detail.getByLabelText(/batch/), { target: { value: '7' } }));
    await click(detail.getByRole('button', { name: 'Submit job' }));
    expect(orchestrator.calls).toContain('submit prc_1 orders/import.json {"batch":"7"}');

    // All projects goes back to the choice.
    await click(page.getByRole('button', { name: 'All projects' }));
    expect(screen.getByRole('list', { name: 'Projects to deploy' })).toBeTruthy();
  });

  it('an Operator cannot publish or deploy, and is told why', async () => {
    const orchestrator = new FakeOrchestrator();
    orchestrator.role = 'Operator';
    await renderStudio(orchestrator);
    await show('Deployments');
    await click(screen.getByRole('button', { name: 'Deployment of demo' }));
    const page = within(screen.getByRole('main', { name: 'Deployments · demo' }));
    await click(page.getByRole('checkbox', { name: 'hello-world.json' }));

    const publish = page.getByRole('button', { name: 'Publish' }) as HTMLButtonElement;
    expect(publish.disabled).toBe(true);
    expect(publish.title).toBe('Needs the Developer role');
    expect((page.getByRole('button', { name: 'Roll back to this' }) as HTMLButtonElement).disabled).toBe(true);
    expect((page.getByRole('button', { name: 'Submit job' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('Robots lists Runners with their status and capabilities, and says when there are none', async () => {
    const orchestrator = new FakeOrchestrator();
    await renderStudio(orchestrator);
    await show('Robots');
    const page = within(screen.getByRole('main', { name: 'Robots' }));
    expect(page.getByText('VM-FIN-01')).toBeTruthy();
    expect(page.getByText('2 plugins · 120 activities')).toBeTruthy();
    cleanup();

    orchestrator.runnerList = [];
    await renderStudio(orchestrator);
    await show('Robots');
    expect(screen.getByText(/No Runner is registered/)).toBeTruthy();
  });

  it('Administration creates a Runner and shows its token once, and reports a refused token honestly', async () => {
    const { orchestrator } = await renderStudio();
    await show('Admin');
    const page = within(screen.getByRole('main', { name: 'Administration' }));

    const runners = within(page.getByRole('region', { name: 'Runner administration' }));
    await act(async () => fireEvent.change(runners.getByLabelText('Name'), { target: { value: 'ops-2' } }));
    await click(runners.getByRole('button', { name: 'Create Runner' }));
    expect(orchestrator.calls).toContain('create runner ops-2');
    expect(runners.getByText('myrpa_runner_rnr_2.SECRET')).toBeTruthy();
    expect(runners.getByText(/"serverUrl": "https:\/\/orchestrator:5311\/"/)).toBeTruthy();

    await click(runners.getByRole('button', { name: 'Revoke…' }));
    await click(runners.getByRole('button', { name: 'Confirm revoke' }));
    expect(orchestrator.calls).toContain('revoke rnr_1');

    const tokens = within(page.getByRole('region', { name: 'Access tokens' }));
    await act(async () => fireEvent.change(tokens.getByLabelText('Name'), { target: { value: 'taken' } }));
    await click(tokens.getByRole('button', { name: 'Create token' }));
    expect(tokens.getByRole('alert').textContent).toBe('A token named taken exists.');

    const health = within(page.getByRole('region', { name: 'Server health and storage' }));
    expect(health.getByText(/SQLite schema 1/)).toBeTruthy();
    expect(within(page.getByRole('region', { name: 'Audit history' })).getByText(/runner\.create/)).toBeTruthy();
  });

  it('Administration needs the Admin role', async () => {
    const orchestrator = new FakeOrchestrator();
    orchestrator.role = 'Developer';
    await renderStudio(orchestrator);
    await show('Admin');

    expect(screen.getByRole('alert').textContent).toBe('Administration needs the Admin role; you are Developer.');
    expect(screen.queryByRole('region', { name: 'Runner administration' })).toBeNull();
  });
});
