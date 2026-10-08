// End-to-end smoke test: Web Studio → MyRPA.Server → WorkflowLoader/ProjectStore → Execution.Hosting → engine →
// execution events and logs → SSE → Web Studio, with the W4A structural editing flow. Real server, real engine, real
// browser (headless Chromium). It works on a throwaway copy of samples/hello-world.json, so it never edits the repository
// and can run repeatedly. Checks use roles, labels and data attributes, never pixels. See harness.mjs for prerequisites.

import { copyFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { createServer as createNetServer } from 'node:net';
import { join } from 'node:path';
import { chromium } from 'playwright-core';
import { check, repo, results, withStudio } from './harness.mjs';

const step = (text) => console.log(`  ✓ ${text}`);

// W5 workflows, written into the throwaway project only.
const workflow = (id, root, args = []) => JSON.stringify({ schemaVersion: '1.0', id, name: id, version: '1.0.0', arguments: args, root }, null, 2);
const log = (id, text) => ({ id, type: 'Core.Log', properties: { message: `'${text}'` } });
const delay = (id, ms) => ({ id, type: 'Core.Delay', properties: { milliseconds: String(ms) } });
const sequence = (...children) => ({ id: 'main', type: 'Core.Sequence', children });
const w5Workflows = {
  'greet.json': workflow(
    'greet',
    sequence(
      { id: 'set-greeting', type: 'Core.Assign', properties: { to: 'greeting', value: "'Hi ' + who" } },
      { id: 'set-doubled', type: 'Core.Assign', properties: { to: 'doubled', value: 'times * 2' } },
    ),
    [
      { name: 'who', direction: 'In', type: 'String', required: true },
      { name: 'times', direction: 'In', type: 'Int', default: 1 },
      { name: 'greeting', direction: 'Out', type: 'String' },
      { name: 'doubled', direction: 'Out', type: 'Int' },
    ],
  ),
  'wait.json': workflow('wait', sequence(log('before', 'before the wait'), delay('wait', 1500), log('after', 'after the wait'))),
  'fail.json': workflow('fail', sequence(log('start', 'starting'), { id: 'boom', type: 'Core.Throw', properties: { message: "'Planned failure'" } }, log('never', 'not reached'))),
  'long.json': workflow('long', sequence(log('begin', 'long run begins'), delay('long-wait', 60000))),
  'resume.json': workflow('resume', sequence(log('a', 'first'), delay('pause', 5000), log('b', 'second'))),
};

await withStudio(async ({ project, page, startServer, problems }) => {
  const file = join(project, 'hello-world.json');
  copyFileSync(join(repo, 'samples', 'hello-world.json'), file);
  for (const [name, text] of Object.entries(w5Workflows)) {
    writeFileSync(join(project, name), text);
  }

  const original = JSON.parse(readFileSync(file, 'utf8'));
  const startLink = await startServer();
  console.log(`MyRPA.Server: ${startLink.replace(/token=.*/, 'token=…')}`);

  let streamConnections = [];
  let runRequests = 0;
  page.on('request', (request) => {
    const path = new URL(request.url()).pathname;
    if (request.method() === 'GET' && /^\/api\/streams\/[^/]+$/.test(path)) {
      streamConnections.push(request.url());
    } else if (request.method() === 'POST' && path === '/api/runs') {
      runRequests++;
    }
  });

  const title = page.getByTestId('document-title');
  const runStatus = page.getByTestId('run-status');
  // UX-3: the selected card has the same editors inline; the Properties panel is the one this script types into.
  const message = page.getByRole('complementary', { name: 'Properties' }).getByLabel(/^message/);
  const click = (name) => page.getByRole('button', { name, exact: true }).click();
  const edit = (name) => page.getByRole('toolbar', { name: 'Edit' }).getByRole('button', { name, exact: true });
  const item = (id) => page.locator(`[role=treeitem][data-node-id="${id}"]`);
  const row = (id) => page.locator(`[role=treeitem][data-node-id="${id}"] > .node`);
  const treeIds = () => page.locator('[role=treeitem][data-node-id]').evaluateAll((items) => items.map((i) => i.dataset.nodeId).join(','));
  const selected = () => page.locator('[role=treeitem][data-node-id][aria-selected=true]').getAttribute('data-node-id');
  const expectTree = async (expected, what) => check((await treeIds()) === expected, `${what}: tree ${await treeIds()}`);
  const openWorkflow = async (name) => {
    await page.getByRole('combobox', { name: 'Workflow' }).selectOption(name);
    await click('Open');
    await title.filter({ hasText: new RegExp(`^${name.replace('.', '\\.')}$`) }).waitFor();
  };
  const openHelloWorld = async () => {
    // After a page load, wait for the Studio to have connected (the workflow list is loaded by then).
    if ((await page.getByRole('combobox', { name: 'Workflow' }).locator('option', { hasText: 'hello-world.json' }).count()) === 0) {
      await page.getByText('Connected to MyRPA.Server').waitFor();
    }

    await openWorkflow('hello-world.json');
  };
  const dialog = page.getByRole('dialog');
  // hello-world declares userName (In, default "World"): Run asks for it; Start with the field blank keeps the default.
  const runWithDefaults = async () => {
    await click('Run');
    await dialog.waitFor();
    await dialog.getByRole('button', { name: 'Start', exact: true }).click();
  };
  const toolbarButton = (name) => page.getByRole('banner').getByRole('button', { name, exact: true });
  const eventSequences = () => page.locator('.events .seq').allTextContents();

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

  await runWithDefaults();
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
  // Error case (W3, now validate-before-run, W5): the server finds the syntax error and the run is never requested.
  await message.fill('greeting +');
  await click('Validate');
  await page.getByRole('complementary', { name: 'Properties' }).locator('.field-error').filter({ hasText: 'MYRPA1043' }).waitFor();
  const requestsBefore = runRequests;
  await runWithDefaults();
  // UX-2: the failed check shows Problems; the toolbar and the Execution tab say the run did not start.
  await page.getByRole('tab', { name: /^Problems/, selected: true }).waitFor();
  await page.getByTestId('toolbar-run-status').filter({ hasText: 'Not started — validation failed' }).waitFor();
  await page.getByRole('tab', { name: 'Execution' }).click();
  await runStatus.filter({ hasText: 'Not started — validation failed' }).waitFor();
  check(runRequests === requestsBefore, `an invalid workflow made ${runRequests - requestsBefore} run request(s)`);
  await page.screenshot({ path: join(results, 'studio-validation-error.png') });
  await page.keyboard.press('Control+z');
  check((await message.inputValue()) === inserted, 'undo the broken edit');
  await runWithDefaults();
  await runStatus.filter({ hasText: 'Succeeded' }).waitFor();
  check(streamConnections.length === 1, `SSE connections after reload: ${streamConnections.length}`);
  step('Error case: MYRPA1043 on the property; Run validated first and never requested the run (Not started — validation failed); undo fixed it and it ran again; one SSE connection');

  // W5-1: run configuration. A required argument blocks Start; the text is parsed by the server like --arg.
  await openWorkflow('greet.json');
  await click('Run');
  await dialog.waitFor();
  const start = dialog.getByRole('button', { name: 'Start', exact: true });
  check(await start.isDisabled(), 'Start is disabled while the required argument is blank');
  check((await dialog.getByText('Enter the required argument(s): who.').count()) === 1, 'the reason is shown');
  check((await dialog.getByLabel(/^times/).getAttribute('placeholder')) === 'default: 1', 'the default is shown');
  await dialog.getByLabel(/^who/).fill('Ada');
  await dialog.getByLabel(/^times/).fill('3');
  await start.click();
  await runStatus.filter({ hasText: 'Succeeded' }).waitFor();
  const outputs = await page.getByTestId('run-outputs').textContent();
  const correlation = await page.getByTestId('run-correlation-id').textContent();
  check((await runStatus.textContent()).includes(correlation) && (await page.getByTestId('run-execution-id').textContent()).length > 0, 'execution and correlation ids shown');
  check(outputs.includes('"greeting":"Hi Ada"') && outputs.includes('"doubled":6'), `outputs ${outputs}`);
  step(`W5-1. Run dialog: required "who" blocked Start; who=Ada, times=3 (text) → Succeeded, ${outputs}`);

  // W5-2: live view of a running workflow: status, start time, current node, node badges, logs.
  await openWorkflow('wait.json');
  await click('Run'); // no input arguments: no dialog
  await page.getByTestId('run-current').filter({ hasText: /^wait$/ }).waitFor();
  check((await runStatus.textContent()).includes('Running'), `status while running: ${await runStatus.textContent()}`);
  check((await page.getByTestId('toolbar-run-status').textContent()) === 'Status: Running', 'toolbar status');
  check((await page.getByTestId('run-started').textContent()).length > 0, 'start time shown');
  check((await row('wait').getAttribute('data-run-status')) === 'Running', 'the running node is marked');
  check((await row('after').getAttribute('data-run-status')) === null, 'a node not executed yet has no run state');
  await page.screenshot({ path: join(results, 'studio-running.png') });
  await runStatus.filter({ hasText: 'Succeeded' }).waitFor();
  check((await row('after').getAttribute('data-run-status')) === 'Succeeded', 'node state after the run');
  const waitLogs = (await page.locator('.events .log').allTextContents()).map((l) => l.trim());
  check(waitLogs.join('|') === '[Information] before the wait|[Information] after the wait', `logs ${waitLogs}`);
  step('W5-2. Running view: Running, current node "wait", start time, running/not-executed node states, then Succeeded with both logs');

  // W5-3: failure: status, failed node, navigation, still editable.
  await openWorkflow('fail.json');
  await click('Run');
  await runStatus.filter({ hasText: 'Failed' }).waitFor();
  const failure = await page.getByTestId('run-error').textContent();
  check(failure.includes('at boom (Core.Throw') && failure.includes('Planned failure'), `failure ${failure}`);
  check((await row('boom').getAttribute('data-run-status')) === 'Failed' && (await row('never').getAttribute('data-run-status')) === null, 'failed and not executed nodes');
  await click('Select failed node');
  check((await selected()) === 'boom', `selected after "Select failed node": ${await selected()}`);
  await message.fill("'Changed after the failure'");
  await title.filter({ hasText: /•$/ }).waitFor();
  await page.screenshot({ path: join(results, 'studio-run-failed.png') });
  await click('Clear log');
  check((await page.locator('.events li').count()) === 0 && (await runStatus.textContent()).includes('Failed'), 'Clear log empties the list, the run stays Failed');
  await page.keyboard.press('Control+z');
  await title.filter({ hasText: /^fail\.json$/ }).waitFor();
  step(`W5-3. Failure: Failed, ${failure.replace(' Select failed node', '').trim()}; Select failed node selected "boom"; the workflow stayed editable`);

  // W5-4: cancellation. Cancelling… until the server reports Cancelled (seen through a DOM observer).
  await openWorkflow('long.json');
  await click('Run');
  await page.getByTestId('run-current').filter({ hasText: /^long-wait$/ }).waitFor();
  await page.evaluate(() => {
    window.__statuses = [];
    const target = document.querySelector('[data-testid="run-status"]');
    new MutationObserver(() => window.__statuses.push(target.textContent)).observe(target, { subtree: true, childList: true, characterData: true });
  });
  await toolbarButton('Stop').click();
  await runStatus.filter({ hasText: 'Cancelled' }).waitFor();
  const statuses = await page.evaluate(() => window.__statuses);
  check(statuses.some((s) => s.includes('Cancelling…')), `statuses seen: ${statuses.join(' → ')}`);
  check(await toolbarButton('Stop').isDisabled(), 'Stop is disabled after the run finished');
  check((await row('long-wait').getAttribute('data-run-status')) === 'Cancelled', 'the cancelled node');
  step('W5-4. Stop: Cancelling… shown until the server reported Cancelled; the node shows Cancelled; Stop disabled afterwards');

  // W5-5: two runs at once on the one stream; each keeps its own status and logs.
  await click('Run');
  await page.getByTestId('run-current').filter({ hasText: /^long-wait$/ }).waitFor();
  const longRun = (await runStatus.textContent()).match(/run (\S+)/)[1];
  await openHelloWorld();
  await runWithDefaults();
  await runStatus.filter({ hasText: 'Succeeded' }).waitFor();
  const helloLogs = (await page.locator('.events .log').allTextContents()).join('|');
  check(!helloLogs.includes('long run begins'), `logs mixed: ${helloLogs}`);
  const recent = page.getByRole('combobox', { name: /Recent runs/ });
  const longOption = await recent.locator('option').evaluateAll((options, id) => options.find((o) => o.textContent.includes('long.json') && o.textContent.includes('Running'))?.value, longRun);
  check(longOption !== undefined, 'the long run is listed as running');
  await recent.selectOption(longOption);
  await runStatus.filter({ hasText: longRun }).waitFor();
  check((await runStatus.textContent()).includes('Running'), 'the long run is still running');
  const longLogs = (await page.locator('.events .log').allTextContents()).map((l) => l.trim()).join('|');
  check(longLogs === '[Information] long run begins', `long run logs: ${longLogs}`);
  check(streamConnections.length === 1, `SSE connections: ${streamConnections.length}`);
  await toolbarButton('Stop').click();
  await runStatus.filter({ hasText: 'Cancelled' }).waitFor();
  step(`W5-5. Concurrent runs: hello-world Succeeded while ${longRun} kept running; separate logs and status; one SSE connection; then stopped`);

  // W5-6: the stream disappears mid-run (deleted on the server); the Studio re-creates it and resumes after the last
  // sequence it saw: no duplicates, nothing lost. (Resume with Last-Event-ID is covered by the server tests.)
  await openWorkflow('resume.json');
  await click('Run');
  await page.getByTestId('run-current').filter({ hasText: /^pause$/ }).waitFor();
  const streamUrl = new URL(streamConnections.at(-1));
  const deleted = await page.evaluate(async (path) => (await fetch(path, { method: 'DELETE', headers: { 'X-MyRPA-Request': '1' } })).status, streamUrl.pathname);
  check(deleted === 204, `stream delete: ${deleted}`);
  await runStatus.filter({ hasText: 'Succeeded' }).waitFor({ timeout: 20_000 });
  const sequences = (await eventSequences()).map(Number);
  check(sequences.join(',') === sequences.map((_, i) => i + 1).join(','), `sequences ${sequences}`);
  const resumeLogs = (await page.locator('.events .log').allTextContents()).map((l) => l.trim()).join('|');
  check(resumeLogs === '[Information] first|[Information] second', `logs ${resumeLogs}`);
  check(streamConnections.length >= 2 && new URL(streamConnections.at(-1)).pathname !== streamUrl.pathname, 'a new stream was created');
  check((await page.getByTestId('stream-reconnecting').count()) === 0, 'the reconnecting notice is gone');
  step(`W5-6. Stream lost mid-run: re-created and resumed; sequences 1..${sequences.length} without duplicates or loss; both logs`);

  const filesTree = page.getByRole('tree', { name: 'Workflow files' });
  const fileRow = (path) => filesTree.locator(`[data-path="${path}"]`);
  const dialogButton = (name) => dialog.getByRole('button', { name, exact: true });
  const titleIs = (text) => title.filter({ hasText: new RegExp(`^${text.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&')}$`) }).waitFor();
  const onDisk = (path) => readFileSync(join(project, ...path.split('/')), 'utf8');
  const exists = (path) => existsSync(join(project, ...path.split('/')));
  const properties = page.getByRole('complementary', { name: 'Properties' });

  // W4B: rich authoring, the PRD 5.5 "Greeter" flow within W4B scope (inserting into slots is W7).
  await page.getByRole('button', { name: 'New…', exact: true }).click();
  await dialog.getByLabel('Path in the project').fill('greeter.json');
  await dialogButton('Create').click();
  await titleIs('greeter.json');
  await page.getByRole('navigation', { name: 'Selection' }).getByRole('button', { name: 'Workflow', exact: true }).click();
  await properties.getByLabel('Name', { exact: true }).fill('Greeter');
  await page.getByRole('tab', { name: /^Arguments/ }).click();
  await click('Add argument');
  await page.getByLabel('argument 1 name').fill('who');
  await page.getByLabel('argument 1 default').fill('"World"');
  await page.getByRole('tab', { name: /^Variables/ }).click();
  await click('Add variable');
  await page.getByLabel('variable 1 name').fill('message');
  await row('main').click();
  await page.getByRole('button', { name: 'Insert Assign (Core.Assign)' }).click();
  await properties.getByLabel(/^to/).fill('message');
  await properties.getByLabel(/^value/).fill("'Hello, ' + who");
  await page.getByRole('button', { name: 'Insert Log (Core.Log)' }).click();
  await properties.getByLabel(/^message/).fill('message');
  await page.getByRole('tab', { name: /^Problems/ }).click();
  await page.getByTestId('no-problems').waitFor(); // live validation (no Validate pressed) found no problems
  await page.screenshot({ path: join(results, 'studio-authoring.png') });
  step('W4B-1. Authored Greeter: metadata, argument who = "World", variable message, Assign and Log; live validation: no problems');

  // Live expression feedback: a syntax error appears on the property without pressing Validate; Undo fixes it.
  // (Selecting another node ends the typing group, so the broken edit is an undo step of its own.)
  await row('assign-1').click();
  await row('log-1').click();
  await properties.getByLabel(/^message/).fill('message +');
  await properties.locator('.field-error').filter({ hasText: 'MYRPA1043' }).waitFor();
  await page.keyboard.press('Control+z');
  await page.getByTestId('no-problems').waitFor();
  await page.keyboard.press('Control+z');
  check((await properties.getByLabel(/^message/).inputValue()) === '', 'undo removed the Log message');
  await page.keyboard.press('Control+y');
  check((await properties.getByLabel(/^message/).inputValue()) === 'message', 'redo restored it');
  step('W4B-2. Live validation showed MYRPA1043 on the property while typing; Undo/Redo of authoring edits');

  await click('Save');
  await titleIs('greeter.json');
  const greeter = JSON.parse(onDisk('greeter.json'));
  check(greeter.name === 'Greeter' && greeter.arguments[0].default === 'World' && greeter.variables[0].name === 'message', `saved ${JSON.stringify(greeter)}`);
  check(greeter.root.children.map((c) => c.type).join(',') === 'Core.Assign,Core.Log', 'saved activities');
  await page.reload();
  await page.getByText('Connected to MyRPA.Server').waitFor();
  await fileRow('greeter.json').dblclick();
  await titleIs('greeter.json');
  await runWithDefaults();
  await runStatus.filter({ hasText: 'Succeeded' }).waitFor();
  const greeterLogs = (await page.locator('.events .log').allTextContents()).map((l) => l.trim());
  check(greeterLogs.join('|') === '[Information] Hello, World', `logs ${greeterLogs}`);
  step('W4B-3. Saved, reloaded and reopened (all preserved); ran with the default argument: "Hello, World"');

  // W7: parity authoring on the Greeter workflow: insert into slots, drag-and-drop (real pointer hit-testing),
  // copy/paste with id renaming, cut. Every change is checked in the document, then saved.
  const editButton = (name) => page.getByRole('toolbar', { name: 'Edit' }).getByRole('button', { name, exact: true });
  const zone = (id, name) => row(id).getByRole('button', { name, exact: true });
  // `at` is where on the target to drop, as a fraction of its height (a card's lower half means "after it").
  const dragTo = async (from, to, at = 0.5) => {
    // Let live validation finish first: an error appearing in a card while dragging would move the target.
    if ((await page.getByTestId('status-validation').count()) > 0) {
      await page.getByTestId('status-validation').filter({ hasNotText: 'Checking' }).waitFor();
    }
    await from.scrollIntoViewIfNeeded();
    // UX-3: a selected card shows its editors, so a drop target inside it can be below the visible area.
    await to.scrollIntoViewIfNeeded();
    const a = await from.boundingBox();
    const b = await to.boundingBox();
    await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2);
    await page.mouse.down();
    await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2 + 10, { steps: 3 });
    await page.mouse.move(b.x + b.width / 2, b.y + b.height * at, { steps: 8 });
    // Follow the target if a card changed while dragging (as in manual.mjs).
    const settled = await to.boundingBox();
    await page.mouse.move(settled.x + settled.width / 2, settled.y + settled.height * at, { steps: 2 });
    await page.mouse.up();
  };
  await row('log-1').click();
  await page.getByRole('button', { name: 'Insert If (Core.If)' }).click();
  await expectTree('main,assign-1,log-1,if-1', 'If inserted after the Log');
  await zone('if-1', 'then: empty (required)').click();
  await page.getByRole('button', { name: 'Insert Log (Core.Log)' }).click();
  await expectTree('main,assign-1,log-1,if-1,log-2', 'Log inserted into then');
  check((await page.locator('[role=treeitem][data-node-id="log-2"] .slot').first().textContent()) === 'then:', 'log-2 is in the then slot');
  step('W7-1. Insert into slots: If after the Log, then a Log into its required then slot through the slot zone');

  await dragTo(row('assign-1'), zone('if-1', 'else: empty'));
  await expectTree('main,log-1,if-1,log-2,assign-1', 'Assign dragged into else');
  await dragTo(row('log-1'), row('if-1'), 0.8);
  await expectTree('main,if-1,log-2,assign-1,log-1', 'Log dragged below the If');
  step('W7-2. Drag-and-drop: Assign into the If\'s else slot (across containers), the Log below the If (real pointer hit-testing)');

  await row('log-1').click();
  await editButton('Copy').click();
  await editButton('Paste').click();
  await expectTree('main,if-1,log-2,assign-1,log-1,log-3', 'pasted copy with a new id');
  await editButton('Cut').click();
  await expectTree('main,if-1,log-2,assign-1,log-1', 'cut removed the copy');
  await page.keyboard.press('Control+z');
  await expectTree('main,if-1,log-2,assign-1,log-1,log-3', 'undo restored the cut copy');
  await page.keyboard.press('Control+y');
  await expectTree('main,if-1,log-2,assign-1,log-1', 'redo cut it again');
  step('W7-3. Copy/paste (the copy got id log-3), cut, and undo/redo of each');

  await row('if-1').click();
  await properties.getByLabel(/^condition/).fill('len(who) > 0');
  await row('log-2').click();
  await properties.getByLabel(/^message/).fill("'Hello from then'");
  await click('Save');
  await titleIs('greeter.json');
  const parity = JSON.parse(onDisk('greeter.json'));
  check(parity.root.children.map((c) => c.id).join(',') === 'if-1,log-1' && parity.root.children[0].slots.then.id === 'log-2' && parity.root.children[0].slots.else.id === 'assign-1', `saved ${JSON.stringify(parity.root)}`);
  await runWithDefaults();
  await runStatus.filter({ hasText: 'Succeeded' }).waitFor();
  step('W7-4. Saved the nested workflow (If with then/else slots) exactly as authored; it runs');

  // W6: project and file management. All through the Files panel and in-app dialogs, against the real file system.

  // W6-1: New… creates a valid workflow (in a new folder) and opens it.
  await page.getByRole('button', { name: 'New…', exact: true }).click();
  await dialog.getByLabel('Path in the project').fill('flows/w6-created.json');
  await dialogButton('Create').click();
  await titleIs('flows/w6-created.json');
  const created = JSON.parse(onDisk('flows/w6-created.json'));
  check(created.id === 'w6-created' && created.root?.type === 'Core.Sequence', `created ${JSON.stringify(created)}`);
  await click('Validate');
  await page.getByTestId('no-problems').waitFor();
  step('W6-1. New…: created flows/w6-created.json (a valid workflow: the server finds no problems) and opened it');

  // W6-2: Rename (F2 in the Files panel) moves the file on disk; the open document follows it.
  await fileRow('flows/w6-created.json').click();
  await page.keyboard.press('F2');
  await dialog.getByLabel('Path in the project').fill('flows/w6-renamed.json');
  await dialogButton('Rename').click();
  await titleIs('flows/w6-renamed.json');
  check(exists('flows/w6-renamed.json') && !exists('flows/w6-created.json'), 'renamed on disk');
  step('W6-2. Rename (F2): flows/w6-created.json → flows/w6-renamed.json on disk; the open document followed');

  // W6-3: Unsaved changes: the Studio asks in its own dialog; Cancel stays, Save saves and then opens the other file.
  await page.getByLabel('Display name').fill('Renamed workflow root');
  await titleIs('flows/w6-renamed.json •');
  await fileRow('hello-world.json').dblclick();
  await page.getByRole('dialog', { name: 'Unsaved changes' }).waitFor();
  await dialogButton('Cancel').click();
  await titleIs('flows/w6-renamed.json •');
  await fileRow('hello-world.json').dblclick();
  await dialogButton('Save').click();
  await titleIs('hello-world.json');
  check(JSON.parse(onDisk('flows/w6-renamed.json')).root.displayName === 'Renamed workflow root', 'saved before opening the other file');
  step('W6-3. Unsaved changes: in-app dialog (no window.confirm); Cancel stayed, Save saved the file, then hello-world.json opened');

  // W6-4: A save conflict (the file changed on disk meanwhile): Overwrite with mine.
  await row('log-greeting').click();
  await message.fill("'mine'");
  const theirs = JSON.parse(onDisk('hello-world.json'));
  writeFileSync(join(project, 'hello-world.json'), JSON.stringify({ ...theirs, version: '9.9.9' }, null, 2));
  await click('Save');
  await page.getByRole('dialog', { name: 'The file changed on disk' }).waitFor();
  await page.screenshot({ path: join(results, 'studio-save-conflict.png') });
  await dialogButton('Overwrite with mine').click();
  await titleIs('hello-world.json');
  check(onDisk('hello-world.json').includes("'mine'"), 'overwritten with this version');
  step('W6-4. Save conflict (file changed on disk): dialog offered Reload / Overwrite / Save as; Overwrite wrote this version');

  // W6-5: Save as… writes a new file, which becomes the open file; the original is unchanged.
  const original5 = onDisk('hello-world.json');
  await message.fill("'only in the copy'");
  await click('Save as…');
  await dialog.getByLabel('Path in the project').fill('copy-of-hello.json');
  await dialogButton('Save').click();
  await titleIs('copy-of-hello.json');
  check(onDisk('copy-of-hello.json').includes("'only in the copy'") && onDisk('hello-world.json') === original5, 'save as kept the original');
  step('W6-5. Save as…: copy-of-hello.json has the edit and is open; hello-world.json is unchanged');

  // W6-6: Crash recovery: unsaved edits survive a reload of the page and are offered back.
  await row('log-greeting').click();
  await message.fill("'recover me'");
  await titleIs('copy-of-hello.json •');
  await page.waitForTimeout(1500); // the draft is written once typing pauses (1 s)
  page.once('dialog', (beforeUnload) => beforeUnload.accept());
  await page.reload();
  await page.getByText('Connected to MyRPA.Server').waitFor();
  await fileRow('copy-of-hello.json').dblclick();
  await page.getByRole('dialog', { name: 'Recover unsaved changes' }).waitFor();
  await dialogButton('Restore').click();
  await titleIs('copy-of-hello.json •');
  await row('log-greeting').click();
  check((await message.inputValue()) === "'recover me'", `recovered ${await message.inputValue()}`);
  await click('Save');
  await titleIs('copy-of-hello.json');
  step('W6-6. Crash recovery: after a page reload, the unsaved edit was offered back, restored and saved');

  // W6-7: Delete (Delete key in the Files panel) after confirmation.
  await fileRow('flows/w6-renamed.json').click();
  await page.keyboard.press('Delete');
  await page.getByRole('dialog', { name: 'Delete flows/w6-renamed.json' }).waitFor();
  await dialogButton('Delete').click();
  await fileRow('flows/w6-renamed.json').waitFor({ state: 'detached' });
  check(!exists('flows/w6-renamed.json'), 'deleted on disk');
  step('W6-7. Delete (Delete key): confirmed in the Studio; the file is gone from disk and from the Files panel');

  // UX-3: the card designer in Chromium: the selected card's own editor, collapse, and CSS zoom (jsdom has no zoom).
  await openHelloWorld();
  await row('log-greeting').click();
  const inlineMessage = row('log-greeting').getByLabel(/^message/);
  check((await inlineMessage.inputValue()) === (await message.inputValue()), 'the card and the Properties panel show the same value');
  check((await row('build-greeting').locator('.summary').textContent()).startsWith('to: greeting'), 'unselected cards show a summary');
  await page.getByRole('button', { name: 'Zoom in', exact: true }).click();
  const zoom = await page.getByRole('tree', { name: 'Workflow', exact: true }).evaluate((tree) => getComputedStyle(tree).zoom);
  check(zoom === '1.1', `computed zoom ${zoom}`);
  await page.getByRole('button', { name: /, reset zoom$/ }).click();
  const before = await treeIds();
  await page.getByRole('button', { name: 'Collapse all', exact: true }).click();
  await page.getByRole('button', { name: 'Expand all', exact: true }).click();
  check((await treeIds()) === before, `expanded again: ${await treeIds()} (was ${before})`);
  step('UX-3. Cards: the selected card edits inline (same value as Properties), summaries, CSS zoom 110% in Chromium, collapse and expand all');

  // G-2: a flowchart built from scratch in Chromium, by pointer (toolbox drops on the canvas, a connection drawn from a
  // step's handle, a step moved) and by keyboard (Properties: transitions); saved as 1.1, run, the arrow taken shown.
  writeFileSync(join(project, 'g2.json'), `${JSON.stringify({ schemaVersion: '1.0', id: 'g2', name: 'G2', version: '1.0.0', variables: [{ name: 'n', type: 'Int', default: 0 }], root: { id: 'main', type: 'Core.Sequence' } }, null, 2)}\n`);
  await page.reload();
  await page.getByText('Connected to MyRPA.Server').waitFor();
  await openWorkflow('g2.json');
  const panel = page.getByRole('complementary', { name: 'Properties' });
  const canvas = page.locator('.flow-canvas');
  const stepCard = (id) => page.locator(`.flow-step[data-node-id="${id}"] > .node`);
  const center = async (locator) => {
    const box = await locator.boundingBox();
    return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
  };
  const pointerDrag = async (from, to) => {
    await page.mouse.move(from.x, from.y);
    await page.mouse.down();
    await page.mouse.move(from.x + 12, from.y + 12, { steps: 3 });
    await page.mouse.move(to.x, to.y, { steps: 10 });
    await page.mouse.up();
  };
  await row('main').click();
  await page.getByRole('button', { name: 'Insert Flowchart (Core.Flowchart)' }).click();
  await page.getByRole('button', { name: 'Empty flowchart: insert the start step here' }).click();
  await page.getByRole('button', { name: 'Insert Assign (Core.Assign)' }).click();
  await panel.getByLabel(/^to/).fill('n');
  await panel.getByLabel(/^value/).fill('n + 1');
  check((await canvas.count()) === 1 && (await page.locator('.flow-step').count()) === 1, 'the flowchart shows its start step on a canvas');
  await canvas.scrollIntoViewIfNeeded();
  // A toolbox entry dropped on the canvas becomes a step where it was dropped.
  const dropOnCanvas = async (name, dx, dy) => {
    const entry = page.getByRole('button', { name, exact: true });
    await entry.scrollIntoViewIfNeeded();
    await canvas.scrollIntoViewIfNeeded();
    const box = await canvas.boundingBox();
    await pointerDrag(await center(entry), { x: box.x + dx, y: box.y + dy });
  };
  await dropOnCanvas('Insert Decision (Core.Decision)', 150, 260);
  await stepCard('decision-1').waitFor({ timeout: 5000 }).catch(async (error) => {
    await page.screenshot({ path: join(results, 'g2-drop-failed.png') });
    throw new Error(`${error.message}; status: ${await page.locator('.segment.message').textContent().catch(() => '?')}`);
  });
  await dropOnCanvas('Insert Log (Core.Log)', 430, 260);
  await stepCard('log-1').waitFor({ timeout: 5000 });
  await panel.getByLabel(/^message/).fill("'done after ' + n");
  // Pointer: an arrow from assign-1 to decision-1, drawn from assign-1's handle.
  await pointerDrag(await center(page.locator('.flow-step[data-node-id="assign-1"] .connect-handle')), await center(stepCard('decision-1')));
  await page.locator('.flow-arrows g.arrow').first().waitFor({ state: 'attached', timeout: 5000 }).catch(async (error) => {
    await page.screenshot({ path: join(results, 'g2-connect-failed.png') });
    throw new Error(`${error.message}; status: ${await page.locator('.segment.message').textContent().catch(() => '?')}`);
  });
  // Keyboard way: Properties of decision-1, two transitions (the first with a condition).
  await stepCard('decision-1').click();
  if ((await selected()) !== 'decision-1') {
    await page.screenshot({ path: join(results, 'g2-select-failed.png') });
    const hit = await page.evaluate(async (box) => document.elementFromPoint(box.x, box.y)?.outerHTML.slice(0, 200), await center(stepCard('decision-1')));
    throw new Error(`Check failed: clicked decision-1 but ${await selected()} is selected; element at its centre: ${hit}`);
  }
  await panel.getByLabel('Add a transition to').selectOption('assign-1');
  await panel.getByRole('button', { name: 'Add transition', exact: true }).click();
  await panel.getByRole('group', { name: /^Transition 1/ }).getByLabel('Condition').fill('n < 3');
  await panel.getByLabel('Add a transition to').selectOption('log-1');
  await panel.getByRole('button', { name: 'Add transition', exact: true }).click();
  check((await page.locator('.flow-arrows g.arrow').count()) === 3, `three arrows: ${await page.locator('.flow-arrows g.arrow').count()}`);
  // Pointer: move log-1 down by 160 px.
  const logBefore = await stepCard('log-1').boundingBox();
  await pointerDrag(await center(stepCard('log-1')), { x: logBefore.x + logBefore.width / 2, y: logBefore.y + logBefore.height / 2 + 160 });
  const logAfter = await stepCard('log-1').boundingBox();
  check(Math.abs(logAfter.y - logBefore.y - 160) < 2, `log-1 moved ${logAfter.y - logBefore.y} px`);
  await page.getByTestId('status-validation').filter({ hasNotText: 'Checking' }).waitFor();
  await click('Validate');
  await page.getByTestId('status-validation').filter({ hasText: /No problems/ }).waitFor();
  await page.keyboard.press('Control+s');
  await title.filter({ hasText: /^g2\.json$/ }).waitFor();
  const g2Saved = JSON.parse(readFileSync(join(project, 'g2.json'), 'utf8'));
  const flow = g2Saved.root.children[0];
  check(g2Saved.schemaVersion === '1.1', `saved schema ${g2Saved.schemaVersion}`);
  check(JSON.stringify(flow.children.map((c) => [c.id, c.transitions ?? []])) === JSON.stringify([['assign-1', [{ to: 'decision-1' }]], ['decision-1', [{ to: 'assign-1', when: 'n < 3' }, { to: 'log-1' }]], ['log-1', []]]), `saved transitions ${JSON.stringify(flow.children)}`);
  check(flow.children.every((c) => Number.isInteger(c.layout?.x ?? 0)) && typeof flow.children[2].layout?.y === 'number', 'positions saved as whole numbers');
  await click('Run');
  await runStatus.filter({ hasText: 'Succeeded' }).waitFor();
  const taken = await page.locator('.flow-arrows g.arrow.taken').evaluateAll((gs) => gs.map((g) => g.dataset.arrow));
  const decisionKey = await page.locator('.flow-step[data-node-id="decision-1"]').getAttribute('data-key');
  check(JSON.stringify(taken) === JSON.stringify([`${decisionKey}:1`]), `arrow taken last ${JSON.stringify(taken)}`);
  check((await page.locator('.events .log').allTextContents()).some((l) => l.includes('done after 3')), 'the flowchart looped three times');
  await page.screenshot({ path: join(results, 'g2-flowchart.png') });
  await page.getByRole('button', { name: 'List view', exact: true }).click();
  check((await item('decision-1').locator('.transitions-summary').textContent()) === '→ assign-1 when n < 3→ log-1', 'the list view shows the transitions');
  step('G-2. Flowchart built in Chromium: Flowchart inserted (file raised to 1.1), steps dropped on the canvas, an arrow drawn, transitions added by keyboard, a step moved; saved, valid, ran 3 loops; the arrow taken is highlighted; list view');

  // G-3: the shipped state machine sample on the same canvas, run: the final state marked, states visited, the arrow taken.
  copyFileSync(join(repo, 'samples', 'state-machine.json'), join(project, 'state-machine.json'));
  await page.reload();
  await page.getByText('Connected to MyRPA.Server').waitFor();
  await openWorkflow('state-machine.json');
  check((await page.locator('.flow-step').evaluateAll((s) => s.map((e) => e.dataset.nodeId).join(','))) === 'init,get-work,process,end', 'the states are on the canvas');
  check((await page.locator('.flow-step[data-node-id="end"] .badge.final').count()) === 1, 'the final state is marked');
  await runWithDefaults();
  await runStatus.filter({ hasText: 'Succeeded' }).waitFor();
  const machineLogs = (await page.locator('.events .log').allTextContents()).join('|');
  check(machineLogs.includes('Processing invoice-3'), `state machine logs: ${machineLogs}`);
  const getWorkKey = await page.locator('.flow-step[data-node-id="get-work"]').getAttribute('data-key');
  const machineTaken = await page.locator('.flow-arrows g.arrow.taken').evaluateAll((gs) => gs.map((g) => g.dataset.arrow));
  check(JSON.stringify(machineTaken) === JSON.stringify([`${getWorkKey}:1`]), `the arrow taken last: ${JSON.stringify(machineTaken)}`);
  await page.screenshot({ path: join(results, 'g3-state-machine.png') });
  step('G-3. State machine sample on the canvas: final state marked; ran (init → get work ⇄ process → end) and processed 3 items; the arrow taken last (get-work → end) highlighted');

  // W6-8: Single-command start: only --open <file> (no --project, no --web). The server serves the Studio bundled
  // next to it, makes the file's folder the project, and the Studio opens the file.
  const singleLink = await startServer(['--open', join(project, 'hello-world.json')]);
  await page.goto(singleLink);
  await titleIs('hello-world.json');
  check((await page.getByRole('combobox', { name: 'Project' }).inputValue()) === 'demo', 'the file’s folder is the project');
  step('W6-8. Single command (MyRPA.Server --open <file>): bundled Studio served, the file’s folder became the project, the file opened');

  // Phase 6 (ADR-0039), the definition of done: record a simple website interaction and insert it into the Studio. The
  // server loads the browser plugin; the recording browser is headless with a DevTools port (test-only flags) so this
  // script can act as the user in it with real input. Then the inserted activities are saved and run headless.
  const site = createServer((request, response) => {
    response.setHeader('Content-Type', 'text/html; charset=utf-8');
    response.end(request.url.startsWith('/done')
      ? '<!doctype html><title>Done</title><h1 id="welcome">Welcome</h1>'
      : '<!doctype html><title>Sign in</title><form action="/done"><label for="email">Email</label><input id="email" name="email" type="email">'
        + '<label for="password">Password</label><input id="password" name="password" type="password"><button type="submit">Sign in</button></form>');
  });
  await new Promise((resolve) => site.listen(0, '127.0.0.1', resolve));
  const siteUrl = `http://127.0.0.1:${site.address().port}/login`;
  const freePort = await new Promise((resolve) => {
    const probe = createNetServer();
    probe.listen(0, '127.0.0.1', () => {
      const { port } = probe.address();
      probe.close(() => resolve(port));
    });
  });
  const configuration = process.env.MYRPA_CONFIGURATION ?? 'Release';
  const browserPlugin = join(repo, 'plugins', 'MyRPA.Browser.Playwright', 'bin', configuration, 'net10.0');
  writeFileSync(join(project, 'rec.json'), `${JSON.stringify({ schemaVersion: '1.0', id: 'rec', name: 'Recorded', version: '1.0.0', root: { id: 'main', type: 'Core.Sequence' } }, null, 2)}\n`);
  const recordingLink = await startServer(['--project', project, '--web', join(repo, 'web', 'studio', 'dist'), '--plugin', browserPlugin, '--recorder-headless', '--recorder-debugging-port', String(freePort)]);
  await page.goto(recordingLink);
  await page.getByText('Connected to MyRPA.Server').waitFor();
  await openWorkflow('rec.json');
  await row('main').click();
  const recorderPanel = page.getByRole('tabpanel');
  await click('Record');
  await recorderPanel.getByLabel('Start at').fill(siteUrl);
  await recorderPanel.getByRole('button', { name: 'Start recording', exact: true }).click();
  await page.getByTestId('recorder-status').filter({ hasText: /^Recording/ }).waitFor();
  // Act as the user in the recording browser (real input over DevTools).
  const recordingBrowser = await chromium.connectOverCDP(`http://127.0.0.1:${freePort}`);
  const userPage = recordingBrowser.contexts()[0].pages().find((p) => p.url().startsWith(siteUrl)) ?? recordingBrowser.contexts()[0].pages()[0];
  await userPage.getByLabel('Email').fill('ada@example.com');
  await userPage.getByLabel('Password').fill('s3cret-value');
  await userPage.getByRole('button', { name: 'Sign in' }).click();
  await page.getByRole('tab', { name: 'Recorder (3)' }).waitFor();
  const recordedWhat = await recorderPanel.locator('.recorded-what').allTextContents();
  check(JSON.stringify(recordedWhat) === JSON.stringify(['Step 1: Type textbox "Email"', 'Step 2: Type input "Password"', 'Step 3: Click button "Sign in"']), `recorded steps: ${JSON.stringify(recordedWhat)}`);
  check(!(await recorderPanel.textContent()).includes('s3cret'), 'the password is not shown');
  await recorderPanel.getByRole('button', { name: 'Insert 3 steps', exact: true }).click();
  await page.locator('[role=treeitem][data-node-id="close-1"]').waitFor({ timeout: 15_000 }).catch(() => undefined);
  await expectTree('main,open-1,type-1,type-2,click-1,close-1', `the recording was inserted (${await page.locator('.segment.message').textContent()})`);
  await recordingBrowser.close();
  await page.keyboard.press('Control+s');
  await title.filter({ hasText: /^rec\.json$/ }).waitFor();
  const recordedFile = readFileSync(join(project, 'rec.json'), 'utf8');
  const recorded = JSON.parse(recordedFile);
  check(recorded.root.children.map((c) => c.type).join(',') === 'Browser.Open,Browser.TypeText,Browser.TypeText,Browser.Click,Browser.Close', `saved activities ${recorded.root.children.map((c) => c.type)}`);
  check(recorded.root.children[1].properties.selector === 'role=textbox|Email' || recorded.root.children[1].properties.selector === 'label=Email', `semantic selector ${recorded.root.children[1].properties.selector}`);
  check(recorded.arguments?.[0]?.name === 'password' && !recordedFile.includes('s3cret'), 'the password is an argument, never in the file');
  await click('Run');
  await dialog.waitFor();
  await dialog.getByLabel(/^password/).fill('s3cret-value');
  await dialog.getByRole('button', { name: 'Start', exact: true }).click();
  await runStatus.filter({ hasText: 'Succeeded' }).waitFor({ timeout: 60_000 });
  site.close();
  step('Phase 6 DoD. Recorded a sign-in on a local website (real input in the recording browser), inserted it at the selection (Open, Type, Type password, Click, Close; semantic selectors; the password became an argument, never stored), saved, and ran it headless: Succeeded');

  // The deleted stream answers the browser's automatic retry with a 404 (JSON), which Chromium reports on the console.
  const expected = (text) => /EventSource's response has a MIME type/.test(text);
  const unexpected = problems.filter((p) => !expected(p));
  check(unexpected.length === 0, `browser errors: ${unexpected.join('; ')}`);
  step('No script errors or CSP violations in the browser');
  console.log(`Smoke test passed. Screenshots: ${results}`);
});
