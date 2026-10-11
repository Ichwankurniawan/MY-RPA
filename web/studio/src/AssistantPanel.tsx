// The AI Workflow Assistant (ADR-0047, the owner's Phase 8 brief): describe, clarify, review, then apply or discard.
// Every answer is advice or a proposal checked by the server; applying never runs anything, and Undo reverts it.

import { useId, useMemo, useState } from 'react';
import { compareWorkflows, sideEffectWords } from './assistant';
import { useStudio, useStudioState } from './context';
import { Icon } from './icons';

const certaintyLabel = { likely: 'Likely cause', possible: 'Possible cause', unknown: 'Cause unknown' } as const;

export function AssistantPanel() {
  const studio = useStudio();
  const id = useId();
  const assistant = useStudioState((s) => s.assistant);
  const hasDocument = useStudioState((s) => s.document !== undefined);
  const readOnly = useStudioState((s) => s.file?.readOnlyReason !== undefined);
  const info = assistant.info;
  const available = info?.available === true;
  const busy = assistant.busy !== undefined;
  const words = assistant.request.trim() !== '';

  return (
    <section className="assistant" aria-labelledby={`${id}-heading`}>
      <h2 id={`${id}-heading`} className="visually-hidden">
        Assistant
      </h2>
      <p className="assistant-status" data-testid="assistant-status">
        {info === undefined
          ? 'Checking the assistant…'
          : available
            ? `AI provider: ${info.provider ?? 'unknown'}${info.model ? ` · ${info.model}` : ''}. It proposes; you review and decide. Nothing runs by itself.`
            : `The assistant is unavailable: ${info.reason ?? 'no AI provider.'}`}
      </p>
      <label className="field-label" htmlFor={`${id}-request`}>
        Describe what to build or change
      </label>
      <textarea
        id={`${id}-request`}
        rows={3}
        value={assistant.request}
        placeholder="e.g. Call the invoice API, keep this month's invoices and save them to a CSV file."
        onChange={(e) => studio.setAssistantRequest(e.target.value)}
      />
      <div className="assistant-actions" role="toolbar" aria-label="Assistant">
        <button type="button" className="primary" disabled={!available || busy || !words} onClick={() => void studio.askAssistant('create')} title="Propose a new workflow from the description">
          New workflow
        </button>
        <button type="button" disabled={!available || busy || !words || !hasDocument || readOnly} onClick={() => void studio.askAssistant('edit')} title={hasDocument ? 'Propose a change to the open workflow' : 'Open a workflow first'}>
          Change this workflow
        </button>
        <button type="button" disabled={!available || busy || !hasDocument} onClick={() => void studio.explainWorkflow()}>
          Explain this workflow
        </button>
        <button type="button" disabled={info === undefined || busy} onClick={() => void studio.previewAssistant('generate', hasDocument ? 'edit' : 'create')} title="Show exactly what a change request would send to the AI provider">
          What is sent
        </button>
        {busy && (
          <button type="button" onClick={() => studio.cancelAssistant()}>
            Cancel
          </button>
        )}
      </div>
      <div aria-live="polite" className="assistant-results">
        {busy && <p className="hint">{assistant.busy === 'preview' ? 'Preparing…' : 'Asking the assistant…'}</p>}
        {assistant.error && (
          <p className="field-error" role="alert">
            {assistant.error}
          </p>
        )}
        {assistant.answer && <Answer />}
        {assistant.explanation && <Explanation />}
        {assistant.debug && <Debug />}
        {assistant.preview && (
          <details className="assistant-answer" open>
            <summary>What is sent to the AI provider</summary>
            <pre data-testid="assistant-preview">
              {`INSTRUCTIONS\n${assistant.preview.instructions}\n\n${assistant.preview.messages.map((m) => `${m.role.toUpperCase()}\n${m.text}`).join('\n\n')}`}
            </pre>
          </details>
        )}
      </div>
    </section>
  );
}

function List({ title, items }: { title: string; items: readonly string[] }) {
  return items.length === 0 ? null : (
    <>
      <h3>{title}</h3>
      <ul>
        {items.map((item, i) => (
          <li key={i}>{item}</li>
        ))}
      </ul>
    </>
  );
}

