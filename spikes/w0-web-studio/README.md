# W0 Web Studio spike (throwaway)

Measurement code for the W0 decisions (ADR-0021 to ADR-0025). Results: [docs/research/w0-web-studio-spike.md](../../docs/research/w0-web-studio-spike.md).

**This is not production code.** It is not part of `MyRPA.sln`, and it is not scanned by the architecture tests. Delete it once W1 is decided. Nothing in `src` may reference it.

| Folder | Content |
|---|---|
| `server/` | ASP.NET Core minimal API on the real engine: `/api/activities`, `/api/validate`, `/api/executions` (+ cancel), SSE per execution and multiplexed (`/api/events?ids=`), and a span-listener bench (`/api/bench/listeners`). Serves `web/dist`. |
| `web/` | React + TypeScript + Vite designer spike: v1.0 JSON as an immutable tree, memoized nested blocks, CodeMirror expression editor, snapshot undo, dnd-kit or (`?nodnd=1`) pointer hit-testing, and a `window.__bench` harness. |

## Run

Build the web app:

```bash
cd spikes/w0-web-studio/web
npm ci
npm run build
```

Start the server:

```bash
cd spikes/w0-web-studio/server
dotnet run -c Release
```

Then open http://127.0.0.1:5199/ (add `?nodnd=1` for the hit-testing variant) and run `await __bench.designer('flat')`, `await __bench.designer('nested')` or `await __bench.sse()` in the browser console. The published numbers came from headless Chromium at 1440×900; see the report.
