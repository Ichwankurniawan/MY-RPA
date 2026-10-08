// Measures the editing targets of ADR-0021 on the 3,000-node "flat" fixture of the W0 spike (a root Sequence with 300
// groups of one Sequence and 9 Logs = 3,001 nodes). Each sample is timed inside the page, from the command to the next
// painted frame, like the W0 measurements; the synchronous part (command, store update, React render and commit) is
// reported separately, since the rest is waiting for the browser's next frame. Real server, production build, headless Chromium. See harness.mjs.

import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { check, withStudio } from './harness.mjs';

const samples = Number(process.env.MYRPA_PERF_SAMPLES ?? 40);
// CI runners are shared, slower machines: MYRPA_PERF_TOLERANCE scales every target there (default 1: the ADR-0021
// targets as they are). The tolerance used is printed with the results.
const tolerance = Number(process.env.MYRPA_PERF_TOLERANCE ?? 1);

function fixture() {
  const groups = Array.from({ length: 300 }, (_, g) => ({
    id: `group-${g}`,
    type: 'Core.Sequence',
    children: Array.from({ length: 9 }, (_, i) => ({ id: `log-${g}-${i}`, type: 'Core.Log', properties: { message: `'line ${g}.${i}'` } })),
  }));
  return { schemaVersion: '1.0', id: 'flat-3000', name: 'Flat 3000', version: '1.0.0', root: { id: 'main', type: 'Core.Sequence', children: groups } };
}

const percentile = (values, p) => {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)];
};

