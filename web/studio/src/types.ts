// Wire types of MyRPA.Server (docs/architecture/server.md). Hand-written for W3; see ADR-0028.

export type Json = null | boolean | number | string | Json[] | JsonObject;
export type JsonObject = { readonly [key: string]: Json };

/** `GET /api/info`. */
export interface ServerInfo {
  name: string;
  version?: string;
  mode: string;
  workflowSchemaVersions: string[];
  projects: string[];
  /** The workflow named on the server's command line (`--open`), opened after connecting. */
  open?: { project: string; path: string } | null;
}

/** Property kinds of the activity catalog (ADR-0020, workflow-format.md §3). */
export type PropertyKind = 'Expression' | 'Text' | 'AssignmentTarget' | 'LocalName' | 'ExpressionMap' | 'AssignmentTargetMap';

export interface PropertyDescriptor {
  name: string;
  kind: PropertyKind;
  required: boolean;
  description?: string;
  allowedValues: string[];
  scopeSlots: string[];
}

export interface SlotDescriptor {
  name: string;
  required: boolean;
  prefix: boolean;
  description?: string;
}

/** One activity of `GET /api/activities` (the ADR-0020 catalog snapshot). */
export interface ActivityDescriptor {
  type: string;
  displayName: string;
  category: string;
  description?: string;
  allowsChildren: boolean;
  /** Catalog 1.1 (ADR-0037): `Graph` for containers whose children are flowchart steps; absent in 1.0 snapshots. */
  childLayout?: 'List' | 'Graph';
  properties: PropertyDescriptor[];
  slots: SlotDescriptor[];
}

/** One file of `GET /api/projects/{project}/workflows`. */
export interface WorkflowFile {
  path: string;
  size: number;
  modified: string;
}

/** A `WorkflowLoader` diagnostic (`POST /api/validate`, 422 from `POST /api/runs`). */
export interface Diagnostic {
  code: string;
  severity: string;
  message: string;
  path: string;
  nodeId?: string | null;
}

export interface ValidationResult {
  valid: boolean;
  diagnostics: Diagnostic[];
}

/** An `ExecutionEventMessage` (MyRPA.Contracts) as streamed over SSE (ADR-0024). */
export interface ExecutionEvent {
  sequence: number;
  kind: string;
  runId: string;
  time: string;
  executionId?: string;
  parentExecutionId?: string;
  workflowId?: string;
  nodeId?: string;
  activityType?: string;
  status?: string;
  error?: ExecutionError;
  durationMs?: number;
  level?: string;
  message?: string;
  missingFromSequence?: number;
  missingToSequence?: number;
  /** `debug.paused`: breakpoint, step or pause; `debug.resumed`: the command (ADR-0040). */
  reason?: string;
}

/** A command for a debug run (ADR-0040). */
export type DebugCommandName = 'continue' | 'stepInto' | 'stepOver' | 'stepOut' | 'pause';

/** One name in scope where a debug run is paused, with its value. */
export interface DebugValue {
  name: string;
  /** Argument, Variable or Local. */
  kind: string;
  type: string;
  value: Json;
}

/** `GET /api/runs/{runId}/debug`: where a debug run is paused (the only place its values appear), and its breakpoints. */
export interface DebugState {
  paused: {
    executionId: string;
    parentExecutionId?: string | null;
    workflowId: string;
    nodeId: string;
    activityType: string;
    reason: string;
    values: DebugValue[];
  } | null;
  breakpoints: string[];
}

export interface ExecutionError {
  code: string;
  message: string;
  nodeId?: string | null;
  activityType?: string | null;
  errorType?: string | null;
}

/** `GET /api/plugins`: loaded plugins and their load diagnostics (ADR-0014, ADR-0019). */
export interface PluginReport {
  plugins: { id: string; name: string; version: string; sha256: string; activities: string[] }[];
  diagnostics: { code: string; severity: string; message: string; pluginId?: string | null }[];
}

/** `GET /api/runs/{runId}`. */
export interface RunStatus {
  runId: string;
  state: string;
  lastSequence: number;
  result?: {
    status: string;
    executionId: string;
    correlationId: string;
    workflowId: string;
    durationMs: number;
    outputs: JsonObject;
    error?: ExecutionError;
  };
}

/** A recorded step (ADR-0039), as the server sends it on the event stream (`recording.step`). */
export interface RecordedStep {
  sequence: number;
  kind: 'navigate' | 'click' | 'type' | 'select' | 'upload' | 'download';
  selector?: string | null;
  alternatives?: string[];
  element?: string | null;
  text?: string | null;
  secret?: boolean;
  values?: string[];
  url?: string | null;
  fileName?: string | null;
  /** The sequence of the earlier step this one takes the place of (more typing; a click that downloaded). */
  replaces?: number | null;
}

/** `recording.step` / `recording.ended` on the tab's event stream. */
export interface RecordingEvent {
  sequence: number;
  kind: string;
  recordingId: string;
  time: string;
  step?: RecordedStep;
  endReason?: string;
  message?: string;
}

/** `POST /api/recordings/generate`: the browser plugin's activities for the kept steps, and the arguments they need. */
export interface GeneratedActivities {
  nodes: JsonObject[];
  arguments: JsonObject[];
}
