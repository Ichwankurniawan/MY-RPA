// Phase 10 acceptance in a real browser (ADR-0049): from the Studio only, create a Runner (Administration), start the
// real Runner process with the token the page showed, publish and deploy a workflow (Deployments), submit a job, and
// watch it succeed on the Runner (Monitoring, Robots). Real server, real Runner, real engine, headless Chromium; a
// throwaway project and data folder. Needs the Release build of the Server and the Runner (see harness.mjs).

import { spawn } from 'node:child_process';
import { existsSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { check, repo, withStudio } from './harness.mjs';

const step = (text) => console.log(`  ✓ ${text}`);

const greet = {
  schemaVersion: '1.0',
  id: 'greet',
  name: 'Greet',
  version: '1.0.0',
  arguments: [
    { name: 'who', direction: 'In', type: 'String', required: true },
    { name: 'greeting', direction: 'Out', type: 'String' },
  ],
  root: {
    id: 'main',
    type: 'Core.Sequence',
    children: [
      { id: 'assign', type: 'Core.Assign', properties: { to: 'greeting', value: "'Hello, ' + who" } },
      { id: 'log', type: 'Core.Log', properties: { message: 'greeting' } },
    ],
  },
};

await withStudio(async ({ project, page, startServer, problems, workspace, configuration }) => {
  writeFileSync(join(project, 'greet.json'), JSON.stringify(greet, null, 2));
  writeFileSync(join(project, 'farewell.json'), JSON.stringify({ ...greet, id: 'farewell', name: 'Farewell' }, null, 2));
  const startLink = await startServer();
  const origin = new URL(startLink).origin;
  await page.goto(startLink);
  const rail = page.getByRole('navigation', { name: 'Main' });
  await rail.getByRole('button', { name: 'Deployments' }).waitFor();
  step('The rail shows Deployments, Monitoring, Robots and Admin (the Server runs the orchestrator)');

  // Administration: a Runner identity; its token is shown once.
  await rail.getByRole('button', { name: 'Admin' }).click();
  const admin = page.getByRole('region', { name: 'Runner administration' });
  await admin.getByLabel('Name').fill('browser-runner');
  await admin.getByRole('button', { name: 'Create Runner' }).click();
  const token = (await admin.locator('.ops-secret pre').first().textContent())?.trim() ?? '';
  check(token.startsWith('myrpa_runner_'), `a Runner token was shown (${token.slice(0, 13)}…)`);
  step('Administration created a Runner and showed its token once');

  // The real Runner process, configured like an operator would (token in a file, never on the command line).
  const runnerDll = join(repo, 'src', 'MyRPA.Runner', 'bin', configuration, 'net10.0', 'myrpa-runner.dll');
  check(existsSync(runnerDll), `the Runner is built: ${runnerDll}`);
  writeFileSync(join(workspace, 'runner.token'), token);
  writeFileSync(join(workspace, 'runner.json'), JSON.stringify({ serverUrl: `${origin}/`, tokenFile: 'runner.token', workDirectory: 'runner-work', claimWaitSeconds: 2 }));
  let runnerOutput = '';
  const runner = spawn(process.env.MYRPA_DOTNET ?? 'dotnet', [runnerDll, 'run', '--config', join(workspace, 'runner.json')], { stdio: ['ignore', 'pipe', 'pipe'] });
  runner.stdout.on('data', (d) => (runnerOutput += d));
  runner.stderr.on('data', (d) => (runnerOutput += d));
  try {
    await rail.getByRole('button', { name: 'Robots' }).click();
    const robots = page.getByRole('main', { name: 'Robots' });
    for (let i = 0; i < 60 && !(await robots.getByText('Available').isVisible()); i++) {
      await robots.getByRole('button', { name: 'Refresh' }).click();
      await page.waitForTimeout(500);
    }

    check(await robots.getByText('Available').isVisible(), `the Runner registered:\n${runnerOutput}`);
    step('Robots shows the Runner online (Available) with its machine and capabilities');

    // Deployments: publish the project (immutable v1) with two entry points, deploy it, submit a job for one entry
    // point with an argument typed into the page.
    await rail.getByRole('button', { name: 'Deployments' }).click();
    const deployments = page.getByRole('main', { name: 'Deployments' });
    await deployments.getByRole('button', { name: 'Deployment of demo' }).click();
    const publish = deployments.getByRole('region', { name: 'Publish the project' });
    await publish.getByRole('checkbox', { name: 'greet.json' }).check();
    await publish.getByRole('checkbox', { name: 'farewell.json' }).check();
    await publish.getByRole('button', { name: 'Publish' }).click();
    await publish.getByRole('status').filter({ hasText: 'Published demo with 2 entry points.' }).waitFor();
    const detail = deployments.getByRole('region', { name: 'Deployment details' });
    await detail.getByRole('button', { name: 'Deploy' }).click();
    await detail.getByRole('status').filter({ hasText: 'Deployed v1.' }).waitFor();
    const runNow = detail.getByRole('region', { name: 'Run now' });
    await runNow.getByLabel('Entry point to run').selectOption('greet.json');
    await runNow.getByLabel(/^who/).fill('Ada');
    await runNow.getByRole('button', { name: 'Run now' }).click();
    await detail.getByRole('status').filter({ hasText: 'Job submitted for v1 (greet.json).' }).waitFor();
    step('Deployments published the project as v1 (immutable, two entry points), deployed it and submitted a job for greet.json');

    // Monitoring: the job runs on the Runner and succeeds; its events and outputs are on the Server.
    await rail.getByRole('button', { name: 'Monitoring' }).click();
    const monitoring = page.getByRole('main', { name: 'Monitoring' });
    const executions = monitoring.getByRole('region', { name: 'Executions' });
    for (let i = 0; i < 60 && !(await executions.locator('.badge.status-succeeded').first().isVisible()); i++) {
      await monitoring.getByRole('button', { name: 'Refresh' }).click();
      await page.waitForTimeout(500);
    }

    check(await executions.locator('.badge.status-succeeded').first().isVisible(), `the job succeeded:\n${runnerOutput}`);
    await executions.getByRole('button', { name: /^Show job / }).first().click();
    const job = monitoring.getByRole('region', { name: /^Job / });
    await job.getByText('Hello, Ada').first().waitFor();
    check(await job.getByText('browser-runner').first().isVisible(), 'the job names its Runner');
    step('Monitoring shows the job Succeeded on browser-runner, with its events and outputs');
  } finally {
    runner.kill();
    await new Promise((done) => (runner.exitCode !== null ? done() : runner.once('exit', done)));
  }

  check(problems.length === 0, `browser errors: ${problems.join('; ')}`);
  step('No script errors or CSP violations in the browser');
  console.log('Operations scenario passed.');
});
