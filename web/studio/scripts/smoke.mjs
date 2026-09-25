// End-to-end smoke test of the W3 slice: Web Studio → MyRPA.Server → WorkflowLoader/ProjectStore → Execution.Hosting →
// engine → execution events and logs → SSE → Web Studio. Real server, real engine, real browser (headless Chromium).
//
// Needs: the server built (`dotnet build MyRPA.sln -c Release`), the Studio built (`npm run build`), `dotnet` on PATH,
// and Playwright's Chromium (installed once with the browser plugin's playwright.ps1; this script never downloads).
// It works on a throwaway copy of samples/hello-world.json, so it never edits the repository and can run repeatedly.

import { spawn } from 'node:child_process';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const studioDir = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = resolve(studioDir, '..', '..');
const configuration = process.env.MYRPA_CONFIGURATION ?? 'Release';
const serverDll = join(repo, 'src', 'MyRPA.Server', 'bin', configuration, 'net10.0', 'MyRPA.Server.dll');
const results = join(studioDir, 'test-results');
mkdirSync(results, { recursive: true });

const workspace = mkdtempSync(join(tmpdir(), 'myrpa-smoke-'));
const project = join(workspace, 'demo');
mkdirSync(project);
copyFileSync(join(repo, 'samples', 'hello-world.json'), join(project, 'hello-world.json'));
const original = JSON.parse(readFileSync(join(project, 'hello-world.json'), 'utf8'));

const check = (condition, message) => {
  if (!condition) {
    throw new Error(`Check failed: ${message}`);
  }
};
const step = (text) => console.log(`  ✓ ${text}`);

