import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ApiError } from './api';
import { App } from './App';
import { Studio } from './studio';
import { FakeApi, FakeEventSource, immediately, settle } from './test-support';
import type { ExecutionEvent, GenerationAnswer } from './types';

// The AI Workflow Assistant in the Studio (ADR-0047): propose, clarify, review, then apply or discard. Nothing runs.

const greet = {
  schemaVersion: '1.0',
  id: 'greet',
  name: 'Greet',
  version: '1.0.0',
  root: { id: 'main', type: 'Core.Sequence', children: [{ id: 'say', type: 'Core.Log', properties: { message: "'Hello'" } }] },
};

const answer = (outcome: GenerationAnswer['outcome'], extra: Partial<GenerationAnswer> = {}): GenerationAnswer => ({
  outcome,
  known: [],
  assumptions: [],
  questions: [],
  missing: [],
  plan: [],
  summary: '',
  document: null,
  diagnostics: [],
  repairAttempts: 0,
  activityTypes: [],
  sideEffects: [],
  usage: null,
  ...extra,
});

const proposal = (document: object, extra: Partial<GenerationAnswer> = {}) =>
  answer('proposal', { document: JSON.stringify(document), plan: ['Log a greeting'], summary: 'Logs a greeting.', assumptions: ['English is fine'], activityTypes: ['Core.Sequence', 'Core.Log'], ...extra });

async function renderStudio(api = new FakeApi(), open?: string) {
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { validateDelayMs: undefined });
  render(<App studio={studio} />);
  await act(settle);
  if (open) {
    await act(async () => studio.open(open));
  }

  await act(async () => studio.showOutput('assistant'));
  return { studio, api };
}

const panel = () => within(screen.getByRole('tabpanel', { name: /Assistant/ }));
const click = (name: string | RegExp) =>
  act(async () => {
    fireEvent.click(panel().getByRole('button', { name }));
    await settle();
  });
const describeRequest = (text: string) => fireEvent.change(panel().getByLabelText('Describe what to build or change'), { target: { value: text } });

beforeEach(() => {
  FakeEventSource.instances = [];
});

afterEach(cleanup);

