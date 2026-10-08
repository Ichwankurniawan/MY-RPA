// Accessibility check (W8, ADR-0034): axe-core on the real Studio in each main state, against the real server, plus a
// keyboard-only authoring pass. Fails on any serious or critical axe violation (ADR-0021 criterion 10) and prints the
// others. axe is evaluated through the DevTools protocol, so the page's CSP stays as it is. See harness.mjs for the
// prerequisites.

import { readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join } from 'node:path';
import { check, repo, withStudio } from './harness.mjs';

const axeSource = readFileSync(createRequire(import.meta.url).resolve('axe-core/axe.min.js'), 'utf8');
const step = (text) => console.log(`  ✓ ${text}`);

const workflow = {
  schemaVersion: '1.0',
  id: 'a11y',
  name: 'Accessibility',
  version: '1.0.0',
  arguments: [{ name: 'who', direction: 'In', type: 'String', default: 'World' }],
  variables: [{ name: 'message', type: 'String' }],
  root: {
    id: 'main',
    type: 'Core.Sequence',
    children: [
      { id: 'set', type: 'Core.Assign', properties: { to: 'message', value: "'Hello, ' + who" } },
      { id: 'check', type: 'Core.If', properties: { condition: 'len(who) > 0' }, slots: { then: { id: 'say', type: 'Core.Log', properties: { message: 'message' } } } },
      { id: 'pick', type: 'Core.Switch', properties: { expression: 'who' } },
    ],
  },
};

