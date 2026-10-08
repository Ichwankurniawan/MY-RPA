// Properties (W4B, ADR-0032): the selected node's id, display name and one editor per catalog property kind, raw JSON
// for properties the catalog does not describe, and the workflow's metadata when the workflow itself is selected.
// Every change is a document edit (one undo step per field while typing); validation stays on the server.

import { useEffect, useId, useRef, useState } from 'react';
import { useStudio, useStudioState } from './context';
import { indexDocument, isObject, nodeAt, type Step } from './document';
import { graphParentPath, moveTransitionRefusal, stepsOf } from './graph';
import { addTransitionRefusalOf, setStartRefusalOf, workflowKey } from './studio';
import type { Diagnostic, Json, JsonObject, PropertyDescriptor } from './types';
import { assignableNames, diagnosticTarget, jsonText, parseJsonText, setMetadata, type MetadataField } from './workflowData';

const problemText = (diagnostics: readonly Diagnostic[]) => diagnostics.map((d) => `${d.code}: ${d.message}`).join(' ');

/** Diagnostics of a node's property (including entries of a map property). */
function propertyDiagnostics(diagnostics: readonly Diagnostic[] | undefined, validated: JsonObject | undefined, key: string, name: string): Diagnostic[] {
  return (diagnostics ?? []).filter((d) => {
    const target = diagnosticTarget(d, validated);
    return target.kind === 'node' && target.key === key && target.property === name;
  });
}

/**
 * The selected card's own editors (UX-3): the activity's properties with the same editors as the Properties panel (one
 * document, so both always agree). Id, display name and raw properties stay in the panel.
 */
export function InlineProperties({ nodeKey }: { nodeKey: string }) {
  const document = useStudioState((s) => s.document);
  const readOnlyReason = useStudioState((s) => s.file?.readOnlyReason);
  const diagnostics = useStudioState((s) => s.diagnostics);
  const validated = useStudioState((s) => s.validated);
  const catalog = useStudioState((s) => s.catalog);
  const entry = document ? indexDocument(document).byKey.get(nodeKey) : undefined;
  const type = typeof entry?.node.type === 'string' ? entry.node.type : '';
  const activity = catalog.get(type);
  if (document === undefined || entry === undefined || activity === undefined || activity.properties.length === 0) {
    return null;
  }

  const properties = isObject(entry.node.properties) ? entry.node.properties : {};
  const names = assignableNames(document);
  return (
    <div className="inline-properties">
      {activity.properties.map((descriptor) => (
        <PropertyEditor
          key={descriptor.name}
          nodeKey={nodeKey}
          descriptor={descriptor}
          value={properties[descriptor.name]}
          disabled={readOnlyReason !== undefined}
          names={names}
          errors={propertyDiagnostics(diagnostics, validated, nodeKey, descriptor.name)}
        />
      ))}
    </div>
  );
}

export function PropertiesPanel() {
  const selectedKey = useStudioState((s) => s.selectedKey);
  return (
    <aside className="properties" aria-labelledby="properties-heading">
      <h2 id="properties-heading">Properties</h2>
      {selectedKey === workflowKey ? <WorkflowProperties /> : <NodeProperties />}
    </aside>
  );
}

