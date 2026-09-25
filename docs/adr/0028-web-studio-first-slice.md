# ADR-0028: Web Studio first slice (W3)

- Status: Accepted for W3. Decision 6 (hand-written wire types instead of OpenAPI-generated ones) needs owner approval.
- Date: 2026-09-25
- Phase: Web Studio W3
- Implements: [ADR-0021](0021-web-first-studio-and-wpf-removal.md) (Web Studio, frontend baseline),
  [ADR-0022](0022-server-control-plane-and-project-structure.md) ("serves the built `web/studio` assets")
- Keeps: [ADR-0024](0024-execution-event-streaming-sse.md) (one stream per tab), [ADR-0025](0025-local-mode-security.md) (local-mode security)

## Context
W3 puts the first Web Studio on screen and proves the whole loop: browser → `MyRPA.Server` → `WorkflowLoader` /
project store → `Execution.Hosting` → engine → events and logs → SSE → browser. Full parity with the WPF Studio is
explicitly out of scope; the ADR-0021 exit criteria stay the target for later slices.

Every server behavior the slice needs already existed after W2, except one: `GET /` only printed placeholder text, so
the server could not serve the Studio. The browser must load the Studio from the server's own origin because local-mode
security depends on it:
- the session cookie is `SameSite=Strict`;
- the CSP is `default-src 'self'`;
- state-changing requests must carry the server's own `Origin`.

## Decision
1. **The server serves the built Studio (`--web <dir>`).**
   - The option names the Vite output (`web/studio/dist`, which must contain `index.html`). `GET /` then serves
     `index.html` (`no-cache`); other files are served from that folder.
   - Static files pass through the same guard as everything else (Host check, CSP, `nosniff`, `DENY`). Hidden,
     dot-prefixed and system files and unknown file types are never served. The assets hold no data, so they need no
     session; `/api` still does.
   - Without `--web`, nothing changes. No endpoint, header or security rule changed.
2. **The development proxy is development-only.** `npm run dev` serves the Studio from Vite (`127.0.0.1:5173`) and
   forwards `/api` and the start link (`/?token=`) to a running server (`MYRPA_SERVER`, default `http://127.0.0.1:5310`).
   It re-labels the `Origin` only of requests that come from the dev page itself; any other `Origin` passes through
   unchanged and the server refuses it. Production never involves Vite.
3. **The editing model is the v1.0 JSON itself** (ADR-0021).
   - An immutable tree; unknown fields survive because nothing is mapped into another shape.
   - Stable client keys live in a `WeakMap` keyed by node object and move to the replacement when an edit copies a
     node. They are never written to the file.
   - An edit copies only the path from the edited node to the root, so tree blocks memoized on node identity re-render
     only along that path.
4. **No silent changes.** A file the browser cannot write back unchanged opens read-only, with the reason shown:
   - number forms JavaScript would rewrite (`1.0`, `1.50`, `1e3`, integers beyond 2^53);
   - objects with integer-like keys (JavaScript would reorder them).
   Files with JSON comments or trailing commas (which the engine accepts) cannot be opened yet. Saving re-indents the
   file (two spaces); values and key order are unchanged.
5. **Property editing in W3.**
   - Editable: string values of the Expression, Text, AssignmentTarget and LocalName kinds (Text with allowed values
     as a list), and the display name.
   - Read-only: ExpressionMap and AssignmentTargetMap values, non-string literals (numbers, booleans, null), properties
     the activity does not declare, and every property of an activity missing from the catalog.
   - Clearing an optional property removes it; a required one keeps the empty string, so validation reports it on
     the property (ADR-0026).
6. **Wire types are hand-written for W3** (`web/studio/src/types.ts`, mirroring
   [server.md](../architecture/server.md)). ADR-0022 says the types are generated from the server's OpenAPI document.
   Generating them needs an OpenAPI document from the server (a package or build step) and a generator in the frontend
   toolchain; neither pays off for the ~10 shapes this slice uses. **Proposed:** keep hand-written types until the API
   grows, then add OpenAPI generation as its own step. This deviation needs the owner's approval.
7. **State.** One small external store (`useSyncExternalStore`), no state library. Components subscribe to slices;
   each tree block subscribes to its own selection, error and run-status flags.
8. **Events.** One `EventSource` per tab. Runs are added as subscriptions. The browser's reconnect resumes by
   `Last-Event-ID`; a stream the server no longer knows is re-created and unfinished runs are subscribed again after the
   last sequence seen. Events at or below that sequence are ignored.
9. **Dependencies.**
   - Runtime: React and React DOM only.
   - Development: Vite, TypeScript, Vitest, jsdom, Testing Library, and `playwright-core` for the smoke test.
   - No UI framework, state library, editor component, diagramming or drag-and-drop library.
   - The smoke test uses the Chromium revision already installed for the browser plugin and never downloads browsers.
   - The architecture tests reject dnd-kit and other drag-and-drop frameworks in `package.json` and `package-lock.json`.

## Consequences
- `MyRPA.Server --project <dir> --web web/studio/dist` is the local Studio. Node/npm are needed to build the Studio,
  not to run it.
- ADR-0021 exit criteria advanced by W3: open, save with conflict detection, validation shown on the property and the
  node, run with live status and logs, per-node run state, toolbox filter, keyboard tree navigation, dirty marker,
  `beforeunload` protection. Everything else in the exit criteria stays open.
- Deferred, listed in [web-studio.md](../architecture/web-studio.md): structural editing, drag-and-drop, undo/redo,
  copy/paste, variables/arguments and metadata editors, map and literal editors, CodeMirror expressions, run arguments,
  Stop, timeouts, lossless round-trip of every literal form, OpenAPI types, the smoke test in CI, accessibility audit.
- CI gains a Linux job for the Studio: `npm ci`, typecheck, unit tests, build.
