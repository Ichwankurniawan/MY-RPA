// End-to-end smoke test: Web Studio → MyRPA.Server → WorkflowLoader/ProjectStore → Execution.Hosting → engine →
// execution events and logs → SSE → Web Studio, with the W4A structural editing flow. Real server, real engine, real
// browser (headless Chromium). It works on a throwaway copy of samples/hello-world.json, so it never edits the repository
// and can run repeatedly. Checks use roles, labels and data attributes, never pixels. See harness.mjs for prerequisites.

import { copyFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
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
  const message = page.getByLabel(/^message/);
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
  await page.locator('.field-error').filter({ hasText: 'MYRPA1043' }).waitFor();
  const requestsBefore = runRequests;
  await runWithDefaults();
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
  check(failure.includes('at boom') && failure.includes('Planned failure'), `failure ${failure}`);
  check((await row('boom').getAttribute('data-run-status')) === 'Failed' && (await row('never').getAttribute('data-run-status')) === null, 'failed and not executed nodes');
  await click('Select failed node');
  check((await selected()) === 'boom', `selected after "Select failed node": ${await selected()}`);
  await message.fill("'Changed after the failure'");
  await title.filter({ hasText: /•$/ }).waitFor();
  await page.screenshot({ path: join(results, 'studio-run-failed.png') });
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

  // W6-8: Single-command start: only --open <file> (no --project, no --web). The server serves the Studio bundled
  // next to it, makes the file's folder the project, and the Studio opens the file.
  const singleLink = await startServer(['--open', join(project, 'hello-world.json')]);
  await page.goto(singleLink);
  await titleIs('hello-world.json');
  check((await page.getByRole('combobox', { name: 'Project' }).inputValue()) === 'demo', 'the file’s folder is the project');
  step('W6-8. Single command (MyRPA.Server --open <file>): bundled Studio served, the file’s folder became the project, the file opened');

  // The deleted stream answers the browser's automatic retry with a 404 (JSON), which Chromium reports on the console.
  const expected = (text) => /EventSource's response has a MIME type/.test(text);
  const unexpected = problems.filter((p) => !expected(p));
  check(unexpected.length === 0, `browser errors: ${unexpected.join('; ')}`);
  step('No script errors or CSP violations in the browser');
  console.log(`Smoke test passed. Screenshots: ${results}`);
});