function WorkflowProperties() {
  const studio = useStudio();
  const document = useStudioState((s) => s.document);
  const diagnostics = useStudioState((s) => s.diagnostics);
  const validated = useStudioState((s) => s.validated);
  const disabled = useStudioState((s) => s.file?.readOnlyReason !== undefined);
  if (document === undefined) {
    return <p className="hint">Open a workflow.</p>;
  }

  const located = (diagnostics ?? []).flatMap((d) => {
    const target = diagnosticTarget(d, validated);
    return target.kind === 'workflow' ? [{ d, field: target.field }] : [];
  });
  const forField = (field: string) => located.filter((x) => x.field === field).map((x) => x.d);
  const fields: { field: MetadataField; label: string; hint: string; multiline?: boolean }[] = [
    { field: 'id', label: 'Id', hint: '1–128 characters from A–Z a–z 0–9 - _ . :' },
    { field: 'name', label: 'Name', hint: 'Display name of the workflow.' },
    { field: 'version', label: 'Version', hint: 'Your content version, for example 1.0.0.' },
    { field: 'description', label: 'Description', hint: 'Optional.', multiline: true },
  ];
  const others = located.filter((x) => !fields.some((f) => f.field === x.field)).map((x) => x.d);

  return (
    <>
      <dl className="facts">
        <dt>Selected</dt>
        <dd>Workflow</dd>
        <dt>Format</dt>
        <dd>{typeof document.schemaVersion === 'string' ? document.schemaVersion : ''}</dd>
      </dl>
      {fields.map(({ field, label, hint, multiline }) => (
        <TextField
          key={field}
          label={label}
          value={typeof document[field] === 'string' ? (document[field] as string) : ''}
          hint={hint}
          disabled={disabled}
          multiline={multiline}
          errors={forField(field)}
          onChange={(text) => studio.editWorkflow(`Edit workflow ${field}`, `workflow:${field}`, (d) => setMetadata(d, field, text))}
        />
      ))}
      {others.length > 0 && (
        <ul className="node-problems" aria-label="Problems of the workflow">
          {others.map((d, i) => (
            <li key={i} className={d.severity.toLowerCase()}>
              {d.code}: {d.message}
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

function NodeProperties() {
  const studio = useStudio();
  const document = useStudioState((s) => s.document);
  const selectedKey = useStudioState((s) => s.selectedKey);
  const readOnlyReason = useStudioState((s) => s.file?.readOnlyReason);
  const diagnostics = useStudioState((s) => s.diagnostics);
  const validated = useStudioState((s) => s.validated);
  const catalog = useStudioState((s) => s.catalog);
  const entry = document && selectedKey ? indexDocument(document).byKey.get(selectedKey) : undefined;

  if (document === undefined || entry === undefined) {
    return (
      <p className="hint">
        Select an activity, or{' '}
        <button type="button" className="link" disabled={document === undefined} onClick={() => studio.selectWorkflow()}>
          the workflow
        </button>{' '}
        to edit its details.
      </p>
    );
  }

  const node = entry.node;
  const type = typeof node.type === 'string' ? node.type : '';
  const activity = catalog.get(type);
  const properties = isObject(node.properties) ? node.properties : {};
  const known = new Set(activity?.properties.map((p) => p.name));
  const others = Object.entries(properties).filter(([name]) => !known.has(name));
  const nodeDiagnostics = (diagnostics ?? []).filter((d) => {
    const target = diagnosticTarget(d, validated);
    return target.kind === 'node' && target.key === entry.key;
  });
  const idErrors = nodeDiagnostics.filter((d) => d.path.endsWith('.id'));
  // Problems of a transition are shown on its row in the Transitions section (G-2), not in the node's list.
  const general = nodeDiagnostics.filter((d) => {
    const target = diagnosticTarget(d, validated);
    return target.kind === 'node' && target.property === undefined && !d.path.endsWith('.id') && !/\.transitions(\[|$)/.test(d.path);
  });
  const graphPath = graphParentPath(document, entry.path, catalog);
  const disabled = readOnlyReason !== undefined;
  const names = assignableNames(document);

  return (
    <>
      <dl className="facts">
        <dt>Type</dt>
        <dd data-testid="node-type">{type}</dd>
        {activity && (
          <>
            <dt>Activity</dt>
            <dd>
              {activity.displayName} <small className="hint">({activity.category})</small>
            </dd>
          </>
        )}
      </dl>
      {activity?.description && <p className="hint">{activity.description}</p>}
      <TextField label="Id" value={typeof node.id === 'string' ? node.id : ''} hint="Unique in the workflow." disabled={disabled} errors={idErrors} code onChange={(text) => studio.editNodeId(entry.key, text)} />
      <TextField
        label="Display name"
        value={typeof node.displayName === 'string' ? node.displayName : ''}
        hint="Optional label shown in the designer."
        disabled={disabled}
        errors={[]}
        onChange={(text) => studio.editDisplayName(entry.key, text)}
      />
      {activity === undefined && <p className="hint">This activity is not in the catalog (a plugin may be missing): its properties are edited as raw JSON.</p>}
      {activity?.properties.map((descriptor) => (
        <PropertyEditor
          key={descriptor.name}
          nodeKey={entry.key}
          descriptor={descriptor}
          value={properties[descriptor.name]}
          disabled={disabled}
          names={names}
          errors={propertyDiagnostics(diagnostics, validated, entry.key, descriptor.name)}
        />
      ))}
      {others.map(([name, value]) => (
        <RawPropertyEditor
          key={name}
          nodeKey={entry.key}
          name={name}
          value={value}
          disabled={disabled}
          note={activity ? 'Not a property of this activity.' : 'Raw JSON.'}
          errors={propertyDiagnostics(diagnostics, validated, entry.key, name)}
        />
      ))}
      {activity === undefined && !disabled && <AddRawProperty nodeKey={entry.key} existing={properties} />}
      {graphPath !== undefined && (
        <StepTransitions nodeKey={entry.key} node={node} graph={nodeAt(document, graphPath)} path={entry.path} disabled={disabled} diagnostics={nodeDiagnostics} />
      )}
      {general.length > 0 && (
        <ul className="node-problems" aria-label="Problems of this node">
          {general.map((d, i) => (
            <li key={i} className={d.severity.toLowerCase()}>
              {d.code}: {d.message}
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

/**
 * A flowchart step's transitions (G-2, ADR-0037), in the order they are checked: the target step, the condition (an
 * expression; empty means always) and the arrow's label, with buttons to reorder and remove; then Add a transition and
 * Set as start step. This is the keyboard way to do everything the canvas does with arrows.
 */
function StepTransitions({ nodeKey, node, graph, path, disabled, diagnostics }: { nodeKey: string; node: JsonObject; graph: JsonObject; path: readonly Step[]; disabled: boolean; diagnostics: readonly Diagnostic[] }) {
  const studio = useStudio();
  const heading = useId();
  const list = useRef<HTMLOListElement>(null);
  const focus = useStudioState((s) => s.transitionFocus);
  const [adding, setAdding] = useState('');
  const addRefusal = useStudioState((s) => (adding === '' ? 'Choose the step to go to.' : addTransitionRefusalOf(s, nodeKey, adding)));
  const startRefusal = useStudioState((s) => setStartRefusalOf(s, nodeKey));
  const steps = stepsOf(graph).flatMap((s) => (typeof s.id === 'string' ? [s.id] : []));
  const transitions = Array.isArray(node.transitions) ? node.transitions : [];
  const at = (path.at(-1) as { children: number }).children;

  useEffect(() => {
    if (focus !== undefined && focus.key === nodeKey) {
      list.current?.querySelector<HTMLElement>(`[data-transition="${focus.index}"] select`)?.focus();
    }
  }, [focus, nodeKey]);

  return (
    <section className="transitions" aria-labelledby={heading}>
      <h3 id={heading}>Transitions</h3>
      <p className="hint">After this step, the first transition whose condition is true (or that has none) is taken. When none is taken, the flowchart ends.</p>
      <p className="start-step">
        {at === 0 ? (
          <span className="badge start">start</span>
        ) : (
          <button type="button" className="small" disabled={disabled || startRefusal !== undefined} title={startRefusal} onClick={() => studio.setStartStep(nodeKey)}>
            Set as start step
          </button>
        )}
      </p>
      {transitions.length === 0 && <p className="hint">No transitions: the flowchart ends after this step.</p>}
      {transitions.length > 0 && (
        <ol ref={list} className="transition-list">
          {transitions.map((t, i) => {
            if (!isObject(t)) {
              return null;
            }

            const to = typeof t.to === 'string' ? t.to : '';
            const when = t.when === undefined ? '' : typeof t.when === 'string' ? t.when : JSON.stringify(t.when);
            const label = typeof t.label === 'string' ? t.label : '';
            const problems = diagnostics.filter((d) => d.path.includes(`.transitions[${i}]`));
            const up = moveTransitionRefusal(node, i, -1);
            const down = moveTransitionRefusal(node, i, 1);
            return (
              <li key={i} data-transition={i}>
                <fieldset className={problems.length > 0 ? 'invalid' : undefined}>
                  <legend>
                    Transition {i + 1}
                    {i === 0 && transitions.length > 1 ? ' (checked first)' : ''}
                  </legend>
                  <TargetSelect label="Go to" value={to} steps={steps} disabled={disabled} onChange={(value) => studio.editTransition(nodeKey, i, { to: value })} />
                  <TextField label="Condition" value={when} hint="A Boolean expression; empty means always." disabled={disabled} errors={[]} code onChange={(text) => studio.editTransition(nodeKey, i, { when: text })} />
                  <TextField label="Label" value={label} hint="Shown on the arrow." disabled={disabled} errors={[]} onChange={(text) => studio.editTransition(nodeKey, i, { label: text })} />
                  {problems.length > 0 && <span className="field-error">{problemText(problems)}</span>}
                  <span className="row-actions">
                    <button type="button" className="small" aria-label={`Check transition ${i + 1} earlier`} disabled={disabled || up !== undefined} title={up} onClick={() => studio.moveTransitionAt(nodeKey, i, -1)}>
                      Earlier
                    </button>
                    <button type="button" className="small" aria-label={`Check transition ${i + 1} later`} disabled={disabled || down !== undefined} title={down} onClick={() => studio.moveTransitionAt(nodeKey, i, 1)}>
                      Later
                    </button>
                    <button type="button" className="small" aria-label={`Remove transition ${i + 1}`} disabled={disabled} onClick={() => studio.removeTransitionAt(nodeKey, i)}>
                      Remove
                    </button>
                  </span>
                </fieldset>
              </li>
            );
          })}
        </ol>
      )}
      <div className="add-transition">
        <TargetSelect label="Add a transition to" value={adding} steps={steps} disabled={disabled} placeholder="Choose a step" onChange={setAdding} />
        <button
          type="button"
          className="small"
          disabled={disabled || addRefusal !== undefined}
          title={addRefusal}
          onClick={() => {
            studio.addTransition(nodeKey, adding);
            setAdding('');
          }}
        >
          Add transition
        </button>
      </div>
    </section>
  );
}

function TargetSelect({ label, value, steps, disabled, placeholder, onChange }: { label: string; value: string; steps: readonly string[]; disabled: boolean; placeholder?: string; onChange: (value: string) => void }) {
  const id = useId();
  // A target that is not a step (a validation error) stays visible so it can be corrected.
  const options = value !== '' && !steps.includes(value) ? [value, ...steps] : steps;
  return (
    <div className="field">
      <label className="field-label" htmlFor={id}>
        {label}
      </label>
      <select id={id} value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
        {placeholder !== undefined && <option value="">{placeholder}</option>}
        {options.map((step) => (
          <option key={step} value={step}>
            {step}
          </option>
        ))}
      </select>
    </div>
  );
}

function TextField({
  label,
  value,
  hint,
  disabled,
  errors,
  onChange,
  multiline,
  code,
}: {
  label: string;
  value: string;
  hint?: string;
  disabled: boolean;
  errors: readonly Diagnostic[];
  onChange: (text: string) => void;
  multiline?: boolean;
  code?: boolean;
}) {
  const id = useId();
  const describedBy = `${id}-hint${errors.length > 0 ? ` ${id}-error` : ''}`;
  const common = {
    id,
    value,
    disabled,
    spellCheck: false,
    'aria-invalid': errors.length > 0,
    'aria-describedby': describedBy,
    className: code ? 'code' : undefined,
  };
  return (
    <div className={`field${errors.length > 0 ? ' invalid' : ''}`}>
      <label className="field-label" htmlFor={id}>
        {label}
      </label>
      {multiline ? <textarea {...common} rows={3} onChange={(e) => onChange(e.target.value)} /> : <input {...common} onChange={(e) => onChange(e.target.value)} />}
      <small id={`${id}-hint`}>{hint}</small>
      {errors.length > 0 && (
        <span id={`${id}-error`} className="field-error">
          {problemText(errors)}
        </span>
      )}
    </div>
  );
}

/** One editor per property kind (format §3). */
function PropertyEditor({
  nodeKey,
  descriptor,
  value,
  disabled,
  names,
  errors,
}: {
  nodeKey: string;
  descriptor: PropertyDescriptor;
  value: Json | undefined;
  disabled: boolean;
  names: readonly string[];
  errors: Diagnostic[];
}) {
  const studio = useStudio();
  const id = useId();
  const describedBy = `${id}-hint${errors.length > 0 ? ` ${id}-error` : ''}`;
  const onChange = (text: string) => studio.editProperty(nodeKey, descriptor, text);
  const isMap = descriptor.kind === 'ExpressionMap' || descriptor.kind === 'AssignmentTargetMap';
  // A literal (number, true/false, null) is shown as JSON; editing it stores expression text, which means the same.
  const literal = value !== undefined && typeof value !== 'string' && !isMap;
  const text = value === undefined ? '' : typeof value === 'string' ? value : JSON.stringify(value);
  const kindLabel = descriptor.kind === 'Text' && descriptor.allowedValues.length > 0 ? 'Choice' : descriptor.kind;

  let editor;
  if (isMap) {
    editor = <MapEditor id={id} nodeKey={nodeKey} descriptor={descriptor} value={value} disabled={disabled} names={names} describedBy={describedBy} />;
  } else if (descriptor.kind === 'Text' && descriptor.allowedValues.length > 0) {
    const options = [...(descriptor.required ? [] : ['']), ...descriptor.allowedValues];
    if (!options.includes(text)) {
      options.push(text);
    }

    editor = (
      <select id={id} value={text} disabled={disabled} aria-describedby={describedBy} onChange={(e) => onChange(e.target.value)}>
        {options.map((option) => (
          <option key={option} value={option}>
            {option === '' ? '(not set)' : option}
          </option>
        ))}
      </select>
    );
  } else {
    const list = descriptor.kind === 'AssignmentTarget' ? `${id}-names` : undefined;
    editor = (
      <>
        <input
          id={id}
          className={descriptor.kind === 'Expression' || descriptor.kind === 'AssignmentTarget' || descriptor.kind === 'LocalName' ? 'code' : undefined}
          value={text}
          disabled={disabled}
          spellCheck={false}
          list={list}
          placeholder={descriptor.kind === 'Expression' ? "expression, e.g. 'Hello ' + name" : descriptor.kind === 'AssignmentTarget' ? 'variable or Out/InOut argument' : undefined}
          aria-invalid={errors.length > 0}
          aria-describedby={describedBy}
          onChange={(e) => onChange(e.target.value)}
        />
        {list && (
          <datalist id={list}>
            {names.map((name) => (
              <option key={name} value={name} />
            ))}
          </datalist>
        )}
      </>
    );
  }

  return (
    <div className={`field${errors.length > 0 ? ' invalid' : ''}`}>
      <label className="field-label" htmlFor={isMap ? undefined : id} id={`${id}-label`}>
        {descriptor.name}
        {descriptor.required && <span aria-label="required"> *</span>} <small>{kindLabel}</small>
        {literal && <small className="badge">literal</small>}
      </label>
      {editor}
      <small id={`${id}-hint`}>{descriptor.description}</small>
      {errors.length > 0 && (
        <span id={`${id}-error`} className="field-error">
          {problemText(errors)}
        </span>
      )}
    </div>
  );
}

/** A name → expression (or name → assignment target) map: one row per entry; keys stay in their order. */
function MapEditor({
  id,
  nodeKey,
  descriptor,
  value,
  disabled,
  names,
  describedBy,
}: {
  id: string;
  nodeKey: string;
  descriptor: PropertyDescriptor;
  value: Json | undefined;
  disabled: boolean;
  names: readonly string[];
  describedBy: string;
}) {
  const studio = useStudio();
  const map = isObject(value) ? value : {};
  const entries = Object.entries(map);
  const targets = descriptor.kind === 'AssignmentTargetMap';
  const commit = (next: JsonObject, row: number, part: string) =>
    studio.editPropertyValue(nodeKey, descriptor.name, Object.keys(next).length === 0 && !descriptor.required ? undefined : next, `map:${nodeKey}:${descriptor.name}:${row}:${part}`);
  const rename = (row: number, key: string) => commit(Object.fromEntries(entries.map(([k, v], i) => (i === row ? [key, v] : [k, v]))), row, 'key');
  const setValue = (row: number, text: string) => commit(Object.fromEntries(entries.map(([k, v], i) => (i === row ? [k, text] : [k, v]))), row, 'value');
  const remove = (row: number) => commit(Object.fromEntries(entries.filter((_, i) => i !== row)), row, 'remove');
  const add = () => {
    let n = 1;
    while (`name${n}` in map) {
      n++;
    }

    commit({ ...map, [`name${n}`]: '' }, entries.length, 'add');
  };

  return (
    <div className="map-editor" role="group" aria-labelledby={`${id}-label`} aria-describedby={describedBy}>
      {entries.length === 0 && <p className="hint">No entries.</p>}
      {entries.map(([key, entry], row) => (
        <MapRow
          key={row}
          entryKey={key}
          text={typeof entry === 'string' ? entry : JSON.stringify(entry)}
          taken={entries.filter((_, i) => i !== row).map(([k]) => k)}
          disabled={disabled}
          list={targets ? `${id}-targets` : undefined}
          valueLabel={targets ? 'target' : 'expression'}
          onRename={(k) => rename(row, k)}
          onValue={(text) => setValue(row, text)}
          onRemove={() => remove(row)}
        />
      ))}
      {targets && (
        <datalist id={`${id}-targets`}>
          {names.map((name) => (
            <option key={name} value={name} />
          ))}
        </datalist>
      )}
      <button type="button" className="small" disabled={disabled} onClick={add}>
        Add entry
      </button>
    </div>
  );
}

/** One map entry. A blank or duplicate name is shown as an error and not stored (it would merge or lose an entry). */
function MapRow({
  entryKey,
  text,
  taken,
  disabled,
  list,
  valueLabel,
  onRename,
  onValue,
  onRemove,
}: {
  entryKey: string;
  text: string;
  taken: readonly string[];
  disabled: boolean;
  list?: string;
  valueLabel: string;
  onRename: (key: string) => void;
  onValue: (text: string) => void;
  onRemove: () => void;
}) {
  const [draft, setDraft] = useState(entryKey);
  useEffect(() => setDraft(entryKey), [entryKey]);
  const error = draft.trim() === '' ? 'A name is required.' : taken.includes(draft) ? `'${draft}' is already an entry.` : undefined;
  return (
    <div className="map-row">
      <input
        className="code"
        aria-label="Entry name"
        value={draft}
        disabled={disabled}
        spellCheck={false}
        aria-invalid={error !== undefined}
        title={error}
        onChange={(e) => {
          setDraft(e.target.value);
          if (e.target.value.trim() !== '' && !taken.includes(e.target.value)) {
            onRename(e.target.value);
          }
        }}
      />
      <span aria-hidden="true">→</span>
      <input className="code" aria-label={`Entry ${valueLabel}`} value={text} disabled={disabled} spellCheck={false} list={list} onChange={(e) => onValue(e.target.value)} />
      <button type="button" className="small" aria-label={`Remove ${entryKey}`} disabled={disabled} onClick={onRemove}>
        ×
      </button>
      {error && <span className="field-error map-error">{error}</span>}
    </div>
  );
}

/** A JSON value edited as text: stored only while it is valid JSON; otherwise the reason is shown and nothing changes. */
export function JsonField({
  label,
  value,
  disabled,
  placeholder,
  onCommit,
  errors = [],
}: {
  label: string;
  value: Json | undefined;
  disabled: boolean;
  placeholder?: string;
  onCommit: (value: Json | undefined) => void;
  errors?: readonly Diagnostic[];
}) {
  const [text, setText] = useState(() => jsonText(value));
  const [error, setError] = useState<string>();
  const current = useRef(text);
  current.current = text;
  // An outside change (undo, redo) replaces the text unless it already means the same value.
  useEffect(() => {
    const parsed = parseJsonText(current.current);
    if (!parsed.ok || jsonText(parsed.value) !== jsonText(value)) {
      setText(jsonText(value));
      setError(undefined);
    }
  }, [value]);

  const message = error ?? (errors.length > 0 ? problemText(errors) : undefined);
  return (
    <span className="json-field">
      <input
        className="code"
        aria-label={label}
        value={text}
        disabled={disabled}
        spellCheck={false}
        placeholder={placeholder}
        aria-invalid={message !== undefined}
        title={message}
        onChange={(e) => {
          setText(e.target.value);
          const parsed = parseJsonText(e.target.value);
          if (parsed.ok) {
            setError(undefined);
            onCommit(parsed.value);
          } else {
            setError(parsed.error);
          }
        }}
      />
      {message && <span className="field-error">{message}</span>}
    </span>
  );
}

/** A property the catalog does not describe: its raw JSON value, editable while valid, and removable. */
function RawPropertyEditor({ nodeKey, name, value, disabled, note, errors }: { nodeKey: string; name: string; value: Json; disabled: boolean; note: string; errors: Diagnostic[] }) {
  const studio = useStudio();
  return (
    <div className={`field${errors.length > 0 ? ' invalid' : ''}`}>
      <span className="field-label">
        {name} <small>JSON</small>
      </span>
      <div className="map-row">
        <JsonField label={`${name} (JSON)`} value={value} disabled={disabled} errors={errors} onCommit={(v) => v !== undefined && studio.editPropertyValue(nodeKey, name, v)} />
        <button type="button" className="small" aria-label={`Remove ${name}`} disabled={disabled} onClick={() => studio.editPropertyValue(nodeKey, name, undefined, `remove:${nodeKey}:${name}`)}>
          ×
        </button>
      </div>
      <small>{note}</small>
    </div>
  );
}

function AddRawProperty({ nodeKey, existing }: { nodeKey: string; existing: JsonObject }) {
  const studio = useStudio();
  const [name, setName] = useState('');
  const [text, setText] = useState('');
  const parsed = parseJsonText(text);
  const refusal =
    name.trim() === '' ? 'Enter a property name.'
    : name in existing ? `'${name}' already exists.`
    : !parsed.ok ? parsed.error
    : parsed.value === undefined ? 'Enter a JSON value.'
    : undefined;
  return (
    <div className="field add-raw">
      <span className="field-label">Add a property</span>
      <div className="map-row">
        <input className="code" aria-label="New property name" value={name} spellCheck={false} onChange={(e) => setName(e.target.value)} />
        <input className="code" aria-label="New property value (JSON)" value={text} spellCheck={false} placeholder='"text", 42, true…' onChange={(e) => setText(e.target.value)} />
        <button
          type="button"
          className="small"
          disabled={refusal !== undefined}
          title={refusal}
          onClick={() => {
            if (parsed.ok && parsed.value !== undefined) {
              studio.editPropertyValue(nodeKey, name.trim(), parsed.value, `add:${nodeKey}:${name}`);
              setName('');
              setText('');
            }
          }}
        >
          Add
        </button>
      </div>
    </div>
  );
}
