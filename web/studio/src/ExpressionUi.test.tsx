import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { nodeAt } from './document';
import { callAt, completionContext, completionItems } from './ExpressionInput';
import { Studio } from './studio';
import { FakeApi, FakeEventSource, immediately, settle } from './test-support';
import type { JsonObject, ScopeName } from './types';

// Expression completion (ADR-0041, E-2): names in scope from the server, functions, keyboard and mouse.

const names: ScopeName[] = [
  { name: 'userName', kind: 'Argument', type: 'String', direction: 'In', path: '$.arguments[0]' },
  { name: 'greeting', kind: 'Argument', type: 'String', direction: 'Out', path: '$.arguments[1]' },
];

async function renderStudio() {
  const api = new FakeApi();
  api.scopeNames = names;
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { validateDelayMs: undefined });
  render(<App studio={studio} />);
  await act(settle);
  await act(async () => {
    await studio.open('hello-world.json');
  });
  await act(async () => fireEvent.click(document.querySelector('[role="treeitem"][data-node-id="log-greeting"] > .node')!));
  const field = within(screen.getByRole('complementary', { name: 'Properties' })).getByRole('combobox', { name: /^message/ }) as HTMLInputElement;
  await act(async () => {
    fireEvent.focus(field);
    await settle();
  });
  return { studio, api, field, message: () => (nodeAt(studio.store.get().document!, [{ children: 1 }]).properties as JsonObject).message };
}

const type = (field: HTMLInputElement, text: string) => act(async () => fireEvent.change(field, { target: { value: text } }));
const key = (field: HTMLInputElement, k: string, init: KeyboardEventInit = {}) => act(async () => fireEvent.keyDown(field, { key: k, ...init }));
const list = () => within(screen.getByRole('listbox', { name: 'Completions' }));
const options = () => list().getAllByRole('option').map((o) => o.querySelector('.completion-label')!.textContent);

beforeEach(() => {
  FakeEventSource.instances = [];
});

afterEach(cleanup);

describe('Completion helpers', () => {
  it('finds the word being typed, but not after a dot, inside a string or in a number', () => {
    expect(completionContext('who + gre', 9)).toEqual({ start: 6, end: 9, prefix: 'gre' });
    expect(completionContext('greeting', 3)).toEqual({ start: 0, end: 8, prefix: 'gre' });
    expect(completionContext('x.gre', 5)).toBeUndefined();
    expect(completionContext("'gre", 4)).toBeUndefined();
    expect(completionContext("'it\\'s' + gre", 13)).toEqual({ start: 10, end: 13, prefix: 'gre' });
    expect(completionContext('12', 2)).toBeUndefined();
  });

  it('finds the function call the caret is in, the innermost first', () => {
    expect(callAt('lower(', 6)).toBe('lower');
    expect(callAt('substring(lower(x), 1', 21)).toBe('substring');
    expect(callAt('substring(lower(x', 17)).toBe('lower');
    expect(callAt('(a + b', 6)).toBeUndefined();
    expect(callAt("lower('(", 8)).toBe('lower');
  });

  it('lists names before functions, ignoring case', () => {
    const items = completionItems('L', names, [{ name: 'lower', minArguments: 1, maxArguments: 1, signature: 'lower(text)', description: '' }]);
    expect(items.map((i) => [i.label, i.insert])).toEqual([['lower', 'lower(']]);
    expect(completionItems('', names, []).map((i) => i.detail)).toEqual(['argument · String', 'argument · String']);
  });
});

describe('Expression completion', () => {
  it('asks the server for the names in scope at the field, and completes a name with the keyboard', async () => {
    const { api, field, message } = await renderStudio();

    expect(api.scopeRequests).toEqual(['$.root.children[1].properties.message']);
    await type(field, 'gr');
    expect(field.getAttribute('aria-expanded')).toBe('true');
    expect(options()).toEqual(['greeting']);

    await key(field, 'Enter');

    expect(message()).toBe('greeting');
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(field.getAttribute('aria-expanded')).toBe('false');
  });

  it('completes a function with its parenthesis and shows its signature inside the call', async () => {
    const { field, message } = await renderStudio();

    await type(field, 'lo');
    await key(field, 'Tab');

    expect(message()).toBe('lower(');
    expect(screen.getByText('lower(text)').closest('.signature-hint')!.textContent).toBe('lower(text) — The text in lower case.');
    expect(field.getAttribute('aria-describedby')).toContain('-signature');
  });

  it('Ctrl+Space lists everything; the arrows move; Escape closes', async () => {
    const { field } = await renderStudio();
    await type(field, '');

    await key(field, ' ', { ctrlKey: true });
    expect(options()).toEqual(['userName', 'greeting', 'len', 'lower', 'now', 'substring']);
    await key(field, 'ArrowDown');
    await key(field, 'ArrowUp');
    await key(field, 'ArrowUp');
    expect(field.getAttribute('aria-activedescendant')).toBe(list().getAllByRole('option')[5].id);
    expect(list().getAllByRole('option')[5].getAttribute('aria-selected')).toBe('true');

    await key(field, 'Escape');
    expect(screen.queryByRole('listbox')).toBeNull();
  });

  it('never opens inside a string or after a dot', async () => {
    const { field } = await renderStudio();

    await type(field, "'gr");
    expect(screen.queryByRole('listbox')).toBeNull();
    await type(field, 'x.gr');
    expect(screen.queryByRole('listbox')).toBeNull();
  });

  it('accepts an entry chosen with the mouse', async () => {
    const { field, message } = await renderStudio();
    await type(field, 'us');

    await act(async () => fireEvent.mouseDown(list().getByRole('option', { name: /userName/ })));

    expect(message()).toBe('userName');
  });
});