await withStudio(async ({ project, page, startServer, problems }) => {
  writeFileSync(join(project, 'flat-3000.json'), JSON.stringify(fixture(), null, 2));
  await page.goto(await startServer());
  await page.getByText('Connected to MyRPA.Server').waitFor();
  await page.getByRole('combobox', { name: 'Workflow' }).selectOption('flat-3000.json');

  const openStart = Date.now();
  await page.getByRole('button', { name: 'Open', exact: true }).click();
  await page.locator('[role=treeitem][data-node-id="log-299-8"]').waitFor();
  const openMs = Date.now() - openStart;
  const nodes = await page.locator('[role=treeitem][data-node-id]').count();
  check(nodes === 3001, `fixture nodes: ${nodes}`);
  await page.locator('[role=treeitem][data-node-id="log-150-4"] > .node').click();

  // Runs `count` commands of one kind in the page and returns each one's time to the next painted frame (ms).
  const measure = (kind, count = samples) =>
    page.evaluate(
      async ({ kind, count }) => {
        const frame = () => new Promise((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0)));
        const key = (k, options) => window.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true, ...options }));
        const button = (name) => [...document.querySelectorAll('[role="toolbar"][aria-label="Edit"] button')].find((b) => b.textContent === name);
        const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
        const times = [];
        const scripts = [];
        for (let i = 0; i < count; i++) {
          const start = performance.now();
          if (kind === 'keystroke') {
            const input = document.querySelector('.properties input[placeholder^="expression"]');
            setValue.call(input, `${input.value}x`);
            input.dispatchEvent(new Event('input', { bubbles: true }));
          } else if (kind === 'move') {
            button(i % 2 === 0 ? 'Move up' : 'Move down').click();
          } else if (kind === 'undo') {
            key('z', { ctrlKey: true });
          } else if (kind === 'redo') {
            key('y', { ctrlKey: true });
          } else if (kind === 'insert') {
            document.querySelector('button[aria-label="Insert Log (Core.Log)"]').click();
          } else if (kind === 'delete') {
            button('Delete').click();
          }

          scripts.push(performance.now() - start);
          await frame();
          times.push(performance.now() - start);
        }

        return { times, scripts };
      },
      { kind, count },
    );

  // The W4A editing metrics, on the page as W4A measured it (no run shown yet).
  const results = [
    ['Property keystroke to paint', await measure('keystroke'), 50],
    ['Structural: move up/down', await measure('move'), 100],
    ['Undo', await measure('undo'), 50],
    ['Redo', await measure('redo'), 50],
    ['Structural: insert', await measure('insert'), 100],
    ['Structural: delete', await measure('delete'), 100],
  ];

  // W7: drag-and-drop on the 3,001-node tree with real layout and hit-testing. Activation: pointer down on a card and
  // a move past the threshold, to the next painted frame. Movement: each move over another card, to the next frame.
  const drag = await page.evaluate(async (count) => {
    const frame = () => new Promise((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0)));
    const visible = [...document.querySelectorAll('.designer [role="treeitem"] > .node')].filter((card) => {
      const r = card.getBoundingClientRect();
      // Leaf activities only: the root and containers cannot move into themselves, so their moves would be refused
      // (no indicator) and the samples would depend on which cards happen to be visible in the layout.
      return r.top > 0 && r.bottom < window.innerHeight * 0.6 && r.height > 0 && card.parentElement.dataset.nodeId.startsWith('log-');
    });
    const at = (card, dy = 0) => {
      const r = card.getBoundingClientRect();
      return { clientX: r.left + r.width / 2, clientY: r.top + r.height / 2 + dy, bubbles: true, pointerId: 1, button: 0, isPrimary: true };
    };
    const activation = [];
    const movement = [];
    for (let i = 0; i < count; i++) {
      const source = visible[i % Math.min(visible.length, 6)];
      const start = performance.now();
      source.dispatchEvent(new PointerEvent('pointerdown', at(source)));
      document.dispatchEvent(new PointerEvent('pointermove', at(source, 8)));
      await frame();
      activation.push(performance.now() - start);
      for (let m = 1; m <= 3; m++) {
        // Aim at the gap above another card (3 px below its top edge): a real drop preview (indicator drawn), never a
        // refused hover over the dragged card itself, so every sample measures the same work whatever the layout.
        // Never the dragged card or its neighbours: dropping a card next to itself is a no-op and refused (nothing drawn).
        const from = visible.indexOf(source);
        let to = (from + 2 + m * 3) % visible.length;
        while (Math.abs(to - from) <= 1) {
          to = (to + 2) % visible.length;
        }
        const over = visible[to];
        const t = performance.now();
        document.dispatchEvent(new PointerEvent('pointermove', at(over, 3 - over.getBoundingClientRect().height / 2)));
        await frame();
        movement.push(performance.now() - t);
      }

      // Cancel the drag (Escape) so the fixture is unchanged.
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      document.dispatchEvent(new PointerEvent('pointerup', at(source)));
      await frame();
    }

    return { activation, movement, cards: visible.length };
  }, samples);
  check(drag.cards >= 6, `too few visible cards for the drag measurement: ${drag.cards}`);

  // Reopen the fixture (discarding those edits in the Studio's unsaved-changes dialog), so it is valid again: Run
  // validates first.
  await page.getByRole('button', { name: 'Open', exact: true }).click();
  await page.getByRole('dialog', { name: 'Unsaved changes' }).getByRole('button', { name: 'Discard', exact: true }).click();
  await page.getByTestId('document-title').filter({ hasText: /^flat-3000\.json$/ }).waitFor();
  await page.locator('[role=treeitem][data-node-id="log-150-4"] > .node').click();

  // W5: typing while the fixture itself runs. The run streams about 8,700 events (3,001 node starts and completions,
  // 2,700 logs) through SSE into the Studio; each keystroke is timed to the next painted frame, as above, for as
  // long as the run's events keep arriving. Long tasks (> 50 ms on the main thread) are counted too.
  const heavy = await page.evaluate(async () => {
    const frame = () => new Promise((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0)));
    const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
    const status = () => document.querySelector('[data-testid="run-status"]').textContent;
    const longTasks = [];
    const observer = new PerformanceObserver((list) => list.getEntries().forEach((e) => longTasks.push(e.duration)));
    observer.observe({ type: 'longtask', buffered: false });
    const runButton = [...document.querySelectorAll('header button')].find((b) => b.textContent === 'Run');
    const start = performance.now();
    const events = () => document.querySelectorAll('.events li').length;
    runButton.click();
    // Until the run's first events are shown (with batching, a fast run may skip straight past Running).
    while (events() === 0 && performance.now() - start < 30_000) {
      await frame();
    }

    const times = [];
    while (!status().includes('Succeeded') && performance.now() - start < 120_000) {
      const t0 = performance.now();
      const input = document.querySelector('.properties input[placeholder^="expression"]');
      setValue.call(input, `${input.value}y`);
      input.dispatchEvent(new Event('input', { bubbles: true }));
      await frame();
      times.push(performance.now() - t0);
    }

    observer.disconnect();
    return { times, totalMs: performance.now() - start, status: status(), longTasks };
  });
  check(heavy.status.includes('Succeeded'), `the event-heavy run did not finish: ${heavy.status}`);
  const engineDuration = await page.getByTestId('run-elapsed').textContent();

  // G-2: a 200-step flowchart on the canvas (a chain with a loop back every 10 steps, placed on a 10-column grid).
  // Measured: open to interactive, typing in Properties with a step selected, each pointer move while moving a card,
  // and each move while drawing an arrow (time to the next painted frame).
  const steps = Array.from({ length: 200 }, (_, i) => ({
    id: `s${i}`,
    type: 'Core.Log',
    properties: { message: `'step ${i}'` },
    layout: { x: 40 + (i % 10) * 260, y: 40 + Math.floor(i / 10) * 150 },
    transitions: i === 199 ? [] : i % 10 === 9 && i > 9 ? [{ to: `s${i - 9}`, when: 'false' }, { to: `s${i + 1}` }] : [{ to: `s${i + 1}` }],
  }));
  writeFileSync(join(project, 'flow-200.json'), JSON.stringify({ schemaVersion: '1.1', id: 'flow-200', name: 'Flow 200', version: '1.0.0', root: { id: 'flow', type: 'Core.Flowchart', properties: { maxSteps: 100000 }, children: steps } }, null, 2));
  await page.reload();
  await page.getByText('Connected to MyRPA.Server').waitFor();
  await page.getByRole('combobox', { name: 'Workflow' }).selectOption('flow-200.json');
  const flowOpenStart = Date.now();
  await page.getByRole('button', { name: 'Open', exact: true }).click();
  await page.locator('.flow-step[data-node-id="s199"]').waitFor();
  const flowOpenMs = Date.now() - flowOpenStart;
  check((await page.locator('.flow-arrows g.arrow').count()) === 217, `arrows: ${await page.locator('.flow-arrows g.arrow').count()}`);
  await page.locator('.flow-step[data-node-id="s11"] > .node').click();
  const flowKeystroke = await measure('keystroke');
  const canvasGestures = await page.evaluate(async (count) => {
    const frame = () => new Promise((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0)));
    const pointer = (type, target, x, y) => target.dispatchEvent(new PointerEvent(type, { bubbles: true, button: 0, pointerId: 7, isPrimary: true, clientX: x, clientY: y }));
    const surface = document.querySelector('.flow-surface');
    const centre = (element) => {
      const r = element.getBoundingClientRect();
      return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
    };
    const gesture = async (from) => {
      const start = centre(from);
      const times = [];
      pointer('pointerdown', from, start.x, start.y);
      for (let i = 1; i <= count; i++) {
        const t = performance.now();
        pointer('pointermove', surface, start.x + i * 3, start.y + i * 2);
        await frame();
        times.push(performance.now() - t);
      }

      pointer('pointerup', surface, start.x + count * 3, start.y + count * 2);
      await frame();
      return times;
    };
    const card = document.querySelector('.flow-step[data-node-id="s12"] > .node');
    const move = await gesture(card);
    const draw = await gesture(document.querySelector('.flow-step[data-node-id="s13"] .connect-handle'));
    return { move, draw };
  }, samples);
  check(await page.locator('.flow-step[data-node-id="s12"]').evaluate((e) => e.style.left !== '560px'), 'the card move was committed');

  console.log(`3,001-node fixture, ${samples} samples per metric (production build, headless Chromium)`);
  console.log(`  Open to interactive (click Open → last node rendered, includes the file request): ${openMs} ms (target ≤ 1000)`);
  let within = openMs <= 1000 * tolerance;
  for (const [name, { times, scripts }, target] of results) {
    const p50 = percentile(times, 50);
    const p95 = percentile(times, 95);
    within &&= p95 <= target * tolerance;
    console.log(
      `  ${name}: p50 ${p50.toFixed(1)} ms, p95 ${p95.toFixed(1)} ms (target p95 ≤ ${target}); ` +
        `script and render before the frame: p50 ${percentile(scripts, 50).toFixed(1)} ms, p95 ${percentile(scripts, 95).toFixed(1)} ms`,
    );
  }

  for (const [name, times, target] of [
    ['Drag activation (press and first move to paint)', drag.activation, 100],
    ['Drag movement (each move to paint)', drag.movement, 50],
  ]) {
    const p95 = percentile(times, 95);
    within &&= p95 <= target * tolerance;
    console.log(`  ${name}: p50 ${percentile(times, 50).toFixed(1)} ms, p95 ${p95.toFixed(1)} ms (target p95 ≤ ${target})`);
  }

  const heavyP95 = heavy.times.length > 0 ? percentile(heavy.times, 95) : 0;
  console.log(
    `  Keystroke to paint during an event-heavy run (the fixture itself, ~8,700 events): ${heavy.times.length} samples, ` +
      (heavy.times.length > 0 ? `p50 ${percentile(heavy.times, 50).toFixed(1)} ms, p95 ${heavyP95.toFixed(1)} ms (informational, ADR-0030); ` : 'none (the run finished first); ') +
      `run shown as Succeeded after ${(heavy.totalMs / 1000).toFixed(1)} s (engine duration ${engineDuration}); long tasks: ${heavy.longTasks.length}` +
      (heavy.longTasks.length > 0 ? `, longest ${Math.max(...heavy.longTasks).toFixed(0)} ms` : ''),
  );

  console.log('200-step flowchart on the canvas (G-2):');
  console.log(`  Open to interactive (click Open → last step rendered): ${flowOpenMs} ms (target ≤ 1000)`);
  within &&= flowOpenMs <= 1000 * tolerance;
  for (const [name, times, target] of [
    ['Property keystroke to paint (a step selected)', flowKeystroke.times, 50],
    ['Moving a card (each pointer move to paint)', canvasGestures.move, 50],
    ['Drawing an arrow (each pointer move to paint)', canvasGestures.draw, 50],
  ]) {
    const p95 = percentile(times, 95);
    within &&= p95 <= target * tolerance;
    console.log(`  ${name}: p50 ${percentile(times, 50).toFixed(1)} ms, p95 ${p95.toFixed(1)} ms (target p95 ≤ ${target})`);
  }

  check(problems.length === 0, `browser errors: ${problems.join('; ')}`);
  check(within, 'a measurement is above its target');
  console.log(`All measurements are within the ADR-0021 targets${tolerance === 1 ? '' : ` (tolerance x${tolerance})`}.`);
});
