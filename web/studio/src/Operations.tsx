// The operations pages of the Studio (Phase 10, ADR-0049): Monitoring, Deployments, Robots (Runners) and
// Administration. Every figure comes from the Server's orchestrator API; nothing is invented or simulated. Buttons are
// disabled with their reason when the caller's role lacks a right, but the Server enforces every role (ADR-0051).

import { createContext, useCallback, useContext, useEffect, useId, useState, type ReactNode } from 'react';
import { useStudio, useStudioState } from './context';
import { Icon, type IconName } from './icons';
import {
  hasRole,
  isActive,
  jobStates,
  roles,
  type JobDetail,
  type JobEvent,
  type JobState,
  type JobSummary,
  type OrchestratorApi,
  type Overview,
  type Policy,
  type ProcessDetail,
  type ProcessListItem,
  type PackageArgument,
  type ScheduleView,
  type Role,
  type RunnerView,
  type ScheduleBody,
  type PackageEntryPoint,
} from './orchestrator';

export const OrchestratorContext = createContext<OrchestratorApi | null>(null);

export function useOrchestrator(): OrchestratorApi {
  const api = useContext(OrchestratorContext);
  if (api === null) {
    throw new Error('No orchestrator API in context.');
  }

  return api;
}

interface Loaded<T> {
  readonly data?: T;
  readonly error?: string;
  readonly loading: boolean;
  readonly reload: () => void;
}

/** Loads data, reloads it every `refreshMs` (0: never) and on `reload()`; keeps the last data while reloading. */
export function useLoad<T>(load: () => Promise<T>, deps: readonly unknown[], refreshMs = 5000): Loaded<T> {
  const [state, setState] = useState<{ data?: T; error?: string; loading: boolean }>({ loading: true });
  const [tick, setTick] = useState(0);
  const reload = useCallback(() => setTick((t) => t + 1), []);
  useEffect(() => {
    let cancelled = false;
    setState((s) => ({ ...s, loading: true }));
    load().then(
      (data) => !cancelled && setState({ data, loading: false }),
      (error: unknown) => !cancelled && setState((s) => ({ data: s.data, error: (error as Error).message, loading: false })),
    );
    return () => {
      cancelled = true;
    };
    // `deps` names what the load depends on; `load` itself is a new closure on every render.
  }, [...deps, tick]);
  useEffect(() => {
    if (refreshMs <= 0) {
      return undefined;
    }

    const timer = window.setInterval(reload, refreshMs);
    return () => window.clearInterval(timer);
  }, [refreshMs, reload]);
  return { ...state, reload };
}

/** The caller's role (the Studio session is the local owner: Admin). */
function useRole(): Role | undefined {
  const api = useOrchestrator();
  return useLoad(() => api.me(), [], 0).data?.role;
}

/** Runs an action and reports its outcome honestly (the Server's message on failure). */
function useAction() {
  const [result, setResult] = useState<{ readonly ok: boolean; readonly text: string }>();
  const [busy, setBusy] = useState(false);
  const run = useCallback(async (success: string, action: () => Promise<unknown>, after?: () => void) => {
    setBusy(true);
    try {
      await action();
      setResult({ ok: true, text: success });
      after?.();
    } catch (error) {
      setResult({ ok: false, text: (error as Error).message });
    } finally {
      setBusy(false);
    }
  }, []);
  const outcome =
    result === undefined ? null : (
      <p className={result.ok ? 'ops-result ok' : 'ops-result error'} role={result.ok ? 'status' : 'alert'}>
        {result.text}
      </p>
    );
  return { run, busy, outcome };
}

const time = (iso?: string): string => (iso === undefined ? '—' : new Date(iso).toLocaleString());

const duration = (ms?: number): string => {
  if (ms === undefined) {
    return '—';
  }

  const seconds = ms / 1000;
  return seconds < 60 ? `${seconds.toFixed(1)} s` : `${Math.floor(seconds / 60)} min ${Math.round(seconds % 60)} s`;
};

const ago = (iso?: string): string => {
  if (iso === undefined) {
    return 'never';
  }

  const seconds = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  return seconds < 60 ? `${seconds} s ago` : seconds < 3600 ? `${Math.round(seconds / 60)} min ago` : `${Math.round(seconds / 3600)} h ago`;
};

const short = (hash: string): string => hash.slice(0, 12);

const lacking = (role: Role | undefined, needed: Role): string | undefined => (hasRole(role, needed) ? undefined : `Needs the ${needed} role`);

export function Badge({ value }: { value: string }) {
  return <span className={`badge status-${value.toLowerCase()}`}>{value}</span>;
}

function LoadState<T>({ loaded, empty, children }: { loaded: Loaded<T>; empty?: (data: T) => string | undefined; children: (data: T) => ReactNode }) {
  if (loaded.data === undefined) {
    return loaded.error !== undefined ? (
      <p className="ops-result error" role="alert">
        Could not load: {loaded.error}{' '}
        <button type="button" className="link" onClick={loaded.reload}>
          Try again
        </button>
      </p>
    ) : (
      <p className="hint" aria-live="polite">
        Loading…
      </p>
    );
  }

  const emptyText = empty?.(loaded.data);
  return (
    <>
      {loaded.error !== undefined && (
        <p className="ops-result error" role="alert">
          Refresh failed: {loaded.error} (showing the last data)
        </p>
      )}
      {emptyText !== undefined ? <p className="hint">{emptyText}</p> : children(loaded.data)}
    </>
  );
}

export function PageHeader({ id, icon, title, children }: { id: string; icon: IconName; title: string; children?: ReactNode }) {
  return (
    <div className="page-header">
      <h2 id={id} className="page-title">
        <Icon name={icon} size={22} /> {title}
      </h2>
      <div className="ops-actions">{children}</div>
    </div>
  );
}

// ───────────────────────────── Monitoring ─────────────────────────────

/** The trend of final outcomes per time bucket, as SVG bars (attributes only: CSP-safe). */
function TrendChart({ overview }: { overview: Overview }) {
  const max = Math.max(1, ...overview.buckets.map((b) => b.succeeded + b.failed + b.cancelled + b.interrupted));
  const width = 600;
  const height = 120;
  const step = width / Math.max(1, overview.buckets.length);
  const totals = overview.buckets.reduce((t, b) => ({ s: t.s + b.succeeded, f: t.f + b.failed, c: t.c + b.cancelled, i: t.i + b.interrupted }), { s: 0, f: 0, c: 0, i: 0 });
  const label = `Outcomes in the last ${overview.range}: ${totals.s} succeeded, ${totals.f} failed or timed out, ${totals.c} cancelled, ${totals.i} interrupted.`;
  return (
    <figure className="ops-chart">
      <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label={label} preserveAspectRatio="none">
        {overview.buckets.map((b, index) => {
          let y = height;
          const bars: [number, string][] = [
            [b.succeeded, 'bar-ok'],
            [b.failed, 'bar-failed'],
            [b.cancelled, 'bar-cancelled'],
            [b.interrupted, 'bar-interrupted'],
          ];
          return (
            <g key={b.start}>
              <title>{`${time(b.start)}: ${b.succeeded} succeeded, ${b.failed} failed, ${b.cancelled} cancelled, ${b.interrupted} interrupted`}</title>
              {bars.map(([count, className]) => {
                const h = (count / max) * (height - 4);
                y -= h;
                return count > 0 ? <rect key={className} className={className} x={index * step + 1} y={y} width={Math.max(1, step - 2)} height={h} /> : null;
              })}
            </g>
          );
        })}
      </svg>
      <figcaption>
        <span className="legend bar-ok">Succeeded {totals.s}</span> <span className="legend bar-failed">Failed {totals.f}</span> <span className="legend bar-cancelled">Cancelled {totals.c}</span>{' '}
        <span className="legend bar-interrupted">Interrupted {totals.i}</span>
      </figcaption>
    </figure>
  );
}

