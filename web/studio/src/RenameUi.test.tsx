import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { nodeAt } from './document';
import { Studio } from './studio';
import { FakeApi, FakeEventSource, immediately, settle } from './test-support';
import type { JsonObject, NameReferences } from './types';

// Rename and usages (ADR-0041, E-3): the uses come from the server; the renamed workflow is validated before it is applied.

const greeting: NameReferences = {
  declaration: { name: 'greeting', kind: 'Argument', type: 'String', direction: 'Out', path: '$.arguments[1]' },
  references: [
    { path: '$.arguments[1].name', start: 0, length: 8, declaration: true },
    { path: '$.root.children[0].properties.to', start: 0, length: 8, declaration: false },
    { path: '$.root.children[1].properties.message', start: 0, length: 8, declaration: false },
  ],
};

async function renderStudio(api = new FakeApi()) {
  api.referenceAnswers.set('greeting', greeting);
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { validateDelayMs: undefined });
  render(<App studio={studio} />);
  await act(settle);
  await act(async () => {
    await studio.open('hello-world.json');
  });
  fireEvent.click(screen.getByRole('tab', { name: /^Arguments/ }));
  return { studio, api, state: () => studio.store.get() };
}

const click = (name: string) =>
  act(async () => {
    fireEvent.click(screen.getByRole('button', { name }));
    await settle();
  });
const dialog = () => within(screen.getByRole('dialog'));
const names = (document: JsonObject) => [
  (document.arguments as JsonObject[])[1].name,
  (nodeAt(document, [{ children: 0 }]).properties as JsonObject).to,
  (nodeAt(document, [{ children: 1 }]).properties as JsonObject).message,
];

async function renameTo(text: string) {
  fireEvent.change(dialog().getByLabelText('New name'), { target: { value: text } });
  await act(async () => {
    fireEvent.click(dialog().getByRole('button', { name: 'Rename' }));
    await settle();
  });
}

beforeEach(() => {
  FakeEventSource.instances = [];
});

afterEach(cleanup);

describe('Rename', () => {
  it('renames the declaration and every use the server found, as one undo step', async () => {
    const { studio, api, state } = await renderStudio();

    await click('Rename argument greeting');
    expect(api.referenceRequests).toEqual([{ path: '$.arguments[1]', name: 'greeting' }]);
    expect(dialog().getByText('The declaration and 2 uses will change.')).toBeTruthy();
    await renameTo('salutation');

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(names(state().document!)).toEqual(['salutation', 'salutation', 'salutation']);
    expect(api.validated.some((d) => names(d)[0] === 'salutation')).toBe(true);
    expect(state().message).toBe('Renamed greeting to salutation (the declaration and 2 uses).');

    act(() => studio.undo());
    expect(names(state().document!)).toEqual(['greeting', 'greeting', 'greeting']);
  });

  it('refuses a name the server finds a problem with, saying why, and changes nothing', async () => {
    const api = new FakeApi();
    api.validationFor = {
      contains: '"to":"userName"',
      result: { valid: false, diagnostics: [{ code: 'MYRPA1031', severity: 'Error', message: "Name 'userName' is declared more than once.", path: '$.arguments[1].name' }] },
    };
    const { state } = await renderStudio(api);
    const before = state().document;

    await click('Rename argument greeting');
    await renameTo('userName');

    expect(dialog().getByText("Not renamed: Name 'userName' is declared more than once.")).toBeTruthy();
    expect(state().document).toBe(before);
  });

  it('refuses when the workflow changed after the uses were found', async () => {
    const { studio, state } = await renderStudio();
    await click('Rename argument greeting');

    act(() => studio.editWorkflow('Edit workflow name', 'workflow:name', (d) => ({ ...d, name: 'Changed' })));
    await renameTo('salutation');

    expect(dialog().getByText('The workflow changed since its uses were found; open Rename again.')).toBeTruthy();
    expect(names(state().document!)).toEqual(['greeting', 'greeting', 'greeting']);
  });

  it('cannot rename a name the server does not find declared there', async () => {
    const { studio } = await renderStudio();

    await act(async () => {
      await studio.openRename('$.arguments[0]', 'userName');
    });

    expect(dialog().getByText("'userName' is not declared here, so it cannot be renamed.")).toBeTruthy();
    expect((dialog().getByRole('button', { name: 'Rename' }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('Usages', () => {
  it('lists the activities that use a name; choosing one selects it', async () => {
    const { state } = await renderStudio();

    await click('Usages of greeting');
    const uses = within(screen.getByRole('list', { name: 'Uses of greeting' }));
    expect(uses.getAllByRole('listitem').map((li) => li.textContent)).toEqual(['Assign to', 'Log message']);

    await act(async () => fireEvent.click(uses.getByRole('button', { name: 'Log' })));

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(state().selectedKey).toBe(document.querySelector<HTMLElement>('[role="treeitem"][data-node-id="log-greeting"]')!.dataset.key);
  });
});
