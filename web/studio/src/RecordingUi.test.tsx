import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { nodeAt } from './document';
import { Studio } from './studio';
import { FakeApi, FakeEventSource, immediately, settle } from './test-support';
import type { ExecutionEvent, JsonObject, RecordedStep, RecordingEvent } from './types';

// Phase 6 S-4 (ADR-0039): the Recorder tab. Steps arrive on the tab's event stream; the browser plugin's activities come
// from the server (here the fake API); inserting them is one undo step.

async function renderStudio(api = new FakeApi()) {
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { validateDelayMs: undefined });
  render(<App studio={studio} />);
  await act(settle);
  await act(async () => {
    await studio.open('hello-world.json');
  });
  return { studio, api, state: () => studio.store.get() };
}

const recorder = () => within(document.querySelector<HTMLElement>('.bottom-panel [role="tabpanel"]')!);
const steps = () => within(screen.getByRole('list', { name: 'Recorded steps' })).queryAllByRole('listitem');

let sequence = 0;
const step = (s: Partial<RecordedStep> & Pick<RecordedStep, 'sequence' | 'kind'>): RecordingEvent => ({
  sequence: ++sequence,
  kind: 'recording.step',
  recordingId: 'rec-1',
  time: '2026-10-08T10:00:00Z',
  step: { alternatives: [], values: [], secret: false, ...s },
});

const emit = (...events: RecordingEvent[]) =>
  act(async () => {
    events.forEach((e) => FakeEventSource.instances.at(-1)!.emit(e as unknown as ExecutionEvent));
    await settle();
  });

async function startRecording(url = 'http://localhost:5000/login') {
  fireEvent.click(screen.getByRole('button', { name: 'Record' }));
  await act(settle);
  fireEvent.change(recorder().getByLabelText('Start at'), { target: { value: url } });
  await act(async () => {
    fireEvent.click(recorder().getByRole('button', { name: 'Start recording' }));
    await settle();
  });
}

beforeEach(() => {
  FakeEventSource.instances = [];
  sequence = 0;
});

afterEach(cleanup);