await withStudio(async ({ project, page, startServer, problems }) => {
  writeFileSync(join(project, 'a11y.json'), JSON.stringify(workflow, null, 2));
  await page.goto(await startServer());
  await page.getByText('Connected to MyRPA.Server').waitFor();

  const findings = [];
  const scan = async (state) => {
    await page.evaluate(axeSource);
    const result = await page.evaluate(() => window.axe.run(document, { resultTypes: ['violations'] }));
    for (const v of result.violations) {
      const finding = { state, id: v.id, impact: v.impact, help: v.help, nodes: v.nodes.slice(0, 3).map((n) => n.target.join(' ')) };
      findings.push(finding);
      console.log(`    ${finding.impact ?? 'n/a'} ${finding.id}: ${finding.help} — ${finding.nodes.join(' | ')}`);
    }

    const blocking = result.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
    step(`${state}: ${result.violations.length} violation(s), ${blocking.length} serious or critical`);
  };

  const filesTree = page.getByRole('tree', { name: 'Workflow files' });
  await filesTree.locator('[data-path="a11y.json"]').dblclick();
  await page.getByTestId('document-title').filter({ hasText: /^a11y\.json$/ }).waitFor();
  await page.locator('[role=treeitem][data-node-id="check"] > .node').click();
  await scan('Designer with an If selected (Properties, zones)');
  await page.getByRole('navigation', { name: 'Selection' }).getByRole('button', { name: 'Workflow', exact: true }).click();
  await scan('Workflow details');
  await page.getByRole('tab', { name: /^Variables/ }).click();
  await scan('Variables tab');
  await page.getByRole('tab', { name: /^Arguments/ }).click();
  await scan('Arguments tab');
  await page.getByRole('button', { name: 'Run', exact: true }).click();
  await page.getByRole('dialog').waitFor();
  await scan('Run dialog');
  await page.getByRole('dialog').getByRole('button', { name: 'Start', exact: true }).click();
  await page.getByTestId('run-status').filter({ hasText: 'Succeeded' }).waitFor();
  await scan('After a run (Execution panel, node states)');
  await page.emulateMedia({ colorScheme: 'dark' });
  await scan('Dark theme, after a run');
  await page.locator('[role=treeitem][data-node-id="check"] > .node').click();
  await scan('Dark theme, an If selected');
  await page.emulateMedia({ colorScheme: 'light' });
  await page.getByRole('button', { name: 'New…', exact: true }).click();
  await page.getByRole('dialog').waitFor();
  await scan('New workflow dialog');
  await page.keyboard.press('Escape');

  // Keyboard only: select in the tree, insert from the toolbox, fill a slot through its zone, edit a property, cut,
  // paste, undo, save. No mouse from here on.
  const tree = page.getByRole('tree', { name: 'Workflow', exact: true });
  const selected = () => tree.locator('[role=treeitem][aria-selected=true]').getAttribute('data-node-id');
  await tree.locator('[role=treeitem][data-node-id="main"]').focus();
  await page.keyboard.press('Home');
  await page.keyboard.press('ArrowDown');
  check((await selected()) === 'set', `arrow keys select: ${await selected()}`);
  await page.getByRole('button', { name: 'Insert Log (Core.Log)' }).focus();
  await page.keyboard.press('Enter');
  check((await selected()) === 'log-1', `Enter on a toolbox entry inserts after the selection: ${await selected()}`);
  await page.locator('[role=treeitem][data-node-id="check"]').getByRole('button', { name: 'else: empty', exact: true }).focus();
  await page.keyboard.press('Enter');
  await page.getByRole('button', { name: 'Insert Delay (Core.Delay)' }).focus();
  await page.keyboard.press('Enter');
  check((await selected()) === 'delay-1', `a zone picked by keyboard receives the insert: ${await selected()}`);
  await page.getByRole('complementary', { name: 'Properties' }).getByLabel(/^milliseconds/).focus();
  await page.keyboard.type('10');
  await tree.locator('[role=treeitem][data-node-id="delay-1"]').focus();
  await page.keyboard.press('Control+x');
  check((await tree.locator('[data-node-id="delay-1"]').count()) === 0, 'Ctrl+X cut the Delay');
  await tree.locator('[role=treeitem][aria-selected=true]').focus();
  await page.keyboard.press('Home');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowDown');
  check((await selected()) === 'log-1', `arrow keys reach the Log: ${await selected()}`);
  await page.keyboard.press('Control+v');
  check((await selected()) === 'delay-1', `Ctrl+V pasted it after the Log: ${await selected()}`);
  await page.keyboard.press('Control+z');
  await page.keyboard.press('Control+s');
  await page.getByTestId('document-title').filter({ hasText: /^a11y\.json$/ }).waitFor();
  step('Keyboard only: arrows, insert by Enter, a slot zone by Enter, typing, Ctrl+X, Ctrl+V, Ctrl+Z, Ctrl+S');

  // G-2: the flowchart canvas, its list view and the Transitions editor; then the keyboard way to edit transitions.
  writeFileSync(join(project, 'flowchart.json'), readFileSync(join(repo, 'samples', 'flowchart.json'), 'utf8'));
  await page.reload();
  await page.getByText('Connected to MyRPA.Server').waitFor();
  await filesTree.locator('[data-path="flowchart.json"]').dblclick();
  await page.getByTestId('document-title').filter({ hasText: /^flowchart\.json$/ }).waitFor();
  await page.locator('.flow-step[data-node-id="check"] > .node').click();
  await scan('Flowchart canvas, a decision selected (Transitions in Properties)');
  await page.emulateMedia({ colorScheme: 'dark' });
  await scan('Dark theme, flowchart canvas');
  await page.emulateMedia({ colorScheme: 'light' });
  await page.getByRole('button', { name: 'List view', exact: true }).click();
  await scan('Flowchart list view');
  // G-3: a state machine on the canvas, a state selected.
  writeFileSync(join(project, 'state-machine.json'), readFileSync(join(repo, 'samples', 'state-machine.json'), 'utf8'));
  await page.reload();
  await page.getByText('Connected to MyRPA.Server').waitFor();
  await filesTree.locator('[data-path="state-machine.json"]').dblclick();
  await page.getByTestId('document-title').filter({ hasText: /^state-machine\.json$/ }).waitFor();
  await page.locator('.flow-step[data-node-id="process"] > .node').click();
  await scan('State machine canvas, a state selected');
  await filesTree.locator('[data-path="flowchart.json"]').dblclick();
  await page.getByTestId('document-title').filter({ hasText: /^flowchart\.json$/ }).waitFor();
  await page.locator('.flow-step[data-node-id="check"] > .node').click();
  if ((await page.getByRole('button', { name: 'List view', exact: true }).getAttribute('aria-pressed')) !== 'true') {
    await page.getByRole('button', { name: 'List view', exact: true }).click();
  }
  const panel = page.getByRole('complementary', { name: 'Properties' });
  await tree.locator('[role=treeitem][aria-selected=true]').focus();
  await page.keyboard.press('ArrowDown');
  check((await selected()) === 'wait', `arrow keys move through the steps: ${await selected()}`);
  const addTo = panel.getByLabel('Add a transition to');
  await addTo.focus();
  await page.keyboard.press('ArrowDown'); // the first step: start
  await panel.getByRole('button', { name: 'Add transition', exact: true }).focus();
  await page.keyboard.press('Enter');
  check((await panel.getByRole('group', { name: /^Transition \d/ }).count()) === 2, 'a transition was added by keyboard');
  await panel.getByRole('button', { name: 'Set as start step', exact: true }).focus();
  await page.keyboard.press('Enter');
  check((await tree.locator('[role=treeitem][data-node-id="wait"] .badge.start').count()) === 1, 'Set as start step by keyboard');
  step('Keyboard only (flowchart): arrows through the steps in the list view, a transition added and a start step set from Properties');

  const blocking = findings.filter((f) => f.impact === 'serious' || f.impact === 'critical');
  check(blocking.length === 0, `${blocking.length} serious or critical accessibility violation(s)`);
  check(problems.length === 0, `browser errors: ${problems.join('; ')}`);
  console.log('Accessibility check passed: no serious or critical violations.');
});
