# ADR-0049: Orchestrator, workflow packages and the Runner

- Status: **Accepted** (owner, 2026-10-12).
  - The owner pasted the Phase 10 brief and confirmed "Yes, proceed to Phase 10".
  - Choices: SQLite for durable storage (ADR-0050); roles with access tokens (ADR-0051); a separate HTTPS port for
    Runners (ADR-0051).
- Phase: 10 (Orchestrator). Guide: [orchestrator.md](../architecture/orchestrator.md). Plan:
  [orchestrator-plan.md](../architecture/orchestrator-plan.md).
- Builds on:
  - ADR-0022 (the Server as control plane, `MyRPA.Execution.Hosting`);
  - ADR-0023 (execution events);
  - ADR-0014 and ADR-0019 (plugin host and configuration);
  - ADR-0048 (`AI.Agent`).
- Amends:
  - ADR-0008/0012: one type, `MyRPA.Runner.RunnerConnection`, may use `HttpClient`;
  - ADR-0003: two src libraries (`MyRPA.Orchestration`, `MyRPA.Orchestration.Sqlite`) and one composition root
    (`MyRPA.Runner`) are added.

## Context

Until Phase 10, workflows ran where they were edited: the CLI, or the Studio through the Server's `ExecutionHost`. The
brief asks for:
- publishing immutable versions;
- jobs against exact versions;
- dispatch to independently deployed Runners;
- schedules, persistent history, cancellation, recovery and security.

All of this without a second engine and without the Server executing production workflows.

## Decision

1. **Server decides, Runner executes.**
   - `MyRPA.Orchestration` holds jobs, dispatch, leases, packages, schedules and access. It is pure logic: no HTTP, no
     driver, no engine.
   - The Server hosts it behind `/api/orchestrator/*` (management) and `/runner/v1/*` (Runner protocol).
   - The Runner (`src/MyRPA.Runner`, `myrpa-runner`) composes the same engine, built-in activities, file resolver,
     plugin host and `ExecutionHost` as the CLI.
   - The Server never runs a published job. The Studio's local Run and Debug stay the development path.
2. **Packages are immutable versions of a process.** A process is a project (one process per project; see the
   amendment below).
   - **Publishing** snapshots the saved, valid workflow files of the project into a JSON package (`myrpa-package/2`),
     with the **entry points** the author chose: the workflows a job may start. Publishing one workflow is a project
     with a single entry point.
   - **The manifest** records:
     - every file's SHA-256 and the package's SHA-256;
     - the activity types used;
     - the plugins providing them, with their versions;
     - each entry point's path, workflow id, name, version, schema version and arguments, marking those that feed
       secrets;
     - who published it, and when.
   - **Versions are numbered.** The store refuses to change or delete them (database triggers).
   - **Deploying** chooses the version that jobs and schedules use. A rollback deploys an earlier version.
   - **Not in a package:** plugin binaries and plugin settings stay on Runners. Secrets cannot be in workflow files
     (MYRPA1066).
   - The workflow format does not change.
3. **Jobs pin an exact version and its hash.**
   - A job names its entry point (optional when the version has only one). Submission validates arguments against that
     entry point's workflow (type, required, input only). It refuses
     arguments that feed secret properties, because job arguments are history.
   - The job is persisted before the API answers.
   - An optional idempotency key returns the first job instead of creating another.
   - **States:**
     - `Queued → Assigned → Running → Succeeded | Failed | Cancelled | TimedOut`;
     - `Assigned → Queued` (released, or the lease expired before start);
     - `Running → Interrupted` (the outcome is unknown);
     - `Interrupted → final`, only through a late completion under the same lease.
   - Final states never change. Every change goes through `JobTransitions` and a revision-checked store update.
4. **Dispatch is Runner-initiated claiming under leases.**
   - **A claim** is a long poll of up to 25 s. It takes the highest-priority, oldest queued job the Runner is eligible
     for:
     - its target Runner or group;
     - every required activity type;
     - every required plugin at the same or a newer version;
     - free capacity, the lower of the administrator's cap and the Runner's.
   - **Claims are serialized** and the update is conditional on the job's revision, so one job cannot be held by two
     Runners.
   - **Leases** (default 45 s) are renewed by heartbeats (default every 10 s) and event batches.
   - **When a lease expires:**
     - an assigned job is requeued, and fails after `MaxAttempts` expiries (`MYRPA5003`);
     - a running job becomes `Interrupted` (`MYRPA5002`) and is never retried automatically;
     - an operator may retry it as a new, linked job.
   - **A re-registering Runner** that does not report a job the Server thought it held loses it the same way.
