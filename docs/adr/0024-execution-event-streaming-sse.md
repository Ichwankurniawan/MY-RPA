# ADR-0024: Execution event streaming to browsers (SSE, one stream per tab)

- Status: Accepted, implemented in W2 (see "Implementation (W2)")
- Date: 2026-09-25
- Phase: Web Studio W0
- Related: ADR-0022, ADR-0023, ADR-0025

## Context
The browser needs a live stream of execution events: started, node states, logs, completion. Commands (run, cancel) are ordinary requests.

The W0 prototype (ASP.NET Core `TypedResults.ServerSentEvents`, native `EventSource`, real engine) measured:
- about 130,000–144,000 events per second delivered to the browser;
- a 2,000-node run (6,004 events) streamed in 42–46 ms;
- replay after a dropped connection with `Last-Event-ID`: no gaps, no duplicates;
- cancel to completion event in 3–16 ms.

It also demonstrated a limit. **Local mode is plain HTTP, so browsers use HTTP/1.1, which allows six connections per origin. Six open `EventSource` streams blocked every other request from the page.** One multiplexed stream carrying six runs did not.

## Decision
- **Server-Sent Events for server-to-browser events. REST for commands.**
  - Rejected WebSocket for the browser: it needs two-way traffic, which the browser doesn't need, plus a custom protocol and reconnect logic.
  - Rejected SignalR: an extra client library and protocol for no gain here.
  - Rejected polling: too laggy or wasteful for logs.
- **One multiplexed stream per browser tab**, for example `GET /api/events`, with subscriptions managed through REST:
  `POST /api/events/subscriptions {executionIds}` and `DELETE /api/events/subscriptions/{id}`.
  There is no stream per execution.
- **Event ids:**
  - Each execution has a monotonically increasing sequence number.
  - The multiplexed stream's `id:` is a per-stream cursor that the server maps back to per-execution positions, so `Last-Event-ID` reconnects resume without gaps.
  - Every event carries its execution id and sequence number.
- **Replay buffers** are bounded per execution, with the size limit and time-to-live configurable. Completed executions keep their final event and summary.
- **Keep-alive** comments every 15 s. A slow consumer gets a bounded queue per stream; on overflow the server closes the stream and the client resumes by cursor.
- **Authentication:**
  - `EventSource` can't send custom headers, so the stream is authenticated by the session cookie (ADR-0025).
  - GET stream endpoints must have no side effects.
- **Machine links** (Agent and Robot to server) are not decided here. They need two-way traffic on outbound connections and will get their own ADR (WebSocket or SignalR).

## Implementation (W2)
- **Endpoints:**
  - `POST /api/streams` creates a tab's stream;
  - `GET /api/streams/{id}` serves it;
  - `POST/DELETE /api/streams/{id}/subscriptions` add and remove runs.
- **Cursor format:** the SSE `id` is the stream's position vector, `index=sequence,…`. The index is the subscription's
  position in the stream. A reconnect's `Last-Event-ID` sets every subscription's position before events are replayed.
- **Reading runs:** each subscription reads its run through `ExecutionHandle.ReadEventsAsync(after)`, so replay and
  `stream.gap` reporting come from `MyRPA.Execution.Hosting` unchanged.
- **Slow clients:** a bounded queue per connection; a slow client is disconnected and resumes by cursor.
- **Streams outlive connections:** a stream is kept for 2 minutes after a disconnect and belongs to the session that
  created it.

## Consequences
- The client keeps one `EventSource` open and multiplexes by execution id.
- Serving HTTP/2 over TLS would relax the connection limit, but the design doesn't rely on it: local mode is plain HTTP on loopback.