function Answer() {
  const studio = useStudio();
  const id = useId();
  const { answer, proposal, purpose, base } = useStudioState((s) => s.assistant);
  const [reply, setReply] = useState('');
  const changes = useMemo(() => (proposal ? compareWorkflows(purpose === 'create' ? undefined : base, proposal) : undefined), [proposal, purpose, base]);
  if (!answer) {
    return null;
  }

  switch (answer.outcome) {
    case 'clarify':
      return (
        <div className="assistant-answer" data-testid="assistant-clarify">
          <h3>The assistant needs to know more</h3>
          <List title="Known" items={answer.known} />
          <List title="Would assume" items={answer.assumptions} />
          <ol className="assistant-questions">
            {answer.questions.map((q, i) => (
              <li key={i}>
                {q.question}
                {q.options.length > 0 && (
                  <span className="assistant-options">
                    {q.options.map((option) => (
                      <button key={option} type="button" className="chip" onClick={() => setReply((r) => (r.trim() === '' ? `${i + 1}. ${option}` : `${r}\n${i + 1}. ${option}`))}>
                        {option}
                      </button>
                    ))}
                  </span>
                )}
              </li>
            ))}
          </ol>
          <label className="field-label" htmlFor={`${id}-reply`}>
            Your answers
          </label>
          <textarea id={`${id}-reply`} rows={3} value={reply} onChange={(e) => setReply(e.target.value)} />
          <div className="assistant-actions">
            <button type="button" className="primary" disabled={reply.trim() === ''} onClick={() => void studio.answerAssistant(reply)}>
              Continue
            </button>
            <button type="button" onClick={() => studio.discardAssistantAnswer()}>
              Discard
            </button>
          </div>
        </div>
      );
    case 'unsupported':
      return (
        <div className="assistant-answer" data-testid="assistant-unsupported">
          <h3>Not possible with the available activities</h3>
          {answer.summary && <p>{answer.summary}</p>}
          <List title="Missing" items={answer.missing} />
          <div className="assistant-actions">
            <button type="button" onClick={() => studio.discardAssistantAnswer()}>
              Discard
            </button>
          </div>
        </div>
      );
    case 'invalid':
      return (
        <div className="assistant-answer" data-testid="assistant-invalid">
          <h3>No valid workflow</h3>
          <p>
            The assistant's proposal still failed the checks after {answer.repairAttempts} correction{answer.repairAttempts === 1 ? '' : 's'}, so it cannot be applied. Try a clearer or smaller
            request.
          </p>
          <ul aria-label="Why the proposal is invalid">
            {answer.diagnostics.map((d, i) => (
              <li key={i} className={d.severity.toLowerCase()}>
                {d.code}
                {d.nodeId ? ` (${d.nodeId})` : ''}: {d.message}
              </li>
            ))}
          </ul>
          <div className="assistant-actions">
            <button type="button" onClick={() => studio.discardAssistantAnswer()}>
              Discard
            </button>
          </div>
        </div>
      );
    default:
      return (
        <div className="assistant-answer" data-testid="assistant-proposal">
          <h3>
            <Icon name="check" size={14} /> Proposal: valid for this Studio's activities
          </h3>
          <List title="Plan" items={answer.plan} />
          {answer.summary && (
            <p>
              <span className="assistant-label">The assistant says: </span>
              {answer.summary}
            </p>
          )}
          <List title="Assumptions to check" items={answer.assumptions} />
          <p>
            <span className="assistant-label">Uses: </span>
            {answer.activityTypes.join(', ')}
          </p>
          <p data-testid="assistant-side-effects">
            <span className="assistant-label">Touches: </span>
            {answer.sideEffects.length > 0 ? sideEffectWords(answer.sideEffects) : 'nothing outside the workflow'}
          </p>
          {changes && purpose !== 'create' && (
            <>
              <h3>Changes</h3>
              {changes.nodes.length === 0 && changes.workflow.length === 0 ? (
                <p className="hint">No changes.</p>
              ) : (
                <ul aria-label="Changes" className="assistant-changes">
                  {changes.workflow.map((w) => (
                    <li key={w}>{w}</li>
                  ))}
                  {changes.nodes.map((c) => (
                    <li key={`${c.kind}:${c.id}`} className={`change-${c.kind}`}>
                      {c.kind} {c.id} ({c.type}){c.detail ? `: ${c.detail}` : ''}
                    </li>
                  ))}
                </ul>
              )}
            </>
          )}
          {changes && purpose === 'create' && <p className="hint">A new workflow with {changes.nodes.length} activities.</p>}
          {answer.diagnostics.length > 0 && <List title="Warnings" items={answer.diagnostics.map((d) => `${d.code}: ${d.message}`)} />}
          <div className="assistant-actions">
            <button type="button" className="primary" onClick={() => studio.applyProposal()}>
              {purpose === 'create' ? 'Create as new file…' : 'Apply to this workflow'}
            </button>
            <button type="button" onClick={() => studio.discardAssistantAnswer()}>
              Discard
            </button>
          </div>
          <p className="hint">Applying never runs it; Undo reverts an applied change. Run it yourself when you are ready.</p>
        </div>
      );
  }
}

function Explanation() {
  const studio = useStudio();
  const explanation = useStudioState((s) => s.assistant.explanation);
  if (!explanation) {
    return null;
  }

  return (
    <div className="assistant-answer" data-testid="assistant-explanation">
      <h3>What this workflow does</h3>
      <p>{explanation.summary}</p>
      {explanation.steps.length > 0 && (
        <ol>
          {explanation.steps.map((step) => (
            <li key={step.nodeId}>
              <button type="button" className="link" onClick={() => studio.selectNodeId(step.nodeId)}>
                {step.nodeId}
              </button>
              : {step.text}
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}

function Debug() {
  const studio = useStudio();
  const debug = useStudioState((s) => s.assistant.debug);
  if (!debug) {
    return null;
  }

  return (
    <div className="assistant-answer" data-testid="assistant-debug">
      <h3>{certaintyLabel[debug.certainty] ?? certaintyLabel.unknown}</h3>
      <p>{debug.explanation}</p>
      {debug.nodeId && (
        <p>
          <span className="assistant-label">Points to: </span>
          <button type="button" className="link" onClick={() => studio.selectNodeId(debug.nodeId!)}>
            {debug.nodeId}
          </button>
        </p>
      )}
      <List title="Suggestions" items={debug.suggestions} />
      <div className="assistant-actions">
        <button type="button" className="primary" onClick={() => void studio.askAssistant('fixFailure', '')}>
          Propose a fix
        </button>
        <button type="button" onClick={() => studio.discardAssistantAnswer()}>
          Discard
        </button>
      </div>
      <p className="hint">This is advice from the run's error only; the run is not repeated.</p>
    </div>
  );
}