5. **The protocol is versioned JSON over HTTP(S), outbound from the Runner** (`RunnerProtocol.Version = 1`): `register`,
   `heartbeat`, `claim`, `GET jobs/{id}/package?lease=`, `start`, `events`, `complete` and `release`.
   - Every message about a job carries its lease. A message from another Runner or an old lease answers
     `LeaseValid = false` (409) and changes nothing.
   - Repeated completions are answered as duplicates.
   - **Events are delivered at least once** and stored once per job, attempt and sequence. Messages are capped at
     4,000 characters and outputs at 64 KB. There is a per-job event cap, and dropped events are counted.
   - Cancellation reaches the Runner in heartbeat and event answers. It is cooperative, and only the Runner's report
     makes a job `Cancelled`.
6. **Schedules** are Once, Interval and five-field Cron, each with an explicit IANA or Windows time zone.
   - **Daylight saving:** a skipped local time fires once at the end of the gap; a repeated one fires once, at its
     first occurrence.
   - **Each fire time creates at most one job.** Its idempotency key is `schedule:{id}:{ticks}`, and the job is
     written in the same transaction as the schedule's next fire time.
   - **Misfire policy:** `RunOnce` runs one job for the latest missed time; `Skip` skips them. A time missed by more
     than 2 minutes is a misfire.
   - **Overlap policy:** `Skip` (the default) or `Allow`.
   - **Disabled** schedules and deployments do not fire, and enabling a schedule does not catch up.
   - Event-based triggers are not part of Phase 10.
7. **The Runner** verifies the package hash and every file before anything runs.
   - **Before running**, it checks that every required activity and plugin is installed, unpacks the package into
     `work/jobs/<jobId>/` (paths confined), loads the entry with `WorkflowLoader` and converts the arguments.
   - **Any failure here** is reported as `NotStarted` (`MYRPA5004` integrity, `MYRPA5001` dependency, `MYRPA5005`
     preparation), so it can have had no side effects.
   - **It then runs** the workflow with `ExecutionHost` (`Location` is the unpacked entry, so sub-workflows and agent
     tool workflows resolve as in the project), streams events, reports the outcome and deletes the folder.
   - **Outputs that feed secrets** are redacted.
   - **On shutdown** it stops claiming, gives back jobs it has not started, cancels running jobs ("the Runner shut
     down") and waits up to the grace period. A Runner whose token is refused stops with exit code 3.
   - **Only `RunnerConnection`** makes HTTP calls: bounded retries with backoff for transient failures; a 401 is final.
8. **AI agents run unchanged.** `AI.Agent` jobs need its plugin and a provider plugin on the Runner. Provider and MCP
   settings and secrets stay in the Runner's plugin configuration and environment, never in packages or jobs. Agent
   events reach job history as ordinary log events.

## Amendment (2026-10-12): a package is a project with entry points

The first implementation made each published workflow its own process (`myrpa-package/1`). The owner chose instead
"a project package with entry points, where publishing one workflow is just a project with a single entry":

- **A process is a project.** The store finds a process by project (`UNIQUE(project)`); its name is the project's
  name. Every version of the project is one package; versions can have different entry points.
- **`myrpa-package/2`:** `manifest.entryPoints` (path, workflow id, name, version, schema version, arguments) replaces
  the single `entryPath`, `workflowId`, `workflowVersion`, `schemaVersion` and `arguments`. A package has at least one
  entry point and at most 100; the Runner refuses a package whose entry points are not among its files (`MYRPA5004`).
- **Jobs and schedules name an entry point** (`entryPath`). It may be omitted while the version has exactly one;
  otherwise the request is refused with the count. A retry keeps the original entry point. A schedule whose entry
  point is not in the deployed version skips the fire time and records why.
- **The Runner runs the job's entry point** (`JobAssignment.EntryPath`, unchanged in protocol v1), not a package-wide
  entry.
- **Store schema 2** (ADR-0050): schema-1 data (processes, packages, jobs, events, schedules) was never released and is
  dropped on upgrade; Runners, access tokens, audit and settings are kept.
- `POST /api/orchestrator/publish` takes `{ project, entryPoints: [...] }`; `{ project, path }` still publishes a
  single entry point.

## Consequences

- One engine, one workflow format, one event model, from the Studio to Runners.
- **Delivery is at least once, not exactly once.**
  - A job whose Runner disappears mid-run is reported as unknown, never re-run silently.
  - A job that never started is safe to requeue.
- The orchestrator is single-Server: one process serializes writes. Running several Servers on one store is not
  supported in Phase 10.
- **Runner capabilities are self-reported by authenticated Runners**, from their own configuration and loaded plugins.
  An administrator decides the Runner's group and concurrency cap.
- **Not in Phase 10:**
  - fleet deployment and auto-update;
  - event triggers;
  - queues of work items (PRD 10.4);
  - human approval;
  - checkpoint and resume.
