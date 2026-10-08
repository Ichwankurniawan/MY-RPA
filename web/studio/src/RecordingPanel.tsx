// The Recorder tab (Phase 6, ADR-0039): start a recording at a web address, watch the steps arrive while you work in
// the recording browser, keep or remove them, choose another selector or change typed text, then insert them at the
// selection as the browser plugin's activities (one undo step). Recorded steps are suggestions; nothing here runs.

import { useId } from 'react';
import { useStudio, useStudioState } from './context';
import { Icon } from './icons';
import { recordingInsertRefusal, type RecordedItem } from './studio';

const kinds: Record<string, string> = { navigate: 'Go to', click: 'Click', type: 'Type', select: 'Select', upload: 'Upload', download: 'Download' };

export function RecordingPanel() {
  const studio = useStudio();
  const recorder = useStudioState((s) => s.recorder);
  const insertRefusal = useStudioState(recordingInsertRefusal);
  const urlId = useId();

  if (recorder === undefined) {
    return (
      <div className="recorder">
        <p className="hint">Record what you do on a website; the steps become browser activities in this workflow.</p>
        <button type="button" className="with-icon" onClick={() => studio.openRecorder()}>
          <Icon name="record" size={16} />
          <span>New recording</span>
        </button>
      </div>
    );
  }

  const status =
    recorder.phase === 'setup' ? 'Not started'
    : recorder.phase === 'starting' ? 'Opening the recording browser…'
    : recorder.phase === 'recording' ? 'Recording: do the steps in the browser window, then press Stop.'
    : `Stopped${recorder.endReason === 'BrowserClosed' ? ' (the browser was closed)' : ''}: review the steps, then insert them.`;

  return (
    <div className="recorder">
      {recorder.phase === 'setup' ? (
        <form
          className="recorder-start"
          onSubmit={(event) => {
            event.preventDefault();
            void studio.startRecording();
          }}
        >
          <div className="field">
            <label className="field-label" htmlFor={urlId}>
              Start at
            </label>
            <input id={urlId} type="url" value={recorder.startUrl} spellCheck={false} aria-describedby={`${urlId}-hint`} onChange={(event) => studio.setRecorderUrl(event.target.value)} />
            <small id={`${urlId}-hint`}>The address of the first page. A browser window opens there; do the steps in it.</small>
          </div>
          <button type="submit" className="with-icon" disabled={recorder.unavailable !== undefined} title={recorder.unavailable}>
            <Icon name="record" size={16} />
            <span>Start recording</span>
          </button>
        </form>
      ) : (
        <p className="recorder-status" role="status" data-testid="recorder-status">
          {status}
        </p>
      )}
      {recorder.unavailable !== undefined && <p className="field-error">{recorder.unavailable}</p>}
      {recorder.error !== undefined && <p className="field-error">{recorder.error}</p>}
      {recorder.items.length > 0 && (
        <ol className="recorded-steps" aria-label="Recorded steps">
          {recorder.items.map((item, i) => (
            <RecordedRow key={item.step.sequence} item={item} index={i} />
          ))}
        </ol>
      )}
      <div className="recorder-actions">
        {recorder.phase === 'recording' && (
          <button type="button" className="with-icon" onClick={() => void studio.stopRecording()}>
            <Icon name="stop" size={16} />
            <span>Stop</span>
          </button>
        )}
        {recorder.phase !== 'setup' && (
          <button type="button" className="with-icon" disabled={insertRefusal !== undefined} title={insertRefusal ?? 'Insert the steps at the selection as browser activities'} onClick={() => void studio.insertRecording()}>
            <Icon name="paste" size={16} />
            <span>Insert {recorder.items.length} steps</span>
          </button>
        )}
        <button type="button" onClick={() => void studio.discardRecording()}>
          Discard
        </button>
      </div>
    </div>
  );
}

function RecordedRow({ item, index }: { item: RecordedItem; index: number }) {
  const studio = useStudio();
  const id = useId();
  const { step } = item;
  const selector = item.selector ?? step.selector ?? '';
  const options = [step.selector, ...(step.alternatives ?? [])].filter((s): s is string => typeof s === 'string' && s !== '');
  const what = `Step ${index + 1}: ${kinds[step.kind] ?? step.kind}${step.element ? ` ${step.element}` : ''}`;
  return (
    <li className="recorded-step" data-kind={step.kind}>
      <span className="recorded-what">{what}</span>
      {step.kind === 'navigate' && <code className="recorded-value">{step.url}</code>}
      {step.kind !== 'navigate' && (
        <span className="field">
          <label className="field-label" htmlFor={`${id}-selector`}>
            Selector
          </label>
          {options.length > 1 ? (
            <select id={`${id}-selector`} value={selector} onChange={(event) => studio.chooseRecordedSelector(step.sequence, event.target.value)}>
              {options.map((option) => (
                <option key={option} value={option}>
                  {option}
                </option>
              ))}
            </select>
          ) : (
            <input id={`${id}-selector`} className="code" value={selector} spellCheck={false} onChange={(event) => studio.chooseRecordedSelector(step.sequence, event.target.value)} />
          )}
        </span>
      )}
      {step.kind === 'type' && !step.secret && (
        <span className="field">
          <label className="field-label" htmlFor={`${id}-text`}>
            Text
          </label>
          <input id={`${id}-text`} value={item.text ?? step.text ?? ''} spellCheck={false} onChange={(event) => studio.editRecordedText(step.sequence, event.target.value)} />
        </span>
      )}
      {step.kind === 'type' && step.secret && <span className="recorded-value">A password (not recorded): it becomes the argument <code>password</code>.</span>}
      {(step.kind === 'select' || step.kind === 'upload') && <span className="recorded-value">{(step.values ?? []).join(', ')}</span>}
      {step.kind === 'download' && <span className="recorded-value">Saves as {step.fileName}</span>}
      <button type="button" className="small" aria-label={`Remove step ${index + 1}`} onClick={() => studio.removeRecordedStep(step.sequence)}>
        Remove
      </button>
    </li>
  );
}