describe('AI Workflow Assistant', () => {
  it('says why it is unavailable, and offers nothing that would send a request', async () => {
    const api = new FakeApi();
    api.assistant = { available: false, reason: 'No AI provider is configured.' };
    await renderStudio(api);

    describeRequest('Greet someone');

    expect(panel().getByTestId('assistant-status').textContent).toBe('The assistant is unavailable: No AI provider is configured.');
    expect((panel().getByRole('button', { name: 'New workflow' }) as HTMLButtonElement).disabled).toBe(true);
    expect((panel().getByRole('button', { name: 'Explain this workflow' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('creates a new workflow from a reviewed proposal as a new file, and never runs it', async () => {
    const api = new FakeApi();
    api.generateAnswers = [proposal(greet, { sideEffects: ['FileSystem'] })];
    const { studio } = await renderStudio(api);

    describeRequest('Greet someone');
    await click('New workflow');

    expect(api.assistantRequests).toEqual([{ operation: 'generate', body: { purpose: 'create', request: 'Greet someone', document: undefined, runId: undefined } }]);
    const review = within(panel().getByTestId('assistant-proposal'));
    expect(review.getByText('Log a greeting')).toBeTruthy();
    expect(review.getByText('English is fine')).toBeTruthy();
    expect(review.getByTestId('assistant-side-effects').textContent).toBe('Touches: files');
    expect(review.getByText('A new workflow with 2 activities.')).toBeTruthy();

    await click('Create as new file…');
    const dialog = within(screen.getByRole('dialog'));
    expect((dialog.getByLabelText('Path in the project') as HTMLInputElement).value).toBe('greet.json');
    await act(async () => {
      fireEvent.click(dialog.getByRole('button', { name: 'Create' }));
      await settle();
    });

    expect(JSON.parse(api.files.get('greet.json')!.text).id).toBe('greet');
    expect(studio.store.get().file?.path).toBe('greet.json');
    expect(studio.store.get().runs).toEqual([]);
    expect(FakeEventSource.instances).toEqual([]);
  });

  it('asks focused questions first, and sends the answers with the conversation', async () => {
    const api = new FakeApi();
    api.generateAnswers = [
      answer('clarify', { known: ['invoices come from the API'], assumptions: ['today is the local date'], questions: [{ question: 'Skip or update duplicates?', options: ['Skip', 'Update'] }] }),
      proposal(greet),
    ];
    await renderStudio(api);

    describeRequest("Download today's invoices");
    await click('New workflow');
    const clarify = within(panel().getByTestId('assistant-clarify'));
    expect(clarify.getByText('invoices come from the API')).toBeTruthy();
    expect(clarify.getByText('today is the local date')).toBeTruthy();
    fireEvent.click(clarify.getByRole('button', { name: 'Skip' }));
    expect((clarify.getByLabelText('Your answers') as HTMLTextAreaElement).value).toBe('1. Skip');
    await click('Continue');

    expect(api.assistantRequests[1].body).toEqual({
      purpose: 'create',
      request: '1. Skip',
      document: undefined,
      conversation: [
        { role: 'user', text: "Download today's invoices" },
        { role: 'assistant', text: 'Skip or update duplicates?' },
      ],
      runId: undefined,
    });
    expect(panel().getByTestId('assistant-proposal')).toBeTruthy();
  });

  it('reports an unsupported request honestly, and an invalid proposal cannot be applied', async () => {
    const api = new FakeApi();
    api.generateAnswers = [
      answer('unsupported', { missing: ['reading a document scanner'], summary: 'No activity reads a scanner.' }),
      answer('invalid', { repairAttempts: 2, document: JSON.stringify(greet), diagnostics: [{ code: 'MYRPA1033', severity: 'Error', message: "Unknown activity type 'Shop.Order'.", nodeId: 'order' }] }),
    ];
    await renderStudio(api);

    describeRequest('Scan the invoices');
    await click('New workflow');
    expect(within(panel().getByTestId('assistant-unsupported')).getByText('reading a document scanner')).toBeTruthy();

    await click('New workflow');
    const invalid = within(panel().getByTestId('assistant-invalid'));
    expect(invalid.getByText(/after 2 corrections/)).toBeTruthy();
    expect(invalid.getByText("MYRPA1033 (order): Unknown activity type 'Shop.Order'.")).toBeTruthy();
    expect(invalid.queryByRole('button', { name: /Apply|Create as new file/ })).toBeNull();
  });

  it('applies a reviewed change to the open workflow as one undo step, showing what changes', async () => {
    const api = new FakeApi();
    const { studio } = await renderStudio(api, 'hello-world.json');
    const before = studio.store.get().document!;
    const changed = JSON.parse(JSON.stringify(before));
    changed.root.children[1].properties.message = "'Hi ' + userName";
    api.generateAnswers = [proposal(changed)];

    describeRequest('Say Hi instead of Hello');
    await click('Change this workflow');

    expect((api.assistantRequests[0].body as { purpose: string; document: unknown }).purpose).toBe('edit');
    expect((api.assistantRequests[0].body as { document: unknown }).document).toBe(before);
    expect(studio.store.get().document).toBe(before);
    expect(within(panel().getByRole('list', { name: 'Changes' })).getByText(/changed log-greeting \(Core\.Log\): properties message/)).toBeTruthy();

    await click('Apply to this workflow');
    const applied = studio.store.get().document! as { root: { children: { properties: { message: string } }[] } };
    expect(applied.root.children[1].properties.message).toBe("'Hi ' + userName");
    expect(studio.store.get().runs).toEqual([]);

    await act(async () => studio.undo());
    expect(studio.store.get().document).toBe(before);
  });

  it('refuses to apply a change when the workflow was edited after asking', async () => {
    const api = new FakeApi();
    const { studio } = await renderStudio(api, 'hello-world.json');
    api.generateAnswers = [proposal({ ...studio.store.get().document!, name: 'Proposed' })];
    describeRequest('Rename it');
    await click('Change this workflow');

    await act(async () => studio.editWorkflow('Rename', undefined, (d) => ({ ...d, name: 'Edited meanwhile' })));
    await click('Apply to this workflow');

    expect(panel().getByRole('alert').textContent).toContain('The workflow changed after the assistant was asked');
    expect(studio.store.get().document!.name).toBe('Edited meanwhile');
  });

  it('shows a provider failure as a failure, and changes nothing', async () => {
    const api = new FakeApi();
    const { studio } = await renderStudio(api, 'hello-world.json');
    const before = studio.store.get().document;
    api.generateAnswers = [new ApiError(429, 'The AI endpoint is limiting requests (429).', { kind: 'RateLimited' })];

    describeRequest('Add a log');
    await click('Change this workflow');

    expect(panel().getByRole('alert').textContent).toBe('The assistant could not answer: The AI endpoint is limiting requests (429). Nothing was changed.');
    expect(studio.store.get().document).toBe(before);
    expect(panel().queryByTestId('assistant-proposal')).toBeNull();
  });

  it('can be cancelled while it is working', async () => {
    const api = new FakeApi();
    api.generateAnswers = [proposal(greet)];
    api.assistantHold = new Promise(() => {});
    await renderStudio(api);

    describeRequest('Greet someone');
    await click('New workflow');
    expect(panel().getByText('Asking the assistant…')).toBeTruthy();
    await click('Cancel');

    expect(panel().getByRole('alert').textContent).toBe('Stopped. Nothing was changed.');
    expect(panel().queryByTestId('assistant-proposal')).toBeNull();
  });

  it('explains the open workflow, with links that select the activities', async () => {
    const api = new FakeApi();
    api.explainAnswer = { summary: 'Builds and logs a greeting.', steps: [{ nodeId: 'log-greeting', text: 'Writes the greeting.' }] };
    const { studio } = await renderStudio(api, 'hello-world.json');

    await click('Explain this workflow');
    await act(async () => fireEvent.click(within(panel().getByTestId('assistant-explanation')).getByRole('button', { name: 'log-greeting' })));

    expect(panel().getByText('Builds and logs a greeting.')).toBeTruthy();
    const selected = studio.store.get().selectedKey;
    expect(document.querySelector('[role="treeitem"][data-node-id="log-greeting"]')!.getAttribute('aria-selected')).toBe('true');
    expect(selected).toBeDefined();
  });

  it('shows exactly what would be sent, without sending it', async () => {
    const api = new FakeApi();
    await renderStudio(api);

    describeRequest('Greet someone');
    await click('What is sent');

    expect(panel().getByTestId('assistant-preview').textContent).toContain('You are the MyRPA workflow assistant.');
    expect(api.assistantRequests.map((r) => r.operation)).toEqual(['preview']);
  });

  it('offers a fix for validation problems from the Problems tab', async () => {
    const api = new FakeApi();
    api.validation = { valid: false, diagnostics: [{ code: 'MYRPA1020', severity: 'Error', message: "Required property 'message' is missing.", path: '$.root.children[1].properties', nodeId: 'log-greeting' }] };
    api.generateAnswers = [proposal(greet)];
    const { studio } = await renderStudio(api, 'hello-world.json');
    await act(async () => studio.validate());
    await act(async () => studio.showOutput('problems'));

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Fix with the assistant' }));
      await settle();
    });

    expect(api.assistantRequests[0]).toMatchObject({ operation: 'generate', body: { purpose: 'fixValidation', request: '' } });
    expect(studio.store.get().outputTab).toBe('assistant');
  });

  it('explains why a run failed from the Execution tab, then proposes a fix for that run', async () => {
    const api = new FakeApi();
    api.debugAnswer = { explanation: 'The message is empty.', certainty: 'possible', nodeId: 'say', suggestions: ['Give it a message.'], failure: { status: 'Failed', code: 'MYRPA2001' } };
    api.generateAnswers = [proposal(greet)];
    // No input arguments, so Run starts at once.
    api.files.set('greet.json', { text: JSON.stringify(greet), etag: 1 });
    const { studio } = await renderStudio(api, 'greet.json');
    await act(async () => studio.showOutput('execution'));
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Run' }));
      await settle();
    });
    const base = { runId: 'run-1', time: '2026-10-11T10:00:00Z', executionId: 'e1' };
    await act(async () => {
      const stream = FakeEventSource.instances.at(-1)!;
      stream.emit({ ...base, sequence: 1, kind: 'execution.started' } as ExecutionEvent);
      stream.emit({ ...base, sequence: 2, kind: 'execution.completed', status: 'Failed', error: { code: 'MYRPA2001', message: 'Boom.', nodeId: 'say' } } as ExecutionEvent);
      await settle();
    });

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Ask the assistant why' }));
      await settle();
    });
    const debug = within(panel().getByTestId('assistant-debug'));
    expect(debug.getByText('Possible cause')).toBeTruthy();
    expect(debug.getByText('Give it a message.')).toBeTruthy();
    expect(api.assistantRequests[0]).toMatchObject({ operation: 'debug', body: { runId: 'run-1' } });

    await click('Propose a fix');
    expect(api.assistantRequests[1]).toMatchObject({ operation: 'generate', body: { purpose: 'fixFailure', runId: 'run-1' } });
  });
});
