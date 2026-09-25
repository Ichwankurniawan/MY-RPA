// Measures the editing targets of ADR-0021 on the 3,000-node "flat" fixture of the W0 spike (a root Sequence with 300
// groups of one Sequence and 9 Logs = 3,001 nodes). Each sample is timed inside the page, from the command to the next
// painted frame, like the W0 measurements; the synchronous part (command, store update, React render and commit) is
// reported separately, since the rest is waiting for the browser's next frame. Real server, production build, headless Chromium. See harness.mjs.

import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { check, withStudio } from './harness.mjs';

const samples = Number(process.env.MYRPA_PERF_SAMPLES ?? 40);

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
  const nodes = await page.locator('[role=treeitem]').count();
  check(nodes === 3001, `fixture nodes: ${nodes}`);
  await page.locator('[role=treeitem][data-node-id="log-150-4"] > .node').click();

  // Runs `count` commands of one kind in the page and returns each one's time to the next painted frame (ms).
  const measure = (kind, count = samples) =>
    page.evaluate(
      async ({ kind, count }) => {
        const frame = () => new Promise((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0)));
        const key = (k, options) => window.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true, ...options }));
        const button = (name) => [...document.querySelectorAll('.editbar button')].find((b) => b.textContent === name);
        const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
        const times = [];
        const scripts = [];
        for (let i = 0; i < count; i++) {
          const start = performance.now();
          if (kind === 'keystroke') {
            const input = document.querySelector('.properties input.code');
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

  const results = [
    ['Property keystroke to paint', await measure('keystroke'), 50],
    ['Structural: move up/down', await measure('move'), 100],
    ['Undo', await measure('undo'), 50],
    ['Redo', await measure('redo'), 50],
    ['Structural: insert', await measure('insert'), 100],
    ['Structural: delete', await measure('delete'), 100],
  ];

  console.log(`3,001-node fixture, ${samples} samples per metric (production build, headless Chromium)`);
  console.log(`  Open to interactive (click Open → last node rendered, includes the file request): ${openMs} ms (target ≤ 1000)`);
  let within = openMs <= 1000;
  for (const [name, { times, scripts }, target] of results) {
    const p50 = percentile(times, 50);
    const p95 = percentile(times, 95);
    within &&= p95 <= target;
    console.log(
      `  ${name}: p50 ${p50.toFixed(1)} ms, p95 ${p95.toFixed(1)} ms (target p95 ≤ ${target}); ` +
        `script and render before the frame: p50 ${percentile(scripts, 50).toFixed(1)} ms, p95 ${percentile(scripts, 95).toFixed(1)} ms`,
    );
  }

  check(problems.length === 0, `browser errors: ${problems.join('; ')}`);
  check(within, 'a measurement is above its target');
  console.log('All measurements are within the ADR-0021 targets.');
});