describe('Recorder', () => {
  it('starts a recording at a web address and follows it on the tab’s event stream', async () => {
    const { api } = await renderStudio();
    fireEvent.click(screen.getByRole('button', { name: 'Record' }));
    await act(settle);

    expect(screen.getByRole('tab', { name: 'Recorder' }).getAttribute('aria-selected')).toBe('true');
    fireEvent.change(recorder().getByLabelText('Start at'), { target: { value: 'ftp://nope' } });
    fireEvent.click(recorder().getByRole('button', { name: 'Start recording' }));
    await act(settle);
    expect(recorder().getByText(/Enter the address of a web page/)).toBeTruthy();
    expect(api.recordingsStarted).toEqual([]);

    fireEvent.change(recorder().getByLabelText('Start at'), { target: { value: 'http://localhost:5000/login' } });
    await act(async () => {
      fireEvent.click(recorder().getByRole('button', { name: 'Start recording' }));
      await settle();
    });

    expect(api.recordingsStarted).toEqual(['http://localhost:5000/login']);
    expect(api.recordingSubscriptions.map((s) => s.recordingId)).toEqual(['rec-1']);
    expect(screen.getByTestId('recorder-status').textContent).toMatch(/^Recording/);
  });

  it('shows the steps as they arrive: typing replaces the earlier step, and a password is never shown', async () => {
    await renderStudio();
    await startRecording();

    await emit(step({ sequence: 1, kind: 'type', selector: 'label=Email', alternatives: ['attr=name=email'], element: 'textbox "Email"', text: 'a' }));
    await emit(step({ sequence: 2, kind: 'type', selector: 'label=Email', alternatives: ['attr=name=email'], element: 'textbox "Email"', text: 'ada@example.com', replaces: 1 }));
    await emit(step({ sequence: 3, kind: 'type', selector: 'label=Password', element: 'textbox "Password"', secret: true }));
    await emit(step({ sequence: 4, kind: 'click', selector: 'role=button|Sign in', element: 'button "Sign in"' }));

    expect(steps().map((li) => li.querySelector('.recorded-what')!.textContent)).toEqual([
      'Step 1: Type textbox "Email"',
      'Step 2: Type textbox "Password"',
      'Step 3: Click button "Sign in"',
    ]);
    expect((within(steps()[0]).getByLabelText('Text') as HTMLInputElement).value).toBe('ada@example.com');
    expect(within(steps()[1]).queryByLabelText('Text')).toBeNull();
    expect(steps()[1].textContent).toContain('A password (not recorded)');
    expect(screen.getByRole('tab', { name: 'Recorder (3)' })).toBeTruthy();
  });

  it('lets the user choose another selector, change typed text and remove a step', async () => {
    const { state } = await renderStudio();
    await startRecording();
    await emit(step({ sequence: 1, kind: 'type', selector: 'label=Email', alternatives: ['attr=name=email'], text: 'a' }), step({ sequence: 2, kind: 'click', selector: 'testid=go' }));

    fireEvent.change(within(steps()[0]).getByLabelText('Selector'), { target: { value: 'attr=name=email' } });
    fireEvent.change(within(steps()[0]).getByLabelText('Text'), { target: { value: 'grace@example.com' } });
    fireEvent.click(recorder().getByRole('button', { name: 'Remove step 2' }));

    expect(state().recorder!.items).toEqual([expect.objectContaining({ selector: 'attr=name=email', text: 'grace@example.com' })]);
    expect(steps()).toHaveLength(1);
  });

  it('stops the recording; the steps stay to review', async () => {
    const { api } = await renderStudio();
    await startRecording();
    await emit(step({ sequence: 1, kind: 'click', selector: 'testid=go' }));

    await act(async () => {
      fireEvent.click(recorder().getByRole('button', { name: 'Stop' }));
      await settle();
    });
    await emit({ sequence: ++sequence, kind: 'recording.ended', recordingId: 'rec-1', time: 't', endReason: 'Stopped' });

    expect(api.recordingsStopped).toEqual(['rec-1']);
    expect(screen.getByTestId('recorder-status').textContent).toMatch(/^Stopped/);
    expect(steps()).toHaveLength(1);
  });

  it('inserts the plugin’s activities at the selection as one undo step, with the password argument', async () => {
    const { studio, api, state } = await renderStudio();
    await act(async () => fireEvent.click(document.querySelector('[role="treeitem"][data-node-id="main"]')!));
    await startRecording();
    await emit(
      step({ sequence: 1, kind: 'type', selector: 'label=Email', alternatives: ['attr=name=email'], text: 'ada@example.com' }),
      step({ sequence: 2, kind: 'type', selector: 'label=Password', secret: true }),
      step({ sequence: 3, kind: 'click', selector: 'role=button|Sign in' }),
    );
    fireEvent.change(within(steps()[0]).getByLabelText('Selector'), { target: { value: 'attr=name=email' } });

    await act(async () => {
      fireEvent.click(recorder().getByRole('button', { name: 'Insert 3 steps' }));
      await settle();
    });

    expect(api.recordingsStopped).toEqual(['rec-1']);
    expect(api.generated[0].steps.map((s) => s.selector)).toEqual(['attr=name=email', 'label=Password', 'role=button|Sign in']);
    const children = (nodeAt(state().document!, []).children as JsonObject[]).map((c) => c.id);
    expect(children).toEqual(['build-greeting', 'log-greeting', 'open-1', 'type-1', 'type-2', 'click-3', 'close-1']);
    expect((state().document!.arguments as JsonObject[]).map((a) => a.name)).toEqual(['userName', 'greeting', 'password']);
    expect(state().recorder).toBeUndefined();

    act(() => studio.undo());
    expect((nodeAt(state().document!, []).children as JsonObject[]).map((c) => c.id)).toEqual(['build-greeting', 'log-greeting']);
    expect((state().document!.arguments as JsonObject[]).map((a) => a.name)).toEqual(['userName', 'greeting']);
  });

  it('explains why it cannot record or insert', async () => {
    const api = new FakeApi();
    api.recordingAvailable = false;
    await renderStudio(api);
    fireEvent.click(screen.getByRole('button', { name: 'Record' }));
    await act(settle);

    const start = recorder().getByRole('button', { name: 'Start recording' }) as HTMLButtonElement;
    expect(start.disabled).toBe(true);
    expect(start.title).toBe('Recording needs the browser plugin.');
  });
});
