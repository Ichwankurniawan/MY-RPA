// `npm run manual`: the Web Studio manual test script (docs/architecture/web-studio-parity.md §4, steps 1-11) driven as
// a person would: mouse drags from the toolbox, keyboard shortcuts, the Files panel, the CLI. One screenshot per step
// (test-results/manual-NN.png). It does not replace the person: the browser's leave prompt and step 12 (screen reader)
// cannot be observed here. Needs the Release build, like `npm run smoke`.
import { spawnSync } from 'node:child_process';
import { cpSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { check, repo, results, withStudio } from './harness.mjs';

const shot = (page, n) => page.screenshot({ path: join(results, `manual-${String(n).padStart(2, '0')}.png`) });
const ok = (text) => console.log(`  PASS ${text}`);

await withStudio(async ({ project, page, startServer, problems }) => {
  cpSync(join(repo, 'samples'), project, { recursive: true });
  const link = await startServer();
  await page.setViewportSize({ width: 1512, height: 820 });
  const title = page.getByTestId('document-title');
  const dialog = page.getByRole('dialog');
  const properties = page.getByRole('complementary', { name: 'Properties' });
  const files = page.getByRole('tree', { name: 'Workflow files' });
  const row = (id) => page.locator(`[role=treeitem][data-node-id="${id}"] > .node`);
  const ids = () => page.locator('[role=treeitem][data-node-id]').evaluateAll((items) => items.map((i) => i.dataset.nodeId).join(','));
  const zone = (id, name) => row(id).getByRole('button', { name, exact: true });
  const tool = (name) => page.getByRole('button', { name: `Insert ${name} (Core.${name})` });
  const badges = (id) => row(id).locator('.badge').allTextContents();
  const slotOf = async (id) => (await page.locator(`[role=treeitem][data-node-id="${id}"] .slot`).first().textContent()) ?? '';
  const drag = async (from, to, at = 0.5) => {
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
    await page.mouse.move(a.x + a.width / 2 + 10, a.y + a.height / 2 + 10, { steps: 3 });
    await page.mouse.move(b.x + b.width / 2, b.y + b.height * at, { steps: 10 });
    // Like a person following the target: live validation can change a card (an error appears) while dragging.
    const settled = await to.boundingBox();
    await page.mouse.move(settled.x + settled.width / 2, settled.y + settled.height * at, { steps: 2 });
    await page.mouse.up();
  };
  const problemsTab = () => page.getByRole('tab', { name: /^Problems/ });
  const problemsText = async () => (await page.locator('.problems').textContent()) ?? '';
  const appears = (id) => page.locator(`[role=treeitem][data-node-id="${id}"]`).waitFor();

  // 1. Start
  await page.goto(link);
  await files.locator('[data-path="hello-world.json"]').waitFor();
  await shot(page, 1);
  ok('1. Start link signed in; the Files panel lists the project (hello-world.json, control-flow.json, ...)');

  // 2. Create
  await page.getByRole('button', { name: 'New…', exact: true }).click();
  await dialog.getByLabel('Path in the project').fill('greeter.json');
  await dialog.getByRole('button', { name: 'Create', exact: true }).click();
  await title.filter({ hasText: /^greeter\.json$/ }).waitFor();
  check((await ids()) === 'main', `new workflow is an empty Sequence: ${await ids()}`);
  await page.getByRole('navigation', { name: 'Selection' }).getByRole('button', { name: 'Workflow', exact: true }).click();
  await properties.getByLabel('Name', { exact: true }).fill('Greeter');
  check((await title.textContent()).includes('•'), 'title shows the unsaved marker');
  await shot(page, 2);
  ok('2. New... created greeter.json (empty Sequence); Name = Greeter; the title shows the unsaved marker');

  // 3. Add activities by dragging from the toolbox
  await drag(tool('Assign'), page.getByRole('button', { name: 'Empty list: insert here' }));
  await appears('assign-1');
  await drag(tool('If'), row('assign-1'), 0.6);
  await appears('if-1');
  await drag(tool('Log'), zone('if-1', 'then: empty (required)'));
  await appears('log-1');
  check((await ids()) === 'main,assign-1,if-1,log-1', `tree ${await ids()}`);
  check((await slotOf('log-1')) === 'then:', 'Log is in then');
  await problemsTab().click();
  await page.locator('.problems').filter({ hasText: 'MYRPA1040' }).waitFor();
  // Live validation runs after typing pauses: wait until it has seen the last activity too (the Log).
  await row('log-1').locator('.badge', { hasText: 'error' }).waitFor();
  const marked = [(await badges('assign-1')).includes('error'), (await badges('if-1')).includes('error'), (await badges('log-1')).includes('error')];
  check(marked.every(Boolean), `error badges ${marked}`);
  await shot(page, 3);
  ok(`3. Dragged Assign, If (below it) and Log (into then); all three marked error; Problems lists ${(await problemsText()).match(/MYRPA1040/g).length} x MYRPA1040`);

  // 4. Configure
  await page.getByRole('tab', { name: /^Arguments/ }).click();
  await page.getByRole('button', { name: 'Add argument', exact: true }).click();
  await page.getByLabel('argument 1 name').fill('who');
  await page.getByLabel('argument 1 default').fill('"World"');
  await page.getByRole('tab', { name: /^Variables/ }).click();
  await page.getByRole('button', { name: 'Add variable', exact: true }).click();
  await page.getByLabel('variable 1 name').fill('message');
  await row('assign-1').click();
  await properties.getByLabel(/^to/).fill('message');
  await properties.getByLabel(/^value/).fill("'Hello, ' + who");
  await row('if-1').click();
  await properties.getByLabel(/^condition/).fill('len(who) > 0');
  await row('log-1').click();
  await properties.getByLabel(/^message/).fill('message');
  const typed = Date.now();
  await problemsTab().click();
  await page.getByTestId('no-problems').waitFor();
  await shot(page, 4);
  ok(`4. Argument who = "World", variable message, Assign/If/Log configured; Problems empty ${Date.now() - typed} ms after typing stopped (no Validate pressed)`);

  // 5. Undo/redo (focus in the tree, as after clicking the Log)
  await row('log-1').click();
  await page.keyboard.press('Control+z');
  check((await properties.getByLabel(/^message/).inputValue()) === '', 'Ctrl+Z removed the Log message');
  await page.keyboard.press('Control+y');
  check((await properties.getByLabel(/^message/).inputValue()) === 'message', 'Ctrl+Y restored it');
  await shot(page, 5);
  ok('5. Ctrl+Z removed the Log message; Ctrl+Y restored it');

  // 6. Copy, paste, delete; cut into else and undo
  await row('assign-1').click();
  await page.keyboard.press('Control+c');
  await page.keyboard.press('Control+v');
  check((await ids()) === 'main,assign-1,assign-2,if-1,log-1', `after paste ${await ids()}`);
  await page.keyboard.press('Delete');
  check((await ids()) === 'main,assign-1,if-1,log-1', `after Del ${await ids()}`);
  await row('log-1').click();
  await page.keyboard.press('Control+x');
  await zone('if-1', 'else: empty').click();
  await page.keyboard.press('Control+v');
  check((await slotOf('log-1')) === 'else:', `Log moved into else (${await slotOf('log-1')})`);
  await shot(page, 6);
  await page.keyboard.press('Control+z');
  await page.keyboard.press('Control+z');
  check((await slotOf('log-1')) === 'then:', `two undos: Log back in then (${await slotOf('log-1')})`);
  ok('6. Ctrl+C/Ctrl+V pasted assign-2 after Assign; Del deleted it; Ctrl+X on Log, else zone, Ctrl+V moved it; Ctrl+Z twice put it back in then');

  // 7. Save; the CLI validates the file
  await page.keyboard.press('Control+s');
  await title.filter({ hasText: /^greeter\.json$/ }).waitFor();
  const cli = spawnSync(process.env.MYRPA_DOTNET ?? 'dotnet', [join(repo, 'src', 'MyRPA.Cli', 'bin', 'Release', 'net10.0', 'myrpa.dll'), 'validate', join(project, 'greeter.json')], { encoding: 'utf8' });
  check(cli.status === 0, `myrpa validate exit ${cli.status}: ${cli.stdout}${cli.stderr}`);
  await shot(page, 7);
  ok(`7. Ctrl+S saved (marker gone); myrpa validate greeter.json exit 0: ${cli.stdout.trim().split('\n')[0]}`);

  // 8. Open another file, then greeter.json again
  await files.locator('[data-path="hello-world.json"]').dblclick();
  await title.filter({ hasText: /^hello-world\.json$/ }).waitFor();
  await files.locator('[data-path="greeter.json"]').dblclick();
  await title.filter({ hasText: /^greeter\.json$/ }).waitFor();
  check((await ids()) === 'main,assign-1,if-1,log-1', `reopened ${await ids()}`);
  const saved = JSON.parse(readFileSync(join(project, 'greeter.json'), 'utf8'));
  check(saved.name === 'Greeter' && saved.arguments[0].name === 'who' && saved.variables[0].name === 'message', 'saved metadata, argument and variable');
  await shot(page, 8);
  ok('8. Opened hello-world.json, then greeter.json again: everything is back');

  // 9. Run with who = Ada
  await row('main').click();
  await page.keyboard.press('F5');
  await dialog.waitFor();
  await dialog.getByLabel(/^who/).fill('Ada');
  await dialog.getByRole('button', { name: 'Start', exact: true }).click();
  await page.getByTestId('run-status').filter({ hasText: 'Succeeded' }).waitFor();
  const logs = (await page.locator('.events .log').allTextContents()).map((l) => l.trim());
  check(logs.some((l) => l.includes('Hello, Ada')), `logs ${logs}`);
  const states = await Promise.all(['main', 'assign-1', 'if-1', 'log-1'].map(async (id) => `${id}=${(await badges(id)).join('/')}`));
  await shot(page, 9);
  ok(`9. F5, who = Ada, Start: Succeeded; log "${logs.find((l) => l.includes('Hello'))}"; node states ${states.join(' ')}`);

  // 10. Failure: a Throw at the end
  await drag(tool('Throw'), row('if-1'), 0.6);
  await appears('throw-1');
  check((await ids()) === 'main,assign-1,if-1,log-1,throw-1', `throw at the end: ${await ids()}`);
  await row('throw-1').click();
  await properties.getByLabel(/^message/).fill("'boom'");
  await row('main').click();
  await page.keyboard.press('F5');
  await dialog.waitFor();
  await dialog.getByRole('button', { name: 'Start', exact: true }).click();
  await page.getByTestId('run-status').filter({ hasText: 'Failed' }).waitFor();
  const failure = (await page.getByTestId('run-error').textContent()) ?? '';
  check(failure.includes('throw-1') && failure.includes('boom'), `failure ${failure}`);
  await page.getByRole('button', { name: 'Select failed node', exact: true }).click();
  check((await page.locator('[role=treeitem][data-node-id][aria-selected=true]').getAttribute('data-node-id')) === 'throw-1', 'Select failed node selected the Throw');
  const throwBadges = await badges('throw-1');
  await shot(page, 10);
  ok(`10. Throw 'boom' at the end: Failed: ${failure.replace('Select failed node', '').trim()}; Select failed node selected throw-1; badges ${throwBadges.join('/')}`);

  // 11. Close with an unsaved change; the browser asks; the change is offered back
  await properties.getByLabel(/^message/).fill("'boom!'");
  const prevented = await page.evaluate(() => { const e = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(e); return e.defaultPrevented; });
  check((await title.textContent()).includes('•') && prevented, 'unsaved, and leaving the page is cancelled by the Studio');
  await page.goto('about:blank');
  const again = page;
  await again.goto(new URL(link).origin + '/');
  await again.getByRole('tree', { name: 'Workflow files' }).locator('[data-path="greeter.json"]').dblclick();
  await again.getByRole('dialog').getByRole('button', { name: 'Restore', exact: true }).click();
  await again.locator('[role=treeitem][data-node-id="throw-1"] > .node').click();
  const restored = await again.getByRole('complementary', { name: 'Properties' }).getByLabel(/^message/).inputValue();
  check(restored === "'boom!'", `restored ${restored}`);
  await again.screenshot({ path: join(results, 'manual-11.png') });
  ok(`11. With an unsaved change the Studio cancels leaving the page (the browser's prompt itself cannot be observed in automation); left the page and came back; reopened greeter.json: Restore brought back ${restored}`);

  check(problems.length === 0, `browser errors: ${problems.join(' | ')}`);
  ok('No script errors or CSP violations');
});
