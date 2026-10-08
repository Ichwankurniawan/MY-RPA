// The bottom panel (W4B, ADR-0032; one tab strip with Execution since UX-2): Problems, Variables, Arguments. Rows are edited in place; every change
// is one document edit (typing in one field merges into one undo step), and each row shows its own diagnostics.

import { useEffect, useId, useRef, type KeyboardEvent, type ReactNode } from 'react';
import { useStudio, useStudioState } from './context';
import { isObject } from './document';
import { Icon, type IconName } from './icons';
import { JsonField } from './PropertyEditors';
import type { OutputTab } from './studio';
import type { Diagnostic, Json, JsonObject } from './types';
import { addRow, dataTypes, diagnosticTarget, directions, removeRow, rows, updateRow, type DataList } from './workflowData';

type Tab = OutputTab;

const tabs: readonly { id: Tab; label: string }[] = [
  { id: 'problems', label: 'Problems' },
  { id: 'variables', label: 'Variables' },
  { id: 'arguments', label: 'Arguments' },
  { id: 'execution', label: 'Execution' },
  { id: 'recording', label: 'Recorder' },
];

/**
 * The bottom panel (UX-2): one tab strip for Problems, Variables, Arguments and Execution, with counts. The tab is Studio
 * state, so the Studio can show Execution when a run starts and Problems when an explicit check finds errors.
 */
export function BottomPanel({ execution, recording }: { execution: ReactNode; recording: ReactNode }) {
  const studio = useStudio();
  const id = useId();
  const tab = useStudioState((s) => s.outputTab);
  const errors = useStudioState((s) => s.diagnostics?.filter((d) => d.severity === 'Error').length ?? 0);
  const variables = useStudioState((s) => (s.document ? rows(s.document, 'variables').length : 0));
  const args = useStudioState((s) => (s.document ? rows(s.document, 'arguments').length : 0));
  const recorded = useStudioState((s) => s.recorder?.items.length);
  const counts: Record<Tab, string> = { problems: errors > 0 ? ` (${errors})` : '', variables: ` (${variables})`, arguments: ` (${args})`, execution: '', recording: recorded ? ` (${recorded})` : '' };
  const tabRefs = useRef<Partial<Record<Tab, HTMLButtonElement | null>>>({});

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const at = tabs.findIndex((t) => t.id === tab);
    const next =
      event.key === 'ArrowRight' ? tabs[(at + 1) % tabs.length]
      : event.key === 'ArrowLeft' ? tabs[(at + tabs.length - 1) % tabs.length]
      : event.key === 'Home' ? tabs[0]
      : event.key === 'End' ? tabs[tabs.length - 1]
      : undefined;
    if (next) {
      event.preventDefault();
      studio.showOutput(next.id);
      tabRefs.current[next.id]?.focus();
    }
  };

  return (
    <div className="bottom-panel">
      <div role="tablist" aria-label="Output" className="tabs" onKeyDown={onKeyDown}>
        {tabs.map((t) => (
          <button
            key={t.id}
            ref={(element) => {
              tabRefs.current[t.id] = element;
            }}
            type="button"
            role="tab"
            id={`${id}-${t.id}`}
            className={t.id === 'problems' && errors > 0 ? 'has-errors' : undefined}
            aria-selected={tab === t.id}
            aria-controls={`${id}-${t.id}-panel`}
            tabIndex={tab === t.id ? 0 : -1}
            onClick={() => studio.showOutput(t.id)}
          >
            <Icon name={tabIcons[t.id]} size={14} />
            {t.label}
            {counts[t.id]}
          </button>
        ))}
      </div>
      <div role="tabpanel" id={`${id}-${tab}-panel`} aria-labelledby={`${id}-${tab}`} className="tab-panel">
        {tab === 'problems' ? <ProblemsList /> : tab === 'execution' ? execution : tab === 'recording' ? recording : <RowsEditor list={tab} />}
      </div>
    </div>
  );
}

const tabIcons: Record<Tab, IconName> = { problems: 'problems', variables: 'activity', arguments: 'plugin', execution: 'run', recording: 'record' };

