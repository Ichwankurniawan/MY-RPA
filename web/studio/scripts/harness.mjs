// Shared by the browser scripts: a throwaway project, the real MyRPA.Server serving the built Studio, and headless
// Chromium signed in through the server's one-time start link.
//
// Needs: the server built (`dotnet build MyRPA.sln -c Release`), the Studio built (`npm run build`), `dotnet` on PATH,
// and Playwright's Chromium (installed once with the browser plugin's playwright.ps1; these scripts never download).

import { spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

export const studioDir = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const repo = resolve(studioDir, '..', '..');
export const results = join(studioDir, 'test-results');

export const check = (condition, message) => {
  if (!condition) {
    throw new Error(`Check failed: ${message}`);
  }
};

/** Runs `body({ project, page, startLink })` against a fresh server and browser; cleans up; exits 1 on failure. */
export async function withStudio(body) {
  const configuration = process.env.MYRPA_CONFIGURATION ?? 'Release';
  const serverDll = join(repo, 'src', 'MyRPA.Server', 'bin', configuration, 'net10.0', 'MyRPA.Server.dll');
  mkdirSync(results, { recursive: true });
  const workspace = mkdtempSync(join(tmpdir(), 'myrpa-studio-'));
  const project = join(workspace, 'demo');
  mkdirSync(project);

  let server;
  let browser;
  let serverOutput = '';
  let failed = false;
  try {
    const startServer = () => {
      server = spawn(process.env.MYRPA_DOTNET ?? 'dotnet', [serverDll, '--project', project, '--web', join(studioDir, 'dist'), '--port', '0'], {
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      return new Promise((resolveLink, reject) => {
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
    };

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

    await body({ project, page, startServer, problems });
  } catch (error) {
    failed = true;
    console.error(error);
  } finally {
    await browser?.close();
    if (server) {
      server.kill();
      await new Promise((done) => (server.exitCode !== null ? done() : server.once('exit', done)));
    }

    rmSync(workspace, { recursive: true, force: true });
  }

  process.exit(failed ? 1 : 0);
}