function JobsTable({ jobs, onSelect, selected, caption, byEntry = false }: { jobs: readonly JobSummary[]; onSelect: (jobId: string) => void; selected?: string; caption: string; byEntry?: boolean }) {
  return (
    <div className="ops-table-wrap">
      <table className="ops-table">
        <caption className="sr-only">{caption}</caption>
        <thead>
          <tr>
            <th scope="col">Job</th>
            <th scope="col">{byEntry ? 'Entry point' : 'Workflow'}</th>
            <th scope="col">State</th>
            <th scope="col">Runner</th>
            <th scope="col">Created</th>
            <th scope="col">Duration</th>
          </tr>
        </thead>
        <tbody>
          {jobs.map((j) => (
            <tr key={j.jobId} className={selected === j.jobId ? 'selected' : undefined}>
              <td>
                <button type="button" className="link" onClick={() => onSelect(j.jobId)} aria-label={`Show job ${j.jobId}`}>
                  {j.jobId}
                </button>
              </td>
              <td>
                {byEntry ? (j.entryPath ?? '—') : j.processName} <small>v{j.version}</small>
              </td>
              <td>
                <Badge value={j.state} />
                {j.cancelRequested && isActive(j.state) && <small> cancelling…</small>}
              </td>
              <td>{j.runnerName ?? j.runnerId ?? '—'}</td>
              <td>{time(j.createdAt)}</td>
              <td>{duration(j.durationMs)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function JobPanel({ jobId, role, onClose, onChanged }: { jobId: string; role?: Role; onClose: () => void; onChanged: () => void }) {
  const api = useOrchestrator();
  const detail = useLoad(() => api.job(jobId), [jobId], 3000);
  const events = useLoad(() => api.jobEvents(jobId), [jobId], 3000);
  const action = useAction();
  return (
    <section className="ops-card ops-detail" aria-label={`Job ${jobId}`}>
      <div className="page-header">
        <h3>Job {jobId}</h3>
        <button type="button" className="with-icon" onClick={onClose}>
          <Icon name="close" size={14} /> Close
        </button>
      </div>
      <LoadState loaded={detail}>
        {(d: JobDetail) => {
          const cancelReason = !isActive(d.job.state) ? 'The job is not active' : d.job.cancelRequested ? 'Cancellation was already requested' : lacking(role, 'Operator');
          const retryReason = isActive(d.job.state) ? 'The job has not finished' : lacking(role, 'Operator');
          return (
            <>
              <dl className="ops-facts">
                <dt>State</dt>
                <dd>
                  <Badge value={d.job.state} />
                </dd>
                <dt>Workflow</dt>
                <dd>
                  {d.job.processName} v{d.job.version} ({d.entryPath})
                </dd>
                <dt>Package</dt>
                <dd>
                  <code title={d.packageSha256}>sha256 {short(d.packageSha256)}</code>
                </dd>
                <dt>Runner</dt>
                <dd>{d.job.runnerName ?? '—'}</dd>
                <dt>Source</dt>
                <dd>
                  {d.job.source}
                  {d.scheduledFor && ` (for ${time(d.scheduledFor)})`} · by {d.job.submittedBy}
                </dd>
                <dt>Created / started / finished</dt>
                <dd>
                  {time(d.job.createdAt)} / {time(d.job.startedAt)} / {time(d.job.completedAt)}
                </dd>
                <dt>Duration</dt>
                <dd>{duration(d.job.durationMs)}</dd>
                <dt>Correlation</dt>
                <dd>
                  <code>{d.correlationId}</code>
                </dd>
              </dl>
              {d.job.error && (
                <p className="ops-result error" role="note">
                  <strong>{d.job.error.code}</strong> {d.job.error.errorType && `(${d.job.error.errorType})`} {d.job.error.message}
                  {d.job.error.nodeId && ` — node ${d.job.error.nodeId}`}
                </p>
              )}
              {d.waiting && (
                <div>
                  <h4>Why it waits</h4>
                  <ul className="ops-list">
                    {d.waiting.map((w) => (
                      <li key={w}>{w}</li>
                    ))}
                  </ul>
                </div>
              )}
              <div className="ops-actions">
                <button
                  type="button"
                  disabled={action.busy || cancelReason !== undefined}
                  title={cancelReason}
                  onClick={() =>
                    void action.run(
                      'Cancellation requested.',
                      () => api.cancelJob(jobId),
                      () => {
                        detail.reload();
                        onChanged();
                      },
                    )
                  }
                >
                  Cancel job
                </button>
                <button type="button" disabled={action.busy || retryReason !== undefined} title={retryReason ?? 'Submits a new job with the same version and arguments'} onClick={() => void action.run('Retry submitted as a new job.', () => api.retryJob(jobId), onChanged)}>
                  Retry
                </button>
              </div>
              {action.outcome}
              <h4>Attempts</h4>
              {d.attempts.length === 0 ? (
                <p className="hint">Not assigned to a Runner yet.</p>
              ) : (
                <ul className="ops-list">
                  {d.attempts.map((a) => (
                    <li key={a.number}>
                      #{a.number} on {a.runnerName ?? a.runnerId}: assigned {time(a.assignedAt)}
                      {a.startedAt && `, started ${time(a.startedAt)}`}
                      {a.outcome && ` → ${a.outcome}`}
                      {a.reason && ` (${a.reason})`}
                    </li>
                  ))}
                </ul>
              )}
              <h4>Data</h4>
              {d.dataHidden ? (
                <p className="hint">Arguments and outputs are shown to operators and above.</p>
              ) : (
                <div className="ops-data">
                  <div>
                    <small>Arguments</small>
                    <pre>{JSON.stringify(d.arguments ?? {}, null, 2)}</pre>
                  </div>
                  <div>
                    <small>Outputs</small>
                    <pre>{d.outputs === undefined ? '—' : JSON.stringify(d.outputs, null, 2)}</pre>
                  </div>
                </div>
              )}
            </>
          );
        }}
      </LoadState>
      <h4>Events</h4>
      <LoadState loaded={events} empty={(e) => (e.length === 0 ? 'No events yet.' : undefined)}>
        {(list: JobEvent[]) => (
          <ol className="ops-events" aria-label="Execution events">
            {list.map((e) => (
              <li key={`${e.attempt}-${e.sequence}`}>
                <span className="event-time">{new Date(e.event.time).toLocaleTimeString()}</span> <span className="event-kind">{e.event.kind}</span> {e.event.nodeId && <code>{e.event.nodeId}</code>}{' '}
                {e.event.activityType && <small>{e.event.activityType}</small>} {e.event.status && <Badge value={e.event.status} />} {e.event.message}
                {e.event.error && (
                  <span className="event-error">
                    {' '}
                    {e.event.error.code}: {e.event.error.message}
                  </span>
                )}
              </li>
            ))}
          </ol>
        )}
      </LoadState>
    </section>
  );
}

export function MonitoringPage() {
  const api = useOrchestrator();
  const role = useRole();
  const [range, setRange] = useState<'24h' | '7d' | '30d'>('24h');
  const [state, setState] = useState<JobState | ''>('');
  const [processId, setProcessId] = useState('');
  const [offset, setOffset] = useState(0);
  const [selected, setSelected] = useState<string>();
  const overview = useLoad(() => api.overview(range), [range]);
  const runners = useLoad(() => api.runners(), []);
  const processes = useLoad(() => api.processes(), [], 30000);
  const jobs = useLoad(() => api.jobs({ state: state === '' ? undefined : [state], processId: processId || undefined, offset, limit: 25 }), [state, processId, offset]);
  const refresh = () => {
    overview.reload();
    runners.reload();
    jobs.reload();
  };
  return (
    <main className="home ops-page" aria-labelledby="monitoring-heading">
      <PageHeader id="monitoring-heading" icon="monitor" title="Monitoring">
        <label>
          Range{' '}
          <select value={range} onChange={(e) => setRange(e.target.value as '24h' | '7d' | '30d')}>
            <option value="24h">Last 24 hours</option>
            <option value="7d">Last 7 days</option>
            <option value="30d">Last 30 days</option>
          </select>
        </label>
        <button type="button" onClick={refresh}>
          Refresh
        </button>
      </PageHeader>
      <LoadState loaded={overview}>
        {(o: Overview) => (
          <>
            <ul className="ops-figures" aria-label="Job counts">
              {[
                ['All jobs', o.total],
                ['Running', o.byState.Running + o.byState.Assigned],
                ['Queued', o.byState.Queued],
                ['Succeeded', o.byState.Succeeded],
                ['Failed', o.byState.Failed + o.byState.TimedOut],
                ['Cancelled', o.byState.Cancelled],
                ['Interrupted', o.byState.Interrupted],
                ['Runners online', `${o.runners.total - o.runners.byStatus.Offline - o.runners.byStatus.Disabled} / ${o.runners.total}`],
                ['Busy slots', `${o.runners.activeJobs} / ${o.runners.capacity}`],
              ].map(([name, value]) => (
                <li key={name} className="ops-figure">
                  <strong>{value}</strong>
                  <span>{name}</span>
                </li>
              ))}
            </ul>
            <section className="ops-card" aria-label="Outcome trend">
              <h3>Outcomes</h3>
              {o.buckets.every((b) => b.succeeded + b.failed + b.cancelled + b.interrupted === 0) ? <p className="hint">No job finished in this range.</p> : <TrendChart overview={o} />}
            </section>
            {o.failures.length > 0 && (
              <section className="ops-card" aria-label="Recent failures">
                <h3>Recent failures</h3>
                <ul className="ops-list">
                  {o.failures.map((j) => (
                    <li key={j.jobId}>
                      <button type="button" className="link" onClick={() => setSelected(j.jobId)}>
                        {j.processName} v{j.version}
                      </button>{' '}
                      <Badge value={j.state} /> {time(j.completedAt)} — {j.error?.code} {j.error?.message}
                    </li>
                  ))}
                </ul>
              </section>
            )}
          </>
        )}
      </LoadState>
      <section className="ops-card" aria-label="Runner availability">
        <h3>Runners</h3>
        <LoadState loaded={runners} empty={(r) => (r.length === 0 ? 'No Runner is registered. Create one in Administration.' : undefined)}>
          {(list: RunnerView[]) => (
            <ul className="ops-list">
              {list.map((r) => (
                <li key={r.runnerId}>
                  <strong>{r.name}</strong> <Badge value={r.status} /> {r.activeJobs}/{r.capacity} jobs · last heartbeat {ago(r.lastSeenAt)}
                </li>
              ))}
            </ul>
          )}
        </LoadState>
      </section>
      <section className="ops-card" aria-label="Executions">
        <div className="page-header">
          <h3>Executions</h3>
          <div className="ops-actions">
            <label>
              State{' '}
              <select
                value={state}
                onChange={(e) => {
                  setState(e.target.value as JobState | '');
                  setOffset(0);
                }}
              >
                <option value="">All</option>
                {jobStates.map((s) => (
                  <option key={s} value={s}>
                    {s}
                  </option>
                ))}
              </select>
            </label>
            <label>
              Workflow{' '}
              <select
                value={processId}
                onChange={(e) => {
                  setProcessId(e.target.value);
                  setOffset(0);
                }}
              >
                <option value="">All</option>
                {(processes.data ?? []).map((p) => (
                  <option key={p.process.processId} value={p.process.processId}>
                    {p.process.name}
                  </option>
                ))}
              </select>
            </label>
          </div>
        </div>
        <LoadState loaded={jobs} empty={(j) => (j.total === 0 ? 'No jobs match.' : undefined)}>
          {(page) => (
            <>
              <JobsTable jobs={page.jobs} onSelect={setSelected} selected={selected} caption="Jobs, newest first" />
              <div className="ops-pager">
                <button type="button" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - 25))}>
                  Previous
                </button>
                <span>
                  {offset + 1}–{offset + page.jobs.length} of {page.total}
                </span>
                <button type="button" disabled={offset + page.jobs.length >= page.total} onClick={() => setOffset(offset + 25)}>
                  Next
                </button>
              </div>
            </>
          )}
        </LoadState>
      </section>
      {selected !== undefined && <JobPanel key={selected} jobId={selected} role={role} onClose={() => setSelected(undefined)} onChanged={refresh} />}
    </main>
  );
}

// ───────────────────────────── Deployments ─────────────────────────────

function PublishForm({ project, role, onPublished }: { project: string; role?: Role; onPublished: () => void }) {
  const api = useOrchestrator();
  const studio = useStudio();
  const files = useLoad(() => studio.projectWorkflows(project), [project], 0);
  const open = useStudioState((s) => (s.file?.project === project ? s.file.path : undefined));
  const dirty = useStudioState((s) => s.file?.project === project && s.document !== s.saved);
  const [entryPoints, setEntryPoints] = useState<readonly string[]>(open === undefined ? [] : [open]);
  const [notes, setNotes] = useState('');
  const action = useAction();
  const id = useId();
  const paths = files.data ?? [];
  const chosen = paths.filter((p) => entryPoints.includes(p));
  const reason = chosen.length === 0 ? 'Choose at least one entry point' : lacking(role, 'Developer');
  const toggle = (path: string, on: boolean) => setEntryPoints((current) => (on ? [...current.filter((p) => p !== path), path] : current.filter((p) => p !== path)));
  return (
    <section className="ops-card" aria-label="Publish the project">
      <h3>Publish {project}</h3>
      <p className="hint">
        Publishing stores an immutable, numbered version of the project: its saved workflow files, with the entry points you choose as the workflows a job can start. A single workflow is a project with one entry point. It does not run anything: deploy a version to use it for jobs and schedules.
        {dirty && open !== undefined && ` ${open} has unsaved changes in Studio: only saved files are published.`}
      </p>
      <div className="ops-form">
        <fieldset className="ops-wide ops-checks">
          <legend>Entry points of {project}</legend>
          {files.data === undefined && <p className="hint">{files.error !== undefined ? `Could not list the workflows: ${files.error}` : 'Loading…'}</p>}
          {files.data?.length === 0 && <p className="hint">The project has no workflow yet. Build one in Studio.</p>}
          {paths.map((path) => (
            <label key={path}>
              <input type="checkbox" checked={entryPoints.includes(path)} onChange={(e) => toggle(path, e.target.checked)} /> {path}
            </label>
          ))}
        </fieldset>
        <label htmlFor={`${id}-notes`}>Release notes</label>
        <input id={`${id}-notes`} value={notes} maxLength={2000} onChange={(e) => setNotes(e.target.value)} />
        <button
          type="button"
          className="primary"
          disabled={action.busy || reason !== undefined}
          title={reason}
          onClick={() =>
            void action.run(
              `Published ${project} with ${chosen.length === 1 ? `entry point ${chosen[0]}` : `${chosen.length} entry points`}.`,
              () => api.publish(project, chosen, notes || undefined),
              () => {
                setNotes('');
                onPublished();
              },
            )
          }
        >
          Publish
        </button>
      </div>
      {action.outcome}
    </section>
  );
}

function ScheduleForm({ processId, entryPoints, role, onCreated }: { processId: string; entryPoints: readonly PackageEntryPoint[]; role?: Role; onCreated: () => void }) {
  const api = useOrchestrator();
  const zones = useLoad(() => api.timeZones(), [], 0);
  const [body, setBody] = useState<ScheduleBody>({ name: '', kind: 'Cron', cron: '0 9 * * MON-FRI', timeZone: '', misfire: 'RunOnce', overlap: 'Skip' });
  const [argumentText, setArgumentText] = useState<Record<string, string>>({});
  const timeZone = body.timeZone || zones.data?.local || 'UTC';
  const entryPath = body.entryPath ?? entryPoints[0]?.path;
  const effective: ScheduleBody = { ...body, timeZone, entryPath };
  const preview = useLoad(() => api.previewSchedule(effective), [JSON.stringify(effective)], 0);
  const action = useAction();
  const id = useId();
  const reason = body.name.trim() === '' ? 'Name the schedule' : entryPath === undefined ? 'Deploy a version first' : preview.data?.valid === false ? preview.data.error : lacking(role, 'Developer');
  const set = (change: Partial<ScheduleBody>) => setBody((b) => ({ ...b, ...change }));
  const inputs = (entryPoints.find((e) => e.path === entryPath)?.arguments ?? []).filter((a) => a.direction !== 'Out');
  return (
    <div className="ops-form" aria-label="New schedule">
      <label htmlFor={`${id}-name`}>Name</label>
      <input id={`${id}-name`} value={body.name} maxLength={80} onChange={(e) => set({ name: e.target.value })} />
      {entryPoints.length > 1 && (
        <>
          <label htmlFor={`${id}-entry`}>Entry point</label>
          <select
            id={`${id}-entry`}
            value={entryPath}
            onChange={(e) => {
              set({ entryPath: e.target.value });
              setArgumentText({});
            }}
          >
            {entryPoints.map((e) => (
              <option key={e.path} value={e.path}>
                {e.path}
              </option>
            ))}
          </select>
        </>
      )}
      <label htmlFor={`${id}-kind`}>Kind</label>
      <select id={`${id}-kind`} value={body.kind} onChange={(e) => set({ kind: e.target.value as ScheduleBody['kind'] })}>
        <option value="Cron">Cron</option>
        <option value="Interval">Interval</option>
        <option value="Once">Once</option>
      </select>
      {body.kind === 'Cron' && (
        <>
          <label htmlFor={`${id}-cron`}>Cron (minute hour day month weekday)</label>
          <input id={`${id}-cron`} value={body.cron ?? ''} onChange={(e) => set({ cron: e.target.value })} />
        </>
      )}
      {body.kind === 'Interval' && (
        <>
          <label htmlFor={`${id}-interval`}>Every (minutes)</label>
          <input id={`${id}-interval`} type="number" min={1} value={body.intervalMinutes ?? 60} onChange={(e) => set({ intervalMinutes: Number(e.target.value) })} />
        </>
      )}
      {body.kind !== 'Cron' && (
        <>
          <label htmlFor={`${id}-start`}>{body.kind === 'Once' ? 'At' : 'Starting'} (your local time)</label>
          <input id={`${id}-start`} type="datetime-local" onChange={(e) => set({ startAt: e.target.value === '' ? undefined : new Date(e.target.value).toISOString() })} />
        </>
      )}
      <label htmlFor={`${id}-zone`}>Time zone</label>
      <select id={`${id}-zone`} value={timeZone} onChange={(e) => set({ timeZone: e.target.value })}>
        {(zones.data?.zones ?? [{ id: timeZone, display: timeZone }]).map((z) => (
          <option key={z.id} value={z.id}>
            {z.id}
          </option>
        ))}
      </select>
      <label htmlFor={`${id}-misfire`}>Missed times</label>
      <select id={`${id}-misfire`} value={body.misfire} onChange={(e) => set({ misfire: e.target.value as 'RunOnce' | 'Skip' })}>
        <option value="RunOnce">Run once</option>
        <option value="Skip">Skip</option>
      </select>
      <label htmlFor={`${id}-overlap`}>While its job runs</label>
      <select id={`${id}-overlap`} value={body.overlap} onChange={(e) => set({ overlap: e.target.value as 'Skip' | 'Allow' })}>
        <option value="Skip">Skip</option>
        <option value="Allow">Start another</option>
      </select>
      {inputs.length > 0 && (
        <fieldset className="ops-wide schedule-arguments">
          <legend>Arguments for each job (empty: the default)</legend>
          {inputs.map((a) => (
            <RunArgument key={`${entryPath}:${a.name}`} argument={a} value={argumentText[a.name] ?? ''} onChange={(value) => setArgumentText({ ...argumentText, [a.name]: value })} />
          ))}
        </fieldset>
      )}
      <p className="hint ops-wide" aria-live="polite">
        {preview.data === undefined ? 'Checking…' : preview.data.valid ? `Next: ${(preview.data.next ?? []).map((n) => time(n)).join(', ') || 'none'}` : preview.data.error}
      </p>
      <button type="button" className="primary" disabled={action.busy || reason !== undefined} title={reason} onClick={() => void action.run('Schedule created.', () => api.createSchedule(processId, { ...effective, argumentText: Object.fromEntries(Object.entries(argumentText).filter(([, v]) => v !== '')) }), onCreated)}>
        Create schedule
      </button>
      {action.outcome}
    </div>
  );
}

/** One input of Run now: a choice for a Boolean, a text box otherwise; the default shows what an empty field means. */
function RunArgument({ argument, value, onChange }: { argument: PackageArgument; value: string; onChange: (value: string) => void }) {
  const id = useId();
  const fallback = argument.default === undefined || argument.default === null ? undefined : argument.default.replace(/^"(.*)"$/, '$1');
  return (
    <div className="run-argument">
      <label htmlFor={id}>
        {argument.name} <span className="type-chip">{argument.type}</span>
        {argument.required && <span className="required-tag">required</span>}
      </label>
      {argument.secret ? (
        <input id={id} disabled placeholder="Secret: the Runner supplies it, never a job" />
      ) : argument.type === 'Boolean' ? (
        <select id={id} value={value} onChange={(e) => onChange(e.target.value)}>
          <option value="">{fallback === undefined ? 'Not set' : `Default (${fallback})`}</option>
          <option value="true">true</option>
          <option value="false">false</option>
        </select>
      ) : (
        <input id={id} value={value} placeholder={fallback !== undefined ? `Default: ${fallback}` : argument.required ? 'Required' : 'Optional'} onChange={(e) => onChange(e.target.value)} />
      )}
    </div>
  );
}

/** A schedule's job arguments as "name = value, …" (empty when there are none). */
const argumentsText = (values: unknown): string =>
  values !== null && typeof values === 'object'
    ? Object.entries(values as Record<string, unknown>)
        .map(([name, value]) => `${name} = ${typeof value === 'string' ? value : JSON.stringify(value)}`)
        .join(', ')
    : '';

const scheduleText = (s: ScheduleView): string =>
  `${s.kind === 'Cron' ? `cron ${s.cron}` : s.kind === 'Interval' ? `every ${s.intervalMinutes} min` : `once at ${time(s.startAt)}`} (${s.timeZone})`;

function ProcessPanel({ processId, role, onChanged }: { processId: string; role?: Role; onChanged: () => void }) {
  const api = useOrchestrator();
  const detail = useLoad(() => api.process(processId), [processId]);
  const action = useAction();
  const [argumentText, setArgumentText] = useState<Record<string, string>>({});
  const [entryChoice, setEntryChoice] = useState<string>();
  const [selectedJob, setSelectedJob] = useState<string>();
  const changed = () => {
    detail.reload();
    onChanged();
  };
  const developer = lacking(role, 'Developer');
  return (
    <section className="ops-detail-page" aria-label="Deployment details">
      <LoadState loaded={detail}>
        {(d: ProcessDetail) => {
          const deployed = d.versions.find((v) => v.version === d.process.deployedVersion);
          const entryPoints = deployed?.entryPoints ?? [];
          const entry = entryPoints.find((e) => e.path === entryChoice) ?? entryPoints[0];
          const inputs = (entry?.arguments ?? []).filter((a) => a.direction !== 'Out');
          const runReason = !d.process.enabled ? 'The deployment is disabled' : deployed === undefined ? 'No version is deployed' : lacking(role, 'Operator');
          const runners = d.eligibleRunners ?? [];
          const ready = runners.filter((r) => r.reason === 'eligible');
          const revoked = runners.filter((r) => r.status === 'Disabled');
          const lastJob = d.jobs.items[0];
          const nextSchedule = d.schedules.find((x) => x.enabled && x.nextFireAt);
          return (
            <>
              <div className="ops-card deployment-head">
                <div className="page-header">
                  <h3>
                    Deployment <Badge value={d.process.status} />
                  </h3>
                  <div className="ops-actions">
                    <button type="button" disabled={action.busy || developer !== undefined} title={developer} onClick={() => void action.run(d.process.enabled ? 'Deployment disabled.' : 'Deployment enabled.', () => api.setProcessEnabled(processId, !d.process.enabled), changed)}>
                      {d.process.enabled ? 'Disable' : 'Enable'}
                    </button>
                    {d.process.deployedVersion !== undefined && (
                      <button type="button" disabled={action.busy || developer !== undefined} title={developer} onClick={() => void action.run('Undeployed: jobs now need an explicit version.', () => api.undeploy(processId), changed)}>
                        Undeploy
                      </button>
                    )}
                  </div>
                </div>
                <dl className="deployment-facts" aria-label="Deployment summary">
                  <div>
                    <dt>Deployed version</dt>
                    <dd>{deployed ? `v${deployed.version}` : 'None'}
                      <small>{deployed ? `published ${time(deployed.publishedAt)}` : 'Deploy a version below'}</small>
                    </dd>
                  </div>
                  <div>
                    <dt>Deployed by</dt>
                    <dd>{d.process.deployedBy ?? '—'}
                      <small>{d.process.deployedAt ? time(d.process.deployedAt) : ''}</small>
                    </dd>
                  </div>
                  <div>
                    <dt>Runs on</dt>
                    <dd>{d.process.targetRunnerId ? `Runner ${d.process.targetRunnerId}` : d.process.targetGroup ? `Group ${d.process.targetGroup}` : 'Any Runner'}
                      <small>
                      {ready.length} of {runners.length - revoked.length} Runner{runners.length - revoked.length === 1 ? '' : 's'} ready
                    </small>
                    </dd>
                  </div>
                  <div>
                    <dt>Schedules</dt>
                    <dd>{d.schedules.length === 0 ? 'None' : `${d.schedules.filter((x) => x.enabled).length} of ${d.schedules.length} on`}
                      <small>{nextSchedule ? `next ${time(nextSchedule.nextFireAt)}` : ''}</small>
                    </dd>
                  </div>
                  <div>
                    <dt>Last run</dt>
                    <dd>{lastJob ? <Badge value={lastJob.state} /> : 'Never'}
                      <small>{lastJob ? ago(lastJob.createdAt) : ''}</small>
                    </dd>
                  </div>
                </dl>
                {action.outcome}
              </div>

              <div className="deployment-columns">
                <section className="ops-card" aria-labelledby={`${processId}-run`}>
                  <h4 id={`${processId}-run`}>Run now</h4>
                  <p className="hint">{deployed ? `Starts a job for v${deployed.version} on a ready Runner. Leave a field empty to use its default.` : 'Deploy a version to run it.'}</p>
                  <div className="run-form">
                    {entryPoints.length > 1 && (
                      <div className="run-argument">
                        <label htmlFor={`${processId}-entry`}>Entry point to run</label>
                        <select
                          id={`${processId}-entry`}
                          value={entry?.path}
                          onChange={(e) => {
                            setEntryChoice(e.target.value);
                            setArgumentText({});
                          }}
                        >
                          {entryPoints.map((e) => (
                            <option key={e.path} value={e.path}>
                              {e.path}
                            </option>
                          ))}
                        </select>
                      </div>
                    )}
                    {entry !== undefined && inputs.length === 0 && <p className="hint">{entry.path} takes no input.</p>}
                    {inputs.map((a) => (
                      <RunArgument key={`${entry?.path}:${a.name}`} argument={a} value={argumentText[a.name] ?? ''} onChange={(value) => setArgumentText({ ...argumentText, [a.name]: value })} />
                    ))}
                  </div>
                  <button
                    type="button"
                    className="primary"
                    disabled={action.busy || runReason !== undefined}
                    title={runReason}
                    onClick={() => void action.run(`Job submitted for v${deployed?.version} (${entry?.path}).`, () => api.submitJob({ processId, entryPath: entry?.path, argumentText: Object.fromEntries(Object.entries(argumentText).filter(([, v]) => v !== '')) }), changed)}
                  >
                    Run now
                  </button>
                </section>

                <section className="ops-card" aria-labelledby={`${processId}-version`}>
                  <h4 id={`${processId}-version`}>Deployed version</h4>
                  {deployed === undefined ? (
                    <p className="hint">Nothing is deployed. Choose a version below and Deploy it.</p>
                  ) : (
                    <dl className="ops-facts">
                      <dt>Version</dt>
                      <dd>
                        v{deployed.version} <code title={deployed.sha256}>sha256 {short(deployed.sha256)}</code>
                      </dd>
                      <dt>{deployed.entryPoints.length === 1 ? 'Entry point' : 'Entry points'}</dt>
                      <dd>{deployed.entryPoints.map((e) => e.path).join(', ')}</dd>
                      <dt>Files</dt>
                      <dd>{deployed.files.length} workflow file(s)</dd>
                      <dt>Needs plugins</dt>
                      <dd>
                        {deployed.plugins.length === 0
                          ? 'None (built-in activities only)'
                          : deployed.plugins.map((p) => (
                              <span key={p.id} className="plugin-chip">
                                {p.id} {p.version}
                              </span>
                            ))}
                      </dd>
                      {deployed.notes && (
                        <>
                          <dt>Notes</dt>
                          <dd>{deployed.notes}</dd>
                        </>
                      )}
                    </dl>
                  )}
                </section>

                <section className="ops-card" aria-labelledby={`${processId}-runners`}>
                  <h4 id={`${processId}-runners`}>Runners</h4>
                  {runners.length === revoked.length ? (
                    <p className="hint">No Runner can take jobs. Create one in Admin and start it.</p>
                  ) : (
                    <ul className="ops-list runner-list">
                      {runners
                        .filter((r) => r.status !== 'Disabled')
                        .map((r) => (
                          <li key={r.runnerId}>
                            <span>{r.name}</span> <Badge value={r.status} /> <small>{r.reason === 'eligible' ? 'can run this version' : r.reason}</small>
                          </li>
                        ))}
                    </ul>
                  )}
                  {revoked.length > 0 && (
                    <p className="hint">
                      {revoked.length} revoked Runner{revoked.length === 1 ? '' : 's'} not shown.
                    </p>
                  )}
                </section>

                <section className="ops-card" aria-labelledby={`${processId}-schedules`}>
                  <h4 id={`${processId}-schedules`}>Schedules</h4>
                  {d.schedules.length === 0 ? (
                    <p className="hint">No schedule: jobs start only when someone runs them.</p>
                  ) : (
                    <ul className="ops-list schedule-list">
                      {d.schedules.map((s) => (
                        <li key={s.scheduleId}>
                          <div>
                            <strong>{s.name}</strong> <Badge value={s.enabled ? 'Enabled' : 'Disabled'} />
                            <div className="hint">
                              {s.entryPath} · {scheduleText(s)}
                              {s.enabled && ` · next ${time(s.nextFireAt)}`}
                            </div>
                            {argumentsText(s.arguments) && <div className="hint">with {argumentsText(s.arguments)}</div>}
                            {s.lastOutcome && <div className="hint">Last: {s.lastOutcome}</div>}
                          </div>
                          <div className="ops-actions">
                            <button type="button" className="small" disabled={action.busy || lacking(role, 'Operator') !== undefined} title={lacking(role, 'Operator')} onClick={() => void action.run(s.enabled ? 'Schedule disabled.' : 'Schedule enabled.', () => api.setScheduleEnabled(s.scheduleId, !s.enabled), changed)}>
                              {s.enabled ? 'Disable' : 'Enable'}
                            </button>
                            <button type="button" className="small" disabled={action.busy || developer !== undefined} title={developer} aria-label={`Delete schedule ${s.name}`} onClick={() => void action.run('Schedule deleted.', () => api.deleteSchedule(s.scheduleId), changed)}>
                              Delete
                            </button>
                          </div>
                        </li>
                      ))}
                    </ul>
                  )}
                  <details>
                    <summary>New schedule</summary>
                    <ScheduleForm processId={processId} entryPoints={entryPoints} role={role} onCreated={changed} />
                  </details>
                </section>
              </div>

              <section className="ops-card" aria-labelledby={`${processId}-executions`}>
                <h4 id={`${processId}-executions`}>Recent executions ({d.jobs.total})</h4>
                {d.jobs.items.length === 0 ? <p className="hint">No job yet. Use Run now above.</p> : <JobsTable jobs={d.jobs.items} onSelect={setSelectedJob} selected={selectedJob} caption="Recent jobs of this deployment" byEntry />}
                {selectedJob !== undefined && <JobPanel key={selectedJob} jobId={selectedJob} role={role} onClose={() => setSelectedJob(undefined)} onChanged={changed} />}
              </section>

              <section className="ops-card" aria-labelledby={`${processId}-versions`}>
                <h4 id={`${processId}-versions`}>Versions</h4>
                <div className="ops-table-wrap">
                  <table className="ops-table">
                    <caption className="sr-only">Published versions, newest first</caption>
                    <thead>
                      <tr>
                        <th scope="col">Version</th>
                        <th scope="col">Published</th>
                        <th scope="col">Entry points</th>
                        <th scope="col">Plugins</th>
                        <th scope="col">Integrity</th>
                        <th scope="col">Action</th>
                      </tr>
                    </thead>
                    <tbody>
                      {d.versions.map((v) => {
                        const isDeployed = v.version === d.process.deployedVersion;
                        const rollback = d.process.deployedVersion !== undefined && v.version < d.process.deployedVersion;
                        return (
                          <tr key={v.version} className={isDeployed ? 'current' : undefined}>
                            <td>
                              v{v.version} {isDeployed && <Badge value="Deployed" />}
                            </td>
                            <td>
                              {time(v.publishedAt)}
                              <div className="hint">by {v.publishedBy}</div>
                              {v.notes && <div className="hint">{v.notes}</div>}
                            </td>
                            <td>
                              {v.entryPoints.map((e) => e.path).join(', ')}
                              <div className="hint">{v.files.length} file(s)</div>
                              {v.excluded.length > 0 && <div className="hint">left out: {v.excluded.join('; ')}</div>}
                            </td>
                            <td>{v.plugins.length === 0 ? '—' : v.plugins.map((p) => `${p.id} ${p.version}`).join(', ')}</td>
                            <td>
                              <code title={v.sha256}>sha256 {short(v.sha256)}</code>
                            </td>
                            <td>
                              {!isDeployed && (
                                <button type="button" disabled={action.busy || developer !== undefined} title={developer} onClick={() => void action.run(rollback ? `Rolled back to v${v.version}.` : `Deployed v${v.version}.`, () => api.deploy(processId, v.version), changed)}>
                                  {rollback ? 'Roll back to this' : 'Deploy'}
                                </button>
                              )}
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              </section>
            </>
          );
        }}
      </LoadState>
    </section>
  );
}

/**
 * Deployments (ADR-0049, ADR-0044 amendment): starts with choosing a project (a process is a project); then that
 * project's publishing, versions, Run now, schedules and executions.
 */
export function DeploymentsPage() {
  const api = useOrchestrator();
  const role = useRole();
  const projects = useStudioState((s) => s.projects);
  const processes = useLoad(() => api.processes(), []);
  const [chosen, setChosen] = useState<string>();
  const byProject = new Map((processes.data ?? []).map((p) => [p.process.project, p]));
  const others = (processes.data ?? []).filter((p) => !projects.includes(p.process.project));
  const item = chosen === undefined ? undefined : byProject.get(chosen);
  return (
    <main className="home ops-page" aria-labelledby="deployments-heading">
      <PageHeader id="deployments-heading" icon="deploy" title={chosen === undefined ? 'Deployments' : `Deployments · ${chosen}`}>
        {chosen !== undefined && (
          <button type="button" onClick={() => setChosen(undefined)}>
            All projects
          </button>
        )}
        <button type="button" onClick={processes.reload}>
          Refresh
        </button>
      </PageHeader>
      {chosen === undefined ? (
        <>
          <p className="hint">Choose a project to publish it, deploy a version, run it now or schedule it.</p>
          <LoadState loaded={processes}>
            {() =>
              projects.length === 0 && others.length === 0 ? (
                <p className="hint">There are no projects yet. Create one in Studio.</p>
              ) : (
                <section className="ops-card" aria-label="Projects to deploy">
                  <div className="ops-table-wrap">
                    <table className="ops-table project-table">
                      <caption className="sr-only">Projects and their deployments</caption>
                      <thead>
                        <tr>
                          <th scope="col">Project</th>
                          <th scope="col">Status</th>
                          <th scope="col">Deployed</th>
                          <th scope="col">Latest version</th>
                          <th scope="col">Schedules</th>
                          <th scope="col">Last job</th>
                        </tr>
                      </thead>
                      <tbody>
                        {[...projects.map((name) => ({ name, item: byProject.get(name), here: true })), ...others.map((p) => ({ name: p.process.project, item: p as ProcessListItem | undefined, here: false }))].map(({ name, item, here }) => (
                          <tr key={`${here}:${name}`}>
                            <th scope="row">
                              <button type="button" className="link project-link" aria-label={`Deployment of ${name}`} onClick={() => setChosen(name)}>
                                <Icon name="project" size={16} /> {name}
                              </button>
                              {!here && <div className="hint">not on this Server</div>}
                            </th>
                            <td>{item ? <Badge value={item.process.status} /> : <span className="hint">Not published</span>}</td>
                            <td>{item?.process.deployedVersion !== undefined ? `v${item.process.deployedVersion}` : '—'}</td>
                            <td>
                              {item ? `v${item.process.latestVersion}` : '—'}
                              {item?.latest && (
                                <div className="hint">
                                  {ago(item.latest.publishedAt)} by {item.latest.publishedBy}
                                </div>
                              )}
                            </td>
                            <td>{item && item.schedules > 0 ? `${item.enabledSchedules} of ${item.schedules} on` : '—'}</td>
                            <td>
                              {item?.lastJob ? (
                                <>
                                  <Badge value={item.lastJob.state} /> <span className="hint">{ago(item.lastJob.createdAt)}</span>
                                </>
                              ) : (
                                '—'
                              )}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </section>
              )
            }
          </LoadState>
        </>
      ) : (
        <>
          {projects.includes(chosen) ? (
            <PublishForm key={chosen} project={chosen} role={role} onPublished={processes.reload} />
          ) : (
            <p className="hint">This project is not on this Server: its published versions can be deployed and run, but not republished from here.</p>
          )}
          {item !== undefined ? (
            <ProcessPanel key={item.process.processId} processId={item.process.processId} role={role} onChanged={processes.reload} />
          ) : (
            processes.data !== undefined && <p className="hint">{chosen} is not published yet: publish a version above, then deploy it.</p>
          )}
        </>
      )}
    </main>
  );
}

// ───────────────────────────── Robots ─────────────────────────────

export function RobotsPage() {
  const api = useOrchestrator();
  const runners = useLoad(() => api.runners(), []);
  const [selected, setSelected] = useState<string>();
  const detail = useLoad(() => (selected === undefined ? Promise.resolve(undefined) : api.runner(selected)), [selected]);
  return (
    <main className="home ops-page" aria-labelledby="robots-heading">
      <PageHeader id="robots-heading" icon="robot" title="Robots">
        <button type="button" onClick={runners.reload}>
          Refresh
        </button>
      </PageHeader>
      <p className="hint">Runners execute jobs on their own machines and connect to this Server. Create, pause and revoke them in Administration.</p>
      <LoadState loaded={runners} empty={(r) => (r.length === 0 ? 'No Runner is registered. Create one in Administration, then start it with its token.' : undefined)}>
        {(list) => (
          <div className="ops-table-wrap">
            <table className="ops-table">
              <caption className="sr-only">Runners</caption>
              <thead>
                <tr>
                  <th scope="col">Runner</th>
                  <th scope="col">Status</th>
                  <th scope="col">Jobs</th>
                  <th scope="col">Last heartbeat</th>
                  <th scope="col">Machine</th>
                  <th scope="col">Capabilities</th>
                </tr>
              </thead>
              <tbody>
                {list.map((r) => (
                  <tr key={r.runnerId} className={selected === r.runnerId ? 'selected' : undefined}>
                    <td>
                      <button type="button" className="link" onClick={() => setSelected(r.runnerId)}>
                        {r.name}
                      </button>
                      <div className="hint">{r.group ? `group ${r.group}` : 'no group'}</div>
                    </td>
                    <td>
                      <Badge value={r.status} />
                    </td>
                    <td>
                      {r.activeJobs}/{r.capacity}
                    </td>
                    <td>{ago(r.lastSeenAt)}</td>
                    <td>
                      {r.machineName ?? '—'}
                      <div className="hint">
                        {r.operatingSystem ?? 'not connected yet'} {r.architecture} {r.service && '· service (no desktop)'} {r.version && `· Runner ${r.version}`}
                      </div>
                    </td>
                    <td>
                      {r.pluginCount} plugins · {r.activityCount} activities{r.pluginErrors > 0 && ` · ${r.pluginErrors} plugin error(s)`}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </LoadState>
      {selected !== undefined && (
        <section className="ops-card ops-detail" aria-label="Runner details">
          <LoadState loaded={detail}>
            {(d) =>
              d === undefined ? null : (
                <>
                  <h3>
                    {d.runner.name} <Badge value={d.runner.status} />
                  </h3>
                  <h4>Plugins</h4>
                  {d.plugins.length === 0 ? (
                    <p className="hint">Built-in activities only.</p>
                  ) : (
                    <ul className="ops-list">
                      {d.plugins.map((p) => (
                        <li key={p.id}>
                          {p.id} {p.version}
                        </li>
                      ))}
                    </ul>
                  )}
                  {d.pluginErrors.length > 0 && (
                    <ul className="ops-list error">
                      {d.pluginErrors.map((e) => (
                        <li key={e}>{e}</li>
                      ))}
                    </ul>
                  )}
                  <details>
                    <summary>{d.activities.length} activity types</summary>
                    <p className="hint">{d.activities.join(', ')}</p>
                  </details>
                  <h4>Recent jobs</h4>
                  {d.jobs.length === 0 ? <p className="hint">No job yet.</p> : <JobsTable jobs={d.jobs} onSelect={() => undefined} caption="Recent jobs of this Runner" />}
                </>
              )
            }
          </LoadState>
        </section>
      )}
    </main>
  );
}

// ───────────────────────────── Administration ─────────────────────────────

function SecretOnce({ title, token, extra }: { title: string; token: string; extra?: string }) {
  return (
    <div className="ops-secret" role="status">
      <strong>{title}</strong>
      <p className="hint">Copy it now: it is shown only once and the Server keeps only its hash.</p>
      <pre>{token}</pre>
      {extra && <pre>{extra}</pre>}
    </div>
  );
}

function RunnersAdmin() {
  const api = useOrchestrator();
  const runners = useLoad(() => api.runners(), []);
  const action = useAction();
  const [name, setName] = useState('');
  const [group, setGroup] = useState('');
  const [concurrency, setConcurrency] = useState(1);
  const [issued, setIssued] = useState<{ name: string; token: string; serverUrl: string }>();
  const [confirm, setConfirm] = useState<string>();
  const id = useId();
  const config = issued && `runner.json:\n{\n  "serverUrl": "${issued.serverUrl}",\n  "tokenFile": "runner.token",\n  "pluginConfig": "plugins.json",\n  "maxConcurrency": ${concurrency}\n}`;
  return (
    <section className="ops-card" aria-label="Runner administration">
      <h3>Runners</h3>
      <div className="ops-form">
        <label htmlFor={`${id}-name`}>Name</label>
        <input id={`${id}-name`} value={name} maxLength={80} onChange={(e) => setName(e.target.value)} />
        <label htmlFor={`${id}-group`}>Group</label>
        <input id={`${id}-group`} value={group} onChange={(e) => setGroup(e.target.value)} />
        <label htmlFor={`${id}-concurrency`}>Concurrent jobs</label>
        <input id={`${id}-concurrency`} type="number" min={1} max={64} value={concurrency} onChange={(e) => setConcurrency(Number(e.target.value))} />
        <button
          type="button"
          className="primary"
          disabled={action.busy || name.trim() === ''}
          title={name.trim() === '' ? 'Name the Runner' : undefined}
          onClick={() =>
            void action.run(
              `Runner ${name.trim()} created.`,
              async () => {
                const created = await api.createRunner(name.trim(), group.trim() || undefined, concurrency);
                setIssued({ name: created.name, token: created.token, serverUrl: created.serverUrl });
              },
              () => {
                setName('');
                runners.reload();
              },
            )
          }
        >
          Create Runner
        </button>
      </div>
      {action.outcome}
      {issued && <SecretOnce title={`Token for ${issued.name}`} token={issued.token} extra={config} />}
      <LoadState loaded={runners} empty={(r) => (r.length === 0 ? 'No Runner yet.' : undefined)}>
        {(list) => (
          <ul className="ops-list">
            {list.map((r) => (
              <li key={r.runnerId}>
                <strong>{r.name}</strong> <Badge value={r.status} /> {r.group && <small>group {r.group}</small>} · {r.maxConcurrency} concurrent{' '}
                {!r.disabled && (
                  <>
                    <button type="button" className="small" disabled={action.busy} onClick={() => void action.run(r.paused ? `${r.name} resumed.` : `${r.name} paused: it finishes its jobs and takes no new ones.`, () => api.updateRunner(r.runnerId, { paused: !r.paused }), runners.reload)}>
                      {r.paused ? 'Resume' : 'Pause'}
                    </button>{' '}
                    <button
                      type="button"
                      className="small"
                      disabled={action.busy}
                      onClick={() =>
                        void action.run(`New token issued for ${r.name}; the old one no longer works.`, async () => {
                          const rotated = await api.rotateRunnerToken(r.runnerId);
                          setIssued({ name: r.name, token: rotated.token, serverUrl: rotated.serverUrl });
                        })
                      }
                    >
                      New token
                    </button>{' '}
                    {confirm === r.runnerId ? (
                      <button
                        type="button"
                        className="small danger"
                        disabled={action.busy}
                        onClick={() =>
                          void action.run(
                            `${r.name} revoked.`,
                            () => api.revokeRunner(r.runnerId),
                            () => {
                              setConfirm(undefined);
                              runners.reload();
                            },
                          )
                        }
                      >
                        Confirm revoke
                      </button>
                    ) : (
                      <button type="button" className="small" onClick={() => setConfirm(r.runnerId)}>
                        Revoke…
                      </button>
                    )}
                  </>
                )}
              </li>
            ))}
          </ul>
        )}
      </LoadState>
    </section>
  );
}

function TokensAdmin() {
  const api = useOrchestrator();
  const tokens = useLoad(() => api.tokens(), [], 0);
  const action = useAction();
  const [name, setName] = useState('');
  const [role, setRole] = useState<Role>('Viewer');
  const [issued, setIssued] = useState<{ name: string; token: string }>();
  const id = useId();
  return (
    <section className="ops-card" aria-label="Access tokens">
      <h3>Access tokens and roles</h3>
      <p className="hint">
        People and tools use the management API with an access token and its role (Viewer, Operator, Developer, Admin). This Studio session is the local owner (Admin). User accounts and sign-in are not part of
        Phase 10.
      </p>
      <div className="ops-form">
        <label htmlFor={`${id}-name`}>Name</label>
        <input id={`${id}-name`} value={name} maxLength={80} onChange={(e) => setName(e.target.value)} />
        <label htmlFor={`${id}-role`}>Role</label>
        <select id={`${id}-role`} value={role} onChange={(e) => setRole(e.target.value as Role)}>
          {roles.map((r) => (
            <option key={r} value={r}>
              {r}
            </option>
          ))}
        </select>
        <button
          type="button"
          className="primary"
          disabled={action.busy || name.trim() === ''}
          title={name.trim() === '' ? 'Name the token' : undefined}
          onClick={() =>
            void action.run(
              `Token ${name.trim()} created.`,
              async () => {
                const created = await api.createToken(name.trim(), role);
                setIssued({ name: name.trim(), token: created.token });
              },
              () => {
                setName('');
                tokens.reload();
              },
            )
          }
        >
          Create token
        </button>
      </div>
      {action.outcome}
      {issued && <SecretOnce title={`Token ${issued.name}`} token={issued.token} />}
      <LoadState loaded={tokens} empty={(t) => (t.length === 0 ? 'No access token.' : undefined)}>
        {(list) => (
          <ul className="ops-list">
            {list.map((t) => (
              <li key={t.tokenId}>
                <strong>{t.name}</strong> <Badge value={t.role} /> created {time(t.createdAt)} by {t.createdBy}{' '}
                {t.revokedAt ? (
                  <small>revoked {time(t.revokedAt)}</small>
                ) : (
                  <button type="button" className="small" disabled={action.busy} onClick={() => void action.run(`Token ${t.name} revoked.`, () => api.revokeToken(t.tokenId), tokens.reload)}>
                    Revoke
                  </button>
                )}
              </li>
            ))}
          </ul>
        )}
      </LoadState>
    </section>
  );
}

function PolicyAdmin() {
  const api = useOrchestrator();
  const policy = useLoad(() => api.policy(), [], 0);
  const [draft, setDraft] = useState<Policy>();
  const action = useAction();
  const id = useId();
  const value = draft ?? policy.data;
  return (
    <section className="ops-card" aria-label="Execution policy">
      <h3>Execution policy</h3>
      <LoadState loaded={policy}>
        {() =>
          value === undefined ? null : (
            <div className="ops-form">
              <label htmlFor={`${id}-attempts`}>Dispatch attempts before a never-started job fails</label>
              <input id={`${id}-attempts`} type="number" min={1} max={10} value={value.maxAttempts} onChange={(e) => setDraft({ ...value, maxAttempts: Number(e.target.value) })} />
              <label htmlFor={`${id}-events`}>Events kept per job</label>
              <input id={`${id}-events`} type="number" min={100} value={value.maxEventsPerJob} onChange={(e) => setDraft({ ...value, maxEventsPerJob: Number(e.target.value) })} />
              <label htmlFor={`${id}-offline`}>Runner offline after (seconds)</label>
              <input id={`${id}-offline`} type="number" min={15} value={value.offlineAfterSeconds} onChange={(e) => setDraft({ ...value, offlineAfterSeconds: Number(e.target.value) })} />
              <label htmlFor={`${id}-timeout`}>Default job time limit (minutes; empty: engine default)</label>
              <input id={`${id}-timeout`} type="number" min={1} value={value.defaultTimeoutMinutes ?? ''} onChange={(e) => setDraft({ ...value, defaultTimeoutMinutes: e.target.value === '' ? undefined : Number(e.target.value) })} />
              <button
                type="button"
                className="primary"
                disabled={action.busy || draft === undefined}
                onClick={() =>
                  void action.run(
                    'Policy saved.',
                    () => api.setPolicy(value),
                    () => {
                      setDraft(undefined);
                      policy.reload();
                    },
                  )
                }
              >
                Save policy
              </button>
            </div>
          )
        }
      </LoadState>
      {action.outcome}
      <p className="hint">Automatic retry happens only for jobs that never started. A job whose Runner was lost while it ran is marked Interrupted and is never retried automatically.</p>
    </section>
  );
}

function HealthAdmin() {
  const api = useOrchestrator();
  const diagnostics = useLoad(() => api.diagnostics(), [], 15000);
  return (
    <section className="ops-card" aria-label="Server health and storage">
      <h3>Server health and storage</h3>
      <LoadState loaded={diagnostics}>
        {(d) => (
          <>
            <dl className="ops-facts">
              <dt>Server</dt>
              <dd>
                {d.server.version ?? '?'} · started {time(d.server.startedAt)}
              </dd>
              <dt>Runner listener</dt>
              <dd>{d.server.runnerListener ?? 'none (Runners on this machine use the Studio address)'}</dd>
              <dt>Protocol</dt>
              <dd>
                v{d.server.runnerProtocol} · heartbeat {d.server.heartbeatSeconds} s · lease {d.server.leaseSeconds} s
              </dd>
              <dt>Store</dt>
              <dd>
                {d.store.provider} schema {d.store.schemaVersion} · {(d.store.sizeBytes / 1024).toFixed(0)} KB · <code>{d.store.location}</code>
              </dd>
              <dt>Records</dt>
              <dd>
                {Object.entries(d.store.counts)
                  .map(([table, count]) => `${table} ${count}`)
                  .join(' · ')}
              </dd>
            </dl>
            <h4>Plugins on the Server</h4>
            {d.plugins.length === 0 ? (
              <p className="hint">Built-in activities only.</p>
            ) : (
              <ul className="ops-list">
                {d.plugins.map((p) => (
                  <li key={p.id}>
                    {p.id} {p.version} · {p.activities} activities
                  </li>
                ))}
              </ul>
            )}
            {d.pluginDiagnostics.length > 0 && (
              <ul className="ops-list error">
                {d.pluginDiagnostics.map((p) => (
                  <li key={`${p.code}-${p.message}`}>
                    {p.severity} {p.code}: {p.message}
                  </li>
                ))}
              </ul>
            )}
            <h4>Compatibility of deployed versions</h4>
            {d.compatibility.length === 0 ? (
              <p className="hint">Nothing is deployed.</p>
            ) : (
              <ul className="ops-list">
                {d.compatibility.map((c) => (
                  <li key={c.processId}>
                    <strong>
                      {c.name} v{c.version}
                    </strong>
                    : {c.runners.length === 0 ? 'no Runner' : c.runners.map((r) => `${r.name} — ${r.reason}`).join('; ')}
                  </li>
                ))}
              </ul>
            )}
          </>
        )}
      </LoadState>
    </section>
  );
}

function AuditAdmin() {
  const api = useOrchestrator();
  const [offset, setOffset] = useState(0);
  const audit = useLoad(() => api.audit(offset, 50), [offset], 15000);
  return (
    <section className="ops-card" aria-label="Audit history">
      <h3>Audit history</h3>
      <LoadState loaded={audit} empty={(a) => (a.length === 0 && offset === 0 ? 'No administrative action yet.' : undefined)}>
        {(entries) => (
          <>
            <ul className="ops-list">
              {entries.map((e) => (
                <li key={e.id}>
                  {time(e.time)} · <strong>{e.actor}</strong> {e.action} {e.target && <code>{e.target}</code>} {e.details}
                </li>
              ))}
            </ul>
            <div className="ops-pager">
              <button type="button" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - 50))}>
                Newer
              </button>
              <button type="button" disabled={entries.length < 50} onClick={() => setOffset(offset + 50)}>
                Older
              </button>
            </div>
          </>
        )}
      </LoadState>
    </section>
  );
}

export function AdministrationPage() {
  const role = useRole();
  return (
    <main className="home ops-page" aria-labelledby="administration-heading">
      <PageHeader id="administration-heading" icon="admin" title="Administration" />
      {role !== undefined && !hasRole(role, 'Admin') ? (
        <p className="ops-result error" role="alert">
          Administration needs the Admin role; you are {role}.
        </p>
      ) : (
        <>
          <RunnersAdmin />
          <TokensAdmin />
          <PolicyAdmin />
          <HealthAdmin />
          <AuditAdmin />
        </>
      )}
    </main>
  );
}
