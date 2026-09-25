// W0 spike: measurement harness, driven from the browser console / automation as window.__bench.
import { type NodeJson, type Path, type WorkflowJson, countNodes, flatDocument, getNode, insertChild, nestedDocument, removeNode, setProperty } from './model';
import { store } from './store';
import { editorFor } from './Properties';

export const nextPaint = () =>
  new Promise<void>((resolve) =>
    requestAnimationFrame(() => {
      const channel = new MessageChannel();
      channel.port1.onmessage = () => resolve();
      channel.port2.postMessage(null);
    }));

export const commits: number[] = [];

function stats(samples: number[]) {
  const sorted = [...samples].sort((a, b) => a - b);
  const at = (q: number) => sorted[Math.min(sorted.length - 1, Math.floor(q * sorted.length))];
  const r = (x: number) => Math.round(x * 10) / 10;
  return { n: samples.length, p50: r(at(0.5)), p95: r(at(0.95)), max: r(sorted[sorted.length - 1]), mean: r(samples.reduce((a, b) => a + b, 0) / samples.length) };
}

async function timed(action: () => void | Promise<void>): Promise<number> {
  const t0 = performance.now();
  await action();
  await nextPaint();
  return performance.now() - t0;
}

function leaves(node: NodeJson, path: Path = [], out: Path[] = []): Path[] {
  if (node.type === 'Core.Log') out.push(path);
  (node.children ?? []).forEach((c, i) => leaves(c, [...path, { c: i }], out));
  Object.entries(node.slots ?? {}).forEach(([k, c]) => leaves(c, [...path, { s: k }], out));
  return out;
}

