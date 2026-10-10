import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { serialize } from './document';
import { Studio } from './studio';
import { catalog, FakeApi, FakeEventSource, immediately, settle } from './test-support';
import type { ActivityDescriptor } from './types';

// Catalog 1.2 metadata (ADR-0042): value types, defaults, secret properties and side effects, all from the catalog.

const request: ActivityDescriptor = {
  type: 'Http.Request',
  displayName: 'HTTP Request',
  category: 'API',
  description: 'Sends an HTTP request.',
  allowsChildren: false,
  sideEffects: ['Network'],
  properties: [
    { name: 'url', kind: 'Expression', required: true, allowedValues: [], scopeSlots: [], valueType: 'String' },
    { name: 'token', kind: 'Expression', required: false, allowedValues: [], scopeSlots: [], valueType: 'String', secret: true },
    { name: 'timeoutMs', kind: 'Expression', required: false, allowedValues: [], scopeSlots: [], valueType: 'Int', default: 30000 },
    { name: 'script', kind: 'Text', required: false, allowedValues: [], scopeSlots: [], valueType: 'String', multiline: true },
  ],
  slots: [],
};

async function renderStudio() {
  const api = new FakeApi();
  api.activities = async () => [...catalog, request];
  api.files.set('call.json', {
    text: serialize({ schemaVersion: '1.0', id: 'call', name: 'Call', version: '1', root: { id: 'main', type: 'Core.Sequence', children: [{ id: 'get', type: 'Http.Request', properties: { url: "'http://localhost/'" } }] } }),
    etag: 1,
  });
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { validateDelayMs: undefined });
  render(<App studio={studio} />);
  await act(settle);
  await act(async () => {
    await studio.open('call.json');
  });
  return studio;
}

afterEach(cleanup);

describe('Catalog 1.2 metadata', () => {
  it('says in the toolbox what an activity touches', async () => {
    await renderStudio();

    const entry = screen.getByRole('button', { name: 'Insert HTTP Request (Http.Request)' }).closest('li')!;
    expect(entry.querySelector('.description')!.textContent).toBe('Sends an HTTP request. Uses: network.');
  });

  it('shows value types, the default as a placeholder, and marks a secret with how to give it', async () => {
    await renderStudio();
    await act(async () => fireEvent.click(document.querySelector('[role="treeitem"][data-node-id="get"] > .node')!));
    const panel = within(screen.getByRole('complementary', { name: 'Properties' }));

    const token = panel.getByRole('combobox', { name: /^token/ });
    expect(token.closest('.field')!.querySelector('.badge.secret')!.textContent).toBe('secret');
    expect(token.closest('.field')!.querySelector('label small')!.textContent).toBe('Expression · String');
    expect(token.getAttribute('placeholder')).toBe('an argument or variable, e.g. apiToken');
    expect(token.closest('.field')!.textContent).toContain('never type the secret itself here');
    expect(panel.getByRole('combobox', { name: /^timeoutMs/ }).getAttribute('placeholder')).toBe('default: 30000');
  });

  it('edits a multiline text property (code, SQL) in a monospace box that keeps its lines (ADR-0045)', async () => {
    const studio = await renderStudio();
    await act(async () => fireEvent.click(document.querySelector('[role="treeitem"][data-node-id="get"] > .node')!));
    const panel = within(screen.getByRole('complementary', { name: 'Properties' }));

    const script = panel.getByRole('textbox', { name: /^script/ });
    expect(script.tagName).toBe('TEXTAREA');
    expect(script.classList.contains('multiline-text')).toBe(true);
    fireEvent.change(script, { target: { value: 'const a = 1;\nreturn a;' } });

    const node = (studio.store.get().document!.root as { children: { properties: Record<string, unknown> }[] }).children[0];
    expect(node.properties.script).toBe('const a = 1;\nreturn a;');
  });
});
