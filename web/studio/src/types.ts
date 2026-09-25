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
}

export interface ExecutionError {
  code: string;
  message: string;
  nodeId?: string | null;
  activityType?: string | null;
  errorType?: string | null;
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