// Deterministic pseudo-random so runs are comparable.
let seed = 42;
const random = () => ((seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648);
const pick = <T,>(items: T[]) => items[Math.floor(random() * items.length)];

const blockAt = (path: Path) => document.querySelector<HTMLElement>(`.block[data-path="${path.map((s) => ('c' in s ? `c${s.c}` : `s:${s.s}`)).join('/')}"]`)!;

function pointer(type: string, target: EventTarget, x: number, y: number) {
  target.dispatchEvent(new PointerEvent(type, { bubbles: true, cancelable: true, pointerId: 1, isPrimary: true, button: 0, buttons: type === 'pointerup' ? 0 : 1, clientX: x, clientY: y, pointerType: 'mouse' }));
}

export async function runDesigner(shape: 'flat' | 'nested', nodes = 3000) {
  seed = 42;
  const results: Record<string, unknown> = { dnd: location.search.includes('nodnd') ? 'pointer hit-testing' : 'dnd-kit', shape, userAgent: navigator.userAgent, visibility: document.visibilityState, viewport: `${innerWidth}x${innerHeight}` };
  const json = JSON.stringify(shape === 'flat' ? flatDocument(nodes) : nestedDocument(nodes));
  results.documentKB = Math.round(json.length / 1024);

  // Open: parse + first render + paint.
  store.load(null as unknown as WorkflowJson);
  await nextPaint();
  commits.length = 0;
  const t0 = performance.now();
  const doc = JSON.parse(json) as WorkflowJson;
  const parsed = performance.now();
  store.load(doc);
  await nextPaint();
  results.open = { parseMs: Math.round(parsed - t0), totalToPaintMs: Math.round(performance.now() - t0), reactCommitMs: Math.round(commits.reduce((a, b) => a + b, 0)) };
  results.nodes = countNodes(doc.root);
  results.dom = { blocks: document.querySelectorAll('.block').length, zones: document.querySelectorAll('.zone').length, elements: document.querySelectorAll('*').length };

  const logPaths = leaves(doc.root);

  // Selection (includes mounting the CodeMirror editor for the selected block).
  const select: number[] = [];
  const mount: number[] = [];
  for (let i = 0; i < 20; i++) {
    select.push(await timed(() => store.select(pick(logPaths))));
    mount.push((window as unknown as { __lastEditorMountMs: number }).__lastEditorMountMs);
  }
  results.selectToPaintMs = stats(select);
  results.codeMirrorMountMs = stats(mount);

  // Property edit (as a committed value from an editor).
  const edit: number[] = [];
  for (let i = 0; i < 50; i++) {
    const p = pick(logPaths);
    edit.push(await timed(() => store.apply(setProperty(store.doc!, p, 'message', `'edited ${i}'`))));
  }
  results.propertyEditToPaintMs = stats(edit);

  // Typing into CodeMirror: each keystroke updates the document and the block summary.
  const target = pick(logPaths);
  store.select(target);
  await nextPaint();
  const view = editorFor('message')!;
  const typing: number[] = [];
  for (let i = 0; i < 50; i++) {
    typing.push(await timed(() => view.dispatch({ changes: { from: view.state.doc.length, insert: 'x' }, userEvent: 'input.type' })));
  }
  results.keystrokeToPaintMs = stats(typing);
  results.typingMergedIntoOneUndoStep = String(getNode(store.doc!.root, target).properties?.message).endsWith('x'.repeat(50));

  // Undo / redo.
  const undo: number[] = [];
  const redo: number[] = [];
  for (let i = 0; i < 30; i++) undo.push(await timed(() => store.undo()));
  for (let i = 0; i < 30; i++) redo.push(await timed(() => store.redo()));
  results.undoToPaintMs = stats(undo);
  results.redoToPaintMs = stats(redo);

  // Keyboard move (Alt+ArrowDown on the focused block): real key events through React.
  const move: number[] = [];
  let moved = 0;
  for (let i = 0; i < 30; i++) {
    const current = leaves(store.doc!.root);
    const p = pick(current.filter((q) => { const l = q[q.length - 1]; return 'c' in l && l.c < 8; }));
    store.select(p);
    await nextPaint();
    const el = blockAt(p);
    el.focus();
    const before = store.doc;
    move.push(await timed(() => { el.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', altKey: true, bubbles: true, cancelable: true })); }));
    if (store.doc !== before) moved++;
  }
  results.keyboardMoveToPaintMs = stats(move);
  results.keyboardMovesApplied = `${moved}/30`;

  // Insert and delete at the root.
  const insert: number[] = [];
  const remove: number[] = [];
  for (let i = 0; i < 20; i++) {
    insert.push(await timed(() => store.apply(insertChild(store.doc!, [], 0, { id: `new-${i}`, type: 'Core.Log', properties: { message: "'new'" } }))));
    remove.push(await timed(() => store.apply(removeNode(store.doc!, [{ c: 0 }]))));
  }
  results.insertToPaintMs = stats(insert);
  // Insert at the END of the root list: no sibling shifts, so this isolates the cost of index/path identity.
  const insertEnd: number[] = [];
  for (let i = 0; i < 20; i++) {
    const count = store.doc!.root.children!.length;
    insertEnd.push(await timed(() => store.apply(insertChild(store.doc!, [], count, { id: `end-${i}`, type: 'Core.Log', properties: { message: "'end'" } }))));
    store.apply(removeNode(store.doc!, [{ c: count }]));
    await nextPaint();
  }
  results.insertAtEndToPaintMs = stats(insertEnd);
  results.deleteToPaintMs = stats(remove);

  // dnd-kit pointer drag of a block over many drop zones.
  const source: Path = shape === 'flat' ? [{ c: 0 }, { c: 1 }] : [{ c: 0 }, { c: 0 }];
  const el = blockAt(source);
  el.scrollIntoView({ block: 'center' });
  await nextPaint();
  const rect = el.getBoundingClientRect();
  const x = rect.left + 20;
  let y = rect.top + 8;
  const docBefore = store.doc;
  const dragStart = await timed(() => { pointer('pointerdown', el, x, y); pointer('pointermove', document, x, y + 6); });
  const drag: number[] = [];
  for (let i = 0; i < 20; i++) {
    y += 12;
    drag.push(await timed(() => pointer('pointermove', document, x, y)));
  }
  const drop = await timed(() => pointer('pointerup', document, x, y));
  results.dndKitPointer = { activationMs: Math.round(dragStart), moveToPaintMs: stats(drag), dropToPaintMs: Math.round(drop), documentChanged: store.doc !== docBefore };

  // dnd-kit keyboard drag (Space to lift, arrows to move, Space to drop). Not applicable without dnd-kit.
  if (location.search.includes('nodnd')) {
    results.dndKitKeyboard = 'n/a (keyboard moves are Alt+Arrow / cut-paste commands)';
  } else {
  const kSource = leaves(store.doc!.root)[3];
  store.select(kSource);
  await nextPaint();
  const kEl = blockAt(kSource);
  kEl.focus();
  const key = (k: string, t: EventTarget = kEl) => t.dispatchEvent(new KeyboardEvent('keydown', { key: k, code: k === ' ' ? 'Space' : k, bubbles: true, cancelable: true }));
  const kBefore = store.doc;
  const lift = await timed(() => { key(' '); });
  const kMove: number[] = [];
  for (let i = 0; i < 10; i++) kMove.push(await timed(() => { key('ArrowDown', document); }));
  const kDrop = await timed(() => { key(' ', document); });
  results.dndKitKeyboard = { liftMs: Math.round(lift), arrowToPaintMs: stats(kMove), dropMs: Math.round(kDrop), documentChanged: store.doc !== kBefore };
  }

  // Validation round trip against the real WorkflowLoader (spike server).
  const serialize: number[] = [];
  const roundTrip: number[] = [];
  const server: number[] = [];
  let diagnostics = 0;
  for (let i = 0; i < 10; i++) {
    const a = performance.now();
    const body = JSON.stringify(store.doc);
    const b = performance.now();
    const response = await fetch('/api/validate', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body });
    const result = await response.json();
    const c = performance.now();
    serialize.push(b - a);
    roundTrip.push(c - b);
    server.push(result.serverMs);
    diagnostics = result.diagnostics.length;
  }
  results.validation = { serializeMs: stats(serialize), roundTripMs: stats(roundTrip), serverMs: stats(server), diagnostics, bodyKB: Math.round(JSON.stringify(store.doc).length / 1024) };

  const memory = (performance as unknown as { memory?: { usedJSHeapSize: number } }).memory;
  results.jsHeapMB = memory ? Math.round(memory.usedJSHeapSize / 1048576) : 'n/a';
  results.undoDepth = store.undoDepth;
  return results;
}

// ---------------------------------------------------------------- SSE prototype measurements

const sequenceOfLogs = (n: number) => ({
  schemaVersion: '1.0', id: 'logs', name: 'Logs', version: '1.0.0',
  root: { id: 'main', type: 'Core.Sequence', children: Array.from({ length: n }, (_, i) => ({ id: `l${i}`, type: 'Core.Log', properties: { message: `'line ${i}'` } })) },
});
const delay = (ms: number) => ({ schemaVersion: '1.0', id: 'wait', name: 'Wait', version: '1.0.0', root: { id: 'wait', type: 'Core.Delay', properties: { milliseconds: ms } } });

async function start(workflow: unknown, timeoutMs?: number): Promise<string> {
  const response = await fetch('/api/executions', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ workflow, timeoutMs }) });
  return (await response.json()).id;
}

