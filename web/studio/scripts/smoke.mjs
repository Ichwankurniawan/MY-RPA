// End-to-end smoke test: Web Studio → MyRPA.Server → WorkflowLoader/ProjectStore → Execution.Hosting → engine →
// execution events and logs → SSE → Web Studio, with the W4A structural editing flow. Real server, real engine, real
// browser (headless Chromium). It works on a throwaway copy of samples/hello-world.json, so it never edits the repository
// and can run repeatedly. Checks use roles, labels and data attributes, never pixels. See harness.mjs for prerequisites.

import { copyFileSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { check, repo, results, withStudio } from './harness.mjs';

const step = (text) => console.log(`  ✓ ${text}`);

await withStudio(async ({ project, page, startServer, problems }) => {
  const file = join(project, 'hello-world.json');
  copyFileSync(join(repo, 'samples', 'hello-world.json'), file);
  const original = JSON.parse(readFileSync(file, 'utf8'));
  const startLink = await startServer();
  console.log(`MyRPA.Server: ${startLink.replace(/token=.*/, 'token=…')}`);

  let streamConnections = [];
  page.on('request', (request) => {
    if (request.method() === 'GET' && /^\/api\/streams\/[^/]+$/.test(new URL(request.url()).pathname)) {
      streamConnections.push(request.url());
    }
  });

  const title = page.getByTestId('document-title');
  const runStatus = page.getByTestId('run-status');
  const message = page.getByLabel(/^message/);
  const click = (name) => page.getByRole('button', { name, exact: true }).click();
  const edit = (name) => page.getByRole('toolbar', { name: 'Edit' }).getByRole('button', { name, exact: true });
  const item = (id) => page.locator(`[role=treeitem][data-node-id="${id}"]`);
  const row = (id) => page.locator(`[role=treeitem][data-node-id="${id}"] > .node`);
  const treeIds = () => page.locator('[role=treeitem]').evaluateAll((items) => items.map((i) => i.dataset.nodeId).join(','));
  const selected = () => page.locator('[role=treeitem][aria-selected=true]').getAttribute('data-node-id');
  const expectTree = async (expected, what) => check((await treeIds()) === expected, `${what}: tree ${await treeIds()}`);
  const openHelloWorld = async () => {
    await page.getByText('Connected to MyRPA.Server').waitFor();
    await page.getByRole('combobox', { name: 'Workflow' }).selectOption('hello-world.json');
    await click('Open');
    await title.filter({ hasText: /^hello-world\.json$/ }).waitFor();
  };

  // W3: session, open, tree, selection, properties.
  await page.goto(startLink);
  await openHelloWorld();
  await expectTree('main,build-greeting,log-greeting', 'opened');
  step('1. Signed in through the start link; opened hello-world.json (main, build-greeting, log-greeting)');
  await row('log-greeting').click();
  check((await page.getByTestId('node-type').textContent()) === 'Core.Log', 'selected node type');
  check((await edit('Delete').isEnabled()) && !(await edit('Move down').isEnabled()), 'commands for the last child');

  // W4A: insert, move (keyboard), edit, undo, redo, delete, undo the delete.
  await page.getByRole('button', { name: 'Insert Log (Core.Log)' }).click();
  await expectTree('main,build-greeting,log-greeting,log-1', 'insert');
  check((await selected()) === 'log-1', 'the inserted node is selected');
  check((await message.inputValue()) === '', 'a new node has no property values');
  step('2. Inserted a Log (log-1) after log-greeting from the activity catalog; it is selected');

  await row('log-1').click();
  await page.keyboard.press('Alt+ArrowUp');
  await expectTree('main,build-greeting,log-1,log-greeting', 'move');
  check((await selected()) === 'log-1', 'the moved node stays selected');
  step('3. Moved log-1 up with Alt+Up (keyboard)');

  const inserted = "'Inserted by the Web Studio: ' + greeting";
  await message.fill(inserted);
  await title.filter({ hasText: /•$/ }).waitFor();
  step(`4. Edited log-1 message to ${inserted}`);

  await page.keyboard.press('Control+z');
  check((await message.inputValue()) === '', `undo the edit: ${await message.inputValue()}`);
  await expectTree('main,build-greeting,log-1,log-greeting', 'undo keeps the structure');
  step('5. Undo (Ctrl+Z) removed the property edit');

  await edit('Redo').click();
  check((await message.inputValue()) === inserted, 'redo the edit');
  step('6. Redo restored the property edit');

  await edit('Delete').click();
  await expectTree('main,build-greeting,log-greeting', 'delete');
  check((await selected()) === 'log-greeting', 'after delete the next sibling is selected');
  step('7. Deleted log-1; log-greeting is selected');

  await edit('Undo').click();
  await expectTree('main,build-greeting,log-1,log-greeting', 'undo delete');
  check((await selected()) === 'log-1' && (await message.inputValue()) === inserted, 'the restored node and its property');
  await page.screenshot({ path: join(results, 'studio-structural-edit.png') });
  step('8. Undo restored log-1 with its message and selection');

  await click('Validate');
  await page.getByTestId('no-problems').waitFor();
  step('9. Validated by the server (WorkflowLoader): no problems');

  await click('Save');
  await title.filter({ hasText: /^hello-world\.json$/ }).waitFor();
  const savedText = readFileSync(file, 'utf8');
  const saved = JSON.parse(savedText);
  check(saved.root.children.map((c) => c.id).join(',') === 'build-greeting,log-1,log-greeting', 'saved structure');
  check(JSON.stringify(saved.root.children[1]) === JSON.stringify({ id: 'log-1', type: 'Core.Log', properties: { message: inserted } }), 'saved node');
  check(JSON.stringify({ ...saved, root: undefined }) === JSON.stringify({ ...original, root: undefined }), 'other fields unchanged');
  check(!/"(key|_key|__key|clientKey)"/.test(savedText), 'no client keys in the file');
  step('10. Saved with If-Match; the file has the new node in place, nothing else changed, no client keys');

  await page.reload();
  streamConnections = [];
  await openHelloWorld();
  await expectTree('main,build-greeting,log-1,log-greeting', 'after reload');
  await row('log-1').click();
  check((await message.inputValue()) === inserted, 'reloaded property');
  check(!(await edit('Undo').isEnabled()), 'a reopened file starts a new history');
  step('11-12. Reloaded the page and reopened the file: same structure and property');

  await click('Run');
  await runStatus.filter({ hasText: 'Succeeded' }).waitFor();
  const kinds = await page.locator('.events .kind').allTextContents();
  const logs = (await page.locator('.events .log').allTextContents()).map((l) => l.trim());
  check(kinds[0] === 'execution.started' && kinds.at(-1) === 'execution.completed', `event order ${kinds}`);
  check(kinds.includes('node.started') && kinds.includes('node.completed'), `event kinds ${kinds}`);
  check(logs.join('|') === '[Information] Inserted by the Web Studio: Hello, World!|[Information] Hello, World!', `logs ${logs}`);
  check((await item('log-1').textContent()).includes('Succeeded'), 'per-node run state');
  console.log(`    events: ${kinds.join(' → ')}`);
  console.log(`    logs: ${logs.join(' | ')}`);
  await page.screenshot({ path: join(results, 'studio-run-succeeded.png') });
  step('13-14. Ran through Execution.Hosting; SSE delivered the events and both logs in order; status Succeeded');

  // Error case (W3): an expression syntax error found by the server; Run refuses; undo fixes it; runs again.
  await message.fill('greeting +');
  await click('Validate');
  await page.locator('.field-error').filter({ hasText: 'MYRPA1043' }).waitFor();
  await click('Run');
  await runStatus.filter({ hasText: 'NotStarted' }).waitFor();
  await page.screenshot({ path: join(results, 'studio-validation-error.png') });
  await page.keyboard.press('Control+z');
  check((await message.inputValue()) === inserted, 'undo the broken edit');
  await click('Run');
  await runStatus.filter({ hasText: 'Succeeded' }).waitFor();
  check(streamConnections.length === 1, `SSE connections after reload: ${streamConnections.length}`);
  step('Error case: MYRPA1043 on the property, Run refused (422); undo restored the saved version, which ran again; one SSE connection');

  check(problems.length === 0, `browser errors: ${problems.join('; ')}`);
  step('No script errors or CSP violations in the browser');
  console.log(`Smoke test passed. Screenshots: ${results}`);
});
