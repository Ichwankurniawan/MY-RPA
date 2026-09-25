// One multiplexed event stream per browser tab (ADR-0024): a single EventSource; each run is added as a subscription.
// The browser's own reconnect sends Last-Event-ID (the stream's position vector), so the server resumes every run.
// If the stream itself is gone (server restart, retention expired), a new one is created and the unfinished runs are
// subscribed again after the last sequence seen. Events at or below that sequence are ignored.

import { ApiError, type StudioApi } from './api';
import type { ExecutionEvent } from './types';

export interface EventSourceLike {
  readonly readyState: number;
  addEventListener(type: string, listener: (event: MessageEvent<string>) => void): void;
  onerror: ((event: Event) => void) | null;
  close(): void;
}

export type EventSourceFactory = (url: string) => EventSourceLike;

export const executionEventKinds = ['execution.started', 'node.started', 'node.completed', 'execution.completed', 'log', 'stream.gap'];

const closed = 2;

export class RunEventStream {
  private source?: EventSourceLike;
  private opening?: Promise<string>;
  private readonly lastSequence = new Map<string, number>();
  private readonly unfinished = new Set<string>();

  constructor(
    private readonly api: StudioApi,
    private readonly createSource: EventSourceFactory,
    private readonly onEvent: (event: ExecutionEvent) => void,
    private readonly onError: (message: string) => void = () => {},
  ) {}

  /** Follows a run on this tab's stream (opening the stream on first use). */
  async follow(runId: string): Promise<void> {
    this.unfinished.add(runId);
    const streamId = await this.ensureOpen();
    await this.subscribe(streamId, runId);
  }

  close(): void {
    this.source?.close();
    this.source = undefined;
    this.opening = undefined;
  }

  private ensureOpen(): Promise<string> {
    this.opening ??= this.open();
    return this.opening;
  }

  private async open(): Promise<string> {
    const streamId = await this.api.createStream();
    const source = this.createSource(this.api.streamUrl(streamId));
    for (const kind of executionEventKinds) {
      source.addEventListener(kind, (message) => this.receive(JSON.parse(message.data) as ExecutionEvent));
    }

    source.onerror = () => {
      if (source.readyState === closed && this.source === source) {
        this.reopen();
      }
    };
    this.source = source;
    return streamId;
  }

  private async subscribe(streamId: string, runId: string): Promise<void> {
    try {
      await this.api.subscribe(streamId, runId, this.lastSequence.get(runId) ?? 0);
    } catch (error) {
      if (!(error instanceof ApiError && error.status === 409)) {
        throw error;
      }
    }
  }

  private receive(event: ExecutionEvent): void {
    if (event.kind !== 'stream.gap') {
      if (event.sequence <= (this.lastSequence.get(event.runId) ?? 0)) {
        return;
      }

      this.lastSequence.set(event.runId, event.sequence);
    }

    if (event.kind === 'execution.completed' && !event.parentExecutionId) {
      this.unfinished.delete(event.runId);
    }

    this.onEvent(event);
  }

  private reopen(): void {
    this.close();
    if (this.unfinished.size === 0) {
      return;
    }

    this.ensureOpen()
      .then((streamId) => Promise.all([...this.unfinished].map((runId) => this.subscribe(streamId, runId))))
      .catch((error: unknown) => this.onError(`The event stream could not be reopened: ${(error as Error).message}`));
  }
}