function collect(url: string): Promise<{ events: { seq: number; type: string; status?: string }[]; ms: number; firstMs: number }> {
  return new Promise((resolve) => {
    const t0 = performance.now();
    let first = 0;
    const events: { seq: number; type: string; status?: string }[] = [];
    const source = new EventSource(url);
    const handle = (e: MessageEvent) => {
      if (!first) first = performance.now() - t0;
      const data = JSON.parse(e.data);
      events.push({ seq: data.seq, type: data.type, status: data.status });
      if (data.type === 'execution.completed') {
        source.close();
        resolve({ events, ms: performance.now() - t0, firstMs: first });
      }
    };
    for (const type of ['execution.started', 'node.started', 'node.completed', 'node.failed', 'log', 'execution.completed']) {
      source.addEventListener(type, handle as EventListener);
    }
  });
}

/** Reads an SSE stream with fetch, so a reconnect can send Last-Event-ID explicitly. */
async function readStream(url: string, lastEventId: number | null, stopAfter: number | null): Promise<number[]> {
  const controller = new AbortController();
  const response = await fetch(url, { headers: lastEventId ? { 'Last-Event-ID': String(lastEventId) } : {}, signal: controller.signal });
  const reader = response.body!.pipeThrough(new TextDecoderStream()).getReader();
  const seqs: number[] = [];
  let buffer = '';
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += value;
      let end;
      while ((end = buffer.indexOf('\n\n')) >= 0) {
        const block = buffer.slice(0, end);
        buffer = buffer.slice(end + 2);
        const data = block.split('\n').find((l) => l.startsWith('data:'));
        if (data) {
          const event = JSON.parse(data.slice(5));
          seqs.push(event.seq);
          if (stopAfter !== null && seqs.length >= stopAfter) {
            controller.abort();
            return seqs;
          }
        }
      }
    }
  } catch {
    // aborted
  }
  return seqs;
}

