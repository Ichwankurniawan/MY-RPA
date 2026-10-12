# ADR-0050: The orchestrator's store is SQLite, in one src project

- Status: **Accepted** (owner, 2026-10-12: "SQLite (Recommended)").
- Phase: 10. Related: ADR-0049 (orchestrator), ADR-0043 (the `MyRPA.Database` plugin, which also uses
  `Microsoft.Data.Sqlite`).
- Amends:
  - the rule "no database drivers in src" (ADR-0004 and `ArchitectureRules.ForbiddenPrefixes`): one src project,
    `MyRPA.Orchestration.Sqlite`, may reference `Microsoft.Data.Sqlite`;
  - `TechnologyPackageOwners`: a package may have more than one owner.

## Context

Jobs, packages, Runners, schedules, tokens and audit history must survive restarts, and job claims need atomic,
conditional updates. The alternative, JSON files with in-process locks, would need hand-built durability (atomic
replace, journal), indexing for the dashboards, and its own transaction story for schedule fires.

## Decision

- **`IOrchestrationStore`** (in `MyRPA.Orchestration`) is the persistence contract. Every method is atomic.
  - Updates of revisioned records are conditional (`WHERE revision = @expected`).
  - `FireScheduleAsync` writes a schedule's fire and its job in one transaction.
- **`SqliteOrchestrationStore`** stores one file, `<data>/orchestration.db`.
  - Settings: WAL journal, `synchronous = FULL`, one connection serialized by the Server.
  - Records are JSON documents with indexed columns for the queries the orchestrator makes (state, process, Runner,
    schedule, idempotency key, created and completed time).
  - `PRAGMA user_version` holds the schema version (2: one process per project, ADR-0049 amendment). A newer schema is
    refused. Upgrading from schema 1 (unreleased) drops processes, packages, jobs, job events and schedules and keeps
    Runners, access tokens, audit and settings.
- **Published packages are protected by triggers** against `UPDATE` and `DELETE`.
- **The data folder** is `--data` on the Server; the default is `%LOCALAPPDATA%\Laconi\Server`. Tests and the browser
  harness use temporary folders.
- **No other src project** may reference the driver. The engine, Runner and plugins never see it.

## Consequences

- Durable state with one dependency already vetted for the database plugin (MIT, Microsoft).
- A single-writer store: one Server per data folder. Backup means copying the file while the Server is stopped (or
  using SQLite's backup tools).
- Changing the schema needs a migration step in `InitializeAsync` and a new `SchemaVersion`.