const server = spawn(process.env.MYRPA_DOTNET ?? 'dotnet', [serverDll, '--project', project, '--web', join(studioDir, 'dist'), '--port', '0'], {
  stdio: ['ignore', 'pipe', 'pipe'],
});
let serverOutput = '';
let browser;
let failed = false;
try {
  const startLink = await new Promise((resolveLink, reject) => {
    const timer = setTimeout(() => reject(new Error(`The server printed no start link:\n${serverOutput}`)), 30_000);
    server.stdout.on('data', (data) => {
      serverOutput += data;
      const match = /valid once\): (\S+)/.exec(serverOutput);
      if (match) {
        clearTimeout(timer);
        resolveLink(match[1]);
      }
    });
    server.stderr.on('data', (data) => (serverOutput += data));
    server.on('exit', (code) => reject(new Error(`The server exited (${code}):\n${serverOutput}`)));
  });
  console.log(`MyRPA.Server: ${startLink.replace(/token=.*/, 'token=…')}`);

  browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
  const problems = [];
  page.on('pageerror', (error) => problems.push(error.message));
  page.on('console', (message) => {
    // A refused run answers 422, which the browser logs as a failed resource; anything else is a real problem.
    if (message.type() === 'error' && !message.text().startsWith('Failed to load resource')) {
      problems.push(message.text());
    }
  });
  const streamConnections = [];
  page.on('request', (request) => {
    if (request.method() === 'GET' && /^\/api\/streams\/[^/]+$/.test(new URL(request.url()).pathname)) {
      streamConnections.push(request.url());
    }
  });

  const title = page.getByTestId('document-title');
  const runStatus = page.getByTestId('run-status');
  const message = page.getByLabel(/^message/);
  const click = (name) => page.getByRole('button', { name, exact: true }).click();

  await page.goto(startLink);
  await page.getByText('Connected to MyRPA.Server').waitFor();
  step('1-2. Server running; the Web Studio is served by it and signed in through the one-time start link');

  await page.getByRole('combobox', { name: 'Workflow' }).selectOption('hello-world.json');
  await click('Open');
  await title.filter({ hasText: /^hello-world\.json$/ }).waitFor();
  const items = page.getByRole('treeitem');
  const ids = [];
  for (let i = 0; i < (await items.count()); i++) {
    ids.push(await items.nth(i).getAttribute('data-node-id'));
  }
  check(ids.join(',') === 'main,build-greeting,log-greeting', `tree nodes ${ids}`);
  step(`3-4. Opened hello-world.json; the tree shows ${ids.join(', ')}`);

  await page.locator('[role=treeitem][data-node-id="log-greeting"] > .node').click();
  check((await page.getByTestId('node-type').textContent()) === 'Core.Log', 'selected node type');
  check((await message.inputValue()) === 'greeting', 'message property value');
  step('5. Selected log-greeting; Properties shows Core.Log and message = greeting');

  const edited = "greeting + ' (from the Web Studio)'";
  await message.fill(edited);
  await title.filter({ hasText: /•$/ }).waitFor();
  step(`6. Edited message to ${edited}; the document is dirty`);

  await click('Validate');
  await page.getByTestId('no-problems').waitFor();
  step('7. Validated by the server (WorkflowLoader): no problems');

  await click('Save');
  await title.filter({ hasText: /^hello-world\.json$/ }).waitFor();
  const savedText = readFileSync(join(project, 'hello-world.json'), 'utf8');
  const saved = JSON.parse(savedText);
  check(saved.root.children[1].properties.message === edited, 'saved message');
  check(JSON.stringify({ ...saved, root: undefined }) === JSON.stringify({ ...original, root: undefined }), 'other fields unchanged');
  check(!/"(key|_key|__key|clientKey)"/.test(savedText), 'no client keys in the file');
  step('8. Saved through the server with If-Match; the file has the edit, nothing else changed, no client keys');

  await click('Run');
  await runStatus.filter({ hasText: 'Succeeded' }).waitFor();
  const kinds = await page.locator('.events .kind').allTextContents();
  const logs = await page.locator('.events .log').allTextContents();
  check(kinds[0] === 'execution.started' && kinds.at(-1) === 'execution.completed', `event order ${kinds}`);
  check(kinds.includes('node.started') && kinds.includes('node.completed') && kinds.includes('log'), `event kinds ${kinds}`);
  check(logs.some((line) => line.includes('Hello, World! (from the Web Studio)')), `logs ${logs}`);
  await page.getByTestId('run-outputs').filter({ hasText: '"greeting":"Hello, World!"' }).waitFor();
  console.log(`    events: ${kinds.join(' → ')}`);
  console.log(`    log: ${logs.join(' | ').trim()}`);
  step('9-11. Ran through Execution.Hosting; SSE delivered execution.started, node events, the log and execution.completed; status Succeeded');
  await page.screenshot({ path: join(results, 'studio-run-succeeded.png') });

  // Error case: an expression syntax error. The server's WorkflowLoader finds it; the server refuses to run it.
  await message.fill('greeting +');
  await click('Validate');
  await page.locator('.field-error').filter({ hasText: 'MYRPA1043' }).waitFor();
  step('Error case: validation reports MYRPA1043 on the message property');
  await click('Run');
  await runStatus.filter({ hasText: 'NotStarted' }).waitFor();
  step('Error case: Run is refused (422) with the diagnostics; nothing started');
  await page.screenshot({ path: join(results, 'studio-validation-error.png') });

  // Back to a valid document, saved and run again: still the tab's single event stream.
  await message.fill('greeting');
  await click('Save');
  await title.filter({ hasText: /^hello-world\.json$/ }).waitFor();
  await click('Run');
  await runStatus.filter({ hasText: 'Succeeded' }).waitFor();
  check(streamConnections.length === 1, `SSE connections: ${streamConnections.length}`);
  step('Two successful runs used one SSE connection (one per tab, ADR-0024)');

  check(problems.length === 0, `browser errors: ${problems.join('; ')}`);
  step('No script errors or CSP violations in the browser');
  console.log(`Smoke test passed. Screenshots: ${results}`);
} catch (error) {
  failed = true;
  console.error(error);
} finally {
  await browser?.close();
  server.kill();
  await new Promise((done) => (server.exitCode !== null ? done() : server.once('exit', done)));
  rmSync(workspace, { recursive: true, force: true });
}

process.exit(failed ? 1 : 0);