async function ping(timeoutMs = 3000): Promise<number | 'blocked'> {
  const t0 = performance.now();
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    await fetch(`/api/ping?${Math.random()}`, { signal: controller.signal });
    return Math.round((performance.now() - t0) * 10) / 10;
  } catch {
    return 'blocked';
  } finally {
    clearTimeout(timer);
  }
}

export async function runSse() {
  const results: Record<string, unknown> = {};

  // Throughput: 2,000 Log nodes -> 2,000 log + 4,000 node events.
  for (const n of [200, 2000]) {
    const id = await start(sequenceOfLogs(n));
    const { events, ms, firstMs } = await collect(`/api/executions/${id}/events`);
    const seqs = events.map((e) => e.seq);
    const contiguous = seqs.every((s, i) => s === i + 1);
    results[`throughput_${n}_logs`] = { events: events.length, totalMs: Math.round(ms), firstEventMs: Math.round(firstMs), eventsPerSecond: Math.round(events.length / (ms / 1000)), contiguousFromStart: contiguous, status: events.at(-1)?.status };
  }

  // Replay: read 1,000 events, drop the connection, resume with Last-Event-ID.
  {
    const id = await start(sequenceOfLogs(2000));
    const first = await readStream(`/api/executions/${id}/events`, null, 1000);
    await new Promise((r) => setTimeout(r, 300));
    const rest = await readStream(`/api/executions/${id}/events`, first.at(-1)!, null);
    const all = [...first, ...rest];
    results.replay = { firstPart: first.length, resumedPart: rest.length, total: all.length, noGapsNoDuplicates: all.every((s, i) => s === i + 1) };
  }

  // Cancel: a 20 s Delay, cancelled after 300 ms.
  {
    const id = await start(delay(20000));
    const streamed = collect(`/api/executions/${id}/events`);
    await new Promise((r) => setTimeout(r, 300));
    const t0 = performance.now();
    await fetch(`/api/executions/${id}/cancel`, { method: 'POST' });
    const { events } = await streamed;
    results.cancel = { finalStatus: events.at(-1)?.status, cancelToCompletedEventMs: Math.round(performance.now() - t0) };
  }

  // Connection limit: HTTP/1.1 allows 6 connections per origin; each open EventSource holds one.
  {
    const baseline = await ping();
    const ids = await Promise.all(Array.from({ length: 6 }, () => start(delay(8000))));
    const sources = ids.slice(0, 5).map((id) => new EventSource(`/api/executions/${id}/events`));
    await new Promise((r) => setTimeout(r, 500));
    const with5 = await ping();
    sources.push(new EventSource(`/api/executions/${ids[5]}/events`));
    await new Promise((r) => setTimeout(r, 500));
    const with6 = await ping();
    sources.forEach((s) => s.close());
    await new Promise((r) => setTimeout(r, 500));
    const multiplexed = new EventSource(`/api/events?ids=${ids.join(',')}`);
    await new Promise((r) => setTimeout(r, 500));
    const withOneMultiplexed = await ping();
    multiplexed.close();
    for (const id of ids) await fetch(`/api/executions/${id}/cancel`, { method: 'POST' });
    results.connectionLimit = { pingMsNoStreams: baseline, pingMsWith5Streams: with5, pingMsWith6Streams: with6, pingMsWithOneMultiplexedStreamFor6Runs: withOneMultiplexed, protocol: (performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming | undefined)?.nextHopProtocol };
  }

  return results;
}