function ProblemsList() {
  const studio = useStudio();
  const diagnostics = useStudioState((s) => s.diagnostics);
  const validated = useStudioState((s) => s.validated);
  const stale = useStudioState((s) => s.diagnostics !== undefined && s.validated !== s.document);
  return (
    <div className="problems">
      <h2>Problems{stale ? ' (checking…)' : ''}</h2>
      {diagnostics === undefined ? (
        <p className="hint">Not validated yet.</p>
      ) : diagnostics.length === 0 ? (
        <p data-testid="no-problems">No problems.</p>
      ) : (
        <ul aria-label="Problems">
          {diagnostics.map((d, i) => (
            <li key={i} className={d.severity.toLowerCase()}>
              <button type="button" onClick={() => studio.goToDiagnostic(d)}>
                {d.severity} {d.code}
                {where(d, validated)}: {d.message}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function where(diagnostic: Diagnostic, validated: JsonObject | undefined): string {
  const target = diagnosticTarget(diagnostic, validated);
  return target.kind === 'node' ? ` (${target.nodeId ?? 'activity'}${target.property ? `.${target.property}` : ''})`
    : target.kind === 'row' ? ` (${target.list === 'arguments' ? 'argument' : 'variable'} ${target.index + 1})`
    : ' (workflow)';
}

/** The variables or arguments table. */
function RowsEditor({ list }: { list: DataList }) {
  const studio = useStudio();
  const document = useStudioState((s) => s.document);
  const diagnostics = useStudioState((s) => s.diagnostics);
  const validated = useStudioState((s) => s.validated);
  const disabled = useStudioState((s) => s.file?.readOnlyReason !== undefined);
  const rowFocus = useStudioState((s) => s.rowFocus);
  const table = useRef<HTMLTableElement>(null);
  const singular = list === 'arguments' ? 'argument' : 'variable';

  useEffect(() => {
    if (rowFocus?.list === list) {
      table.current?.querySelector<HTMLElement>(`[data-row="${rowFocus.index}"] input`)?.focus();
    }
  }, [rowFocus, list]);

  if (document === undefined) {
    return <p className="hint">Open a workflow.</p>;
  }

  const data = rows(document, list);
  const rowErrors = (index: number) =>
    (diagnostics ?? []).filter((d) => {
      const target = diagnosticTarget(d, validated);
      return target.kind === 'row' && target.list === list && target.index === index;
    });
  const edit = (label: string, mergeKey: string | undefined, apply: (d: JsonObject) => JsonObject) => studio.editWorkflow(label, mergeKey, apply);

  return (
    <div className="rows-editor">
      {data.length === 0 ? (
        <p className="hint">
          No {list} yet.{' '}
          {list === 'arguments' ? 'Arguments are the values a run passes in (In) and gets back (Out).' : 'Variables hold values while the workflow runs.'}
        </p>
      ) : (
        <table ref={table} aria-label={list === 'arguments' ? 'Arguments' : 'Variables'}>
          <thead>
            <tr>
              <th>Name</th>
              {list === 'arguments' && <th>Direction</th>}
              <th>Type</th>
              {list === 'arguments' && <th>Required</th>}
              <th>Default (JSON)</th>
              <th>
                <span className="visually-hidden">Remove</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {data.map((row, index) => {
              if (!isObject(row)) {
                return (
                  <tr key={index} data-row={index}>
                    <td colSpan={6} className="hint">
                      Row {index + 1} is not an object: {JSON.stringify(row)}
                    </td>
                  </tr>
                );
              }

              const name = typeof row.name === 'string' ? row.name : '';
              const out = row.direction === 'Out';
              const errors = rowErrors(index);
              const set = (field: string, value: Json | undefined, merge = true) =>
                edit(`Edit ${singular} ${field}`, merge ? `${list}:${index}:${field}` : undefined, (d) => updateRow(d, list, index, { [field]: value }));
              return (
                <tr key={index} data-row={index} className={errors.length > 0 ? 'invalid' : undefined}>
                  <td>
                    <input className="code" aria-label={`${singular} ${index + 1} name`} value={name} disabled={disabled} spellCheck={false} aria-invalid={errors.length > 0} onChange={(e) => set('name', e.target.value)} />
                    {errors.length > 0 && <span className="field-error">{errors.map((d) => `${d.code}: ${d.message}`).join(' ')}</span>}
                  </td>
                  {list === 'arguments' && (
                    <td>
                      <select aria-label={`${singular} ${index + 1} direction`} value={typeof row.direction === 'string' ? row.direction : ''} disabled={disabled} onChange={(e) => set('direction', e.target.value, false)}>
                        <Options values={directions} current={row.direction} />
                      </select>
                    </td>
                  )}
                  <td>
                    <select aria-label={`${singular} ${index + 1} type`} value={typeof row.type === 'string' ? row.type : ''} disabled={disabled} onChange={(e) => set('type', e.target.value, false)}>
                      <Options values={dataTypes} current={row.type} />
                    </select>
                  </td>
                  {list === 'arguments' && (
                    <td>
                      <input
                        type="checkbox"
                        aria-label={`${singular} ${index + 1} required`}
                        checked={row.required === true}
                        disabled={disabled || out}
                        title={out ? 'Out arguments are never required.' : undefined}
                        onChange={(e) => set('required', e.target.checked ? true : undefined, false)}
                      />
                    </td>
                  )}
                  <td>
                    <JsonField
                      label={`${singular} ${index + 1} default`}
                      value={row.default}
                      disabled={disabled || out}
                      placeholder={out ? 'Out: no default' : 'none'}
                      onCommit={(value) => set('default', value)}
                    />
                  </td>
                  <td>
                    <button type="button" className="small" aria-label={`Remove ${singular} ${name || index + 1}`} disabled={disabled} onClick={() => edit(`Remove ${singular} ${name}`, undefined, (d) => removeRow(d, list, index))}>
                      ×
                    </button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      <button type="button" disabled={disabled} onClick={() => edit(`Add ${singular}`, undefined, (d) => addRow(d, list))}>
        Add {singular}
      </button>
    </div>
  );
}

/** The allowed values, plus the current one when it is not among them (validation reports it). */
function Options({ values, current }: { values: readonly string[]; current: Json | undefined }) {
  const all = typeof current === 'string' && current !== '' && !values.includes(current) ? [...values, current] : values;
  return (
    <>
      {typeof current !== 'string' && <option value="">(not set)</option>}
      {all.map((value) => (
        <option key={value} value={value}>
          {value}
        </option>
      ))}
    </>
  );
}
