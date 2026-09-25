import { createContext, memo, useContext, useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { childSteps, editability, indexDocument, isObject, keyOf, nodeLabel } from './document';
import { useStore } from './store';
import { deleteRefusalOf, insertRefusal, isDirty, moveRefusalOf, type Studio, type StudioState } from './studio';
import type { Diagnostic, Json, JsonObject, PropertyDescriptor } from './types';

const StudioContext = createContext<Studio | null>(null);

function useStudio(): Studio {
  const studio = useContext(StudioContext);
  if (studio === null) {
    throw new Error('No Studio in context.');
  }

  return studio;
}

function useStudioState<T>(selector: (state: StudioState) => T): T {
  return useStore(useStudio().store, selector);
}

export function App({ studio }: { studio: Studio }) {
  useEffect(() => {
    void studio.connect();
  }, [studio]);

  return (
    <StudioContext.Provider value={studio}>
      <Shell />
    </StudioContext.Provider>
  );
}

function Shell() {
  const studio = useStudio();
  const connection = useStudioState((s) => s.connection);
  const dirty = useStudioState(isDirty);
  const path = useStudioState((s) => s.file?.path);

  useEffect(() => {
    document.title = `${path ? `${dirty ? '• ' : ''}${path} — ` : ''}MyRPA Studio`;
  }, [path, dirty]);

  useEffect(() => {
    const onKey = (event: globalThis.KeyboardEvent) => {
      const command = event.ctrlKey || event.metaKey;
      const key = event.key.toLowerCase();
      if (command && key === 's') {
        event.preventDefault();
        void studio.save();
      } else if (command && ((key === 'z' && event.shiftKey) || key === 'y')) {
        event.preventDefault();
        studio.redo();
      } else if (command && key === 'z') {
        event.preventDefault();
        studio.undo();
      } else if (event.key === 'F5' && !event.ctrlKey) {
        event.preventDefault();
        void studio.run();
      }
    };
    const onLeave = (event: BeforeUnloadEvent) => {
      if (isDirty(studio.store.get())) {
        event.preventDefault();
      }
    };
    window.addEventListener('keydown', onKey);
    window.addEventListener('beforeunload', onLeave);
    return () => {
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('beforeunload', onLeave);
    };
  }, [studio]);

  return (
    <div className="studio">
      <Toolbar />
      {connection === 'ready' ? (
        <>
          <Toolbox />
          <WorkflowTree />
          <PropertiesPanel />
          <OutputPanel />
        </>
      ) : (
        <ConnectionPanel />
      )}
      <StatusBar />
    </div>
  );
}

function ConnectionPanel() {
  const connection = useStudioState((s) => s.connection);
  return (
    <main className="connection">
      {connection === 'connecting' && <p>Connecting to MyRPA.Server…</p>}
      {connection === 'signed-out' && (
        <p>
          This browser has no session. Open the start link that <code>MyRPA.Server</code> printed when it started. The link works once;
          restart the server for a new one.
        </p>
      )}
      {connection === 'failed' && <p>MyRPA.Server cannot be reached. Check that it is running, then reload this page.</p>}
    </main>
  );
}

function StatusBar() {
  const message = useStudioState((s) => s.message);
  return (
    <p className="statusbar" role="status" aria-live="polite">
      {message}
    </p>
  );
}

function Toolbar() {
  const studio = useStudio();
  const connection = useStudioState((s) => s.connection);
  const projects = useStudioState((s) => s.projects);
  const project = useStudioState((s) => s.project);
  const files = useStudioState((s) => s.files);
  const file = useStudioState((s) => s.file);
  const hasDocument = useStudioState((s) => s.document !== undefined);
  const dirty = useStudioState(isDirty);
  const busy = useStudioState((s) => s.busy);
  const running = useStudioState((s) => s.run?.status === 'Starting' || s.run?.status === 'Running');
  const [choice, setChoice] = useState('');

  const open = () => {
    if (choice === '' || (dirty && !window.confirm('Discard the unsaved changes?'))) {
      return;
    }

    void studio.open(choice);
  };

  const ready = connection === 'ready';
  return (
    <header className="toolbar">
      <h1>MyRPA Studio</h1>
      <span className="document-title" data-testid="document-title">
        {file ? `${file.path}${dirty ? ' •' : ''}${file.readOnlyReason ? ' (read-only)' : ''}` : 'No workflow open'}
      </span>
      <label>
        Project
        <select value={project ?? ''} disabled={!ready} onChange={(e) => void studio.selectProject(e.target.value)}>
          {projects.map((name) => (
            <option key={name} value={name}>
              {name}
            </option>
          ))}
        </select>
      </label>
      <label>
        Workflow
        <select value={choice} disabled={!ready} onChange={(e) => setChoice(e.target.value)}>
          <option value="">Choose…</option>
          {files.map((f) => (
            <option key={f.path} value={f.path}>
              {f.path}
            </option>
          ))}
        </select>
      </label>
      <button type="button" onClick={open} disabled={choice === '' || busy !== undefined}>
        Open
      </button>
      <button type="button" onClick={() => void studio.save()} disabled={!dirty || file?.readOnlyReason !== undefined || busy !== undefined} title="Ctrl+S">
        Save
      </button>
      <button type="button" onClick={() => void studio.validate()} disabled={!hasDocument || busy !== undefined}>
        Validate
      </button>
      <button type="button" onClick={() => void studio.run()} disabled={!hasDocument || running || busy !== undefined} title="F5">
        Run
      </button>
    </header>
  );
}

function Toolbox() {
  const studio = useStudio();
  const activities = useStudioState((s) => s.activities);
  const refusal = useStudioState(insertRefusal);
  const [query, setQuery] = useState('');
  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q === '' ? activities : activities.filter((a) => `${a.displayName} ${a.type} ${a.category}`.toLowerCase().includes(q));
  }, [activities, query]);

  return (
    <aside className="toolbox" aria-labelledby="toolbox-heading">
      <h2 id="toolbox-heading">Activities</h2>
      <input type="search" placeholder="Search" aria-label="Search activities" value={query} onChange={(e) => setQuery(e.target.value)} />
      <p className="hint" id="toolbox-hint">
        {refusal ?? 'Inserts after the selected activity, or at the end of a selected Sequence.'}
      </p>
      <ul aria-label="Activity catalog">
        {shown.map((a) => (
          <li key={a.type}>
            <button
              type="button"
              className="insert"
              title={a.description}
              aria-label={`Insert ${a.displayName} (${a.type})`}
              aria-describedby="toolbox-hint"
              disabled={refusal !== undefined}
              onClick={() => studio.insertActivity(a.type)}
            >
              <span>{a.displayName}</span> <small>{a.type}</small>
            </button>
          </li>
        ))}
      </ul>
    </aside>
  );
}

function WorkflowTree() {
  const studio = useStudio();
  const document = useStudioState((s) => s.document);
  const selectedKey = useStudioState((s) => s.selectedKey);
  const tree = useRef<HTMLUListElement>(null);
  const refocus = useRef(false);

  // Keep keyboard focus on the selected item while the user works in the tree, also when a command removed or moved
  // the focused item.
  useEffect(() => {
    const root = tree.current;
    if (root && selectedKey && (refocus.current || root.contains(window.document.activeElement))) {
      root.querySelector<HTMLElement>(`[data-key="${selectedKey}"]`)?.focus();
    }

    refocus.current = false;
  }, [selectedKey, document]);

  if (document === undefined) {
    return (
      <section className="designer" aria-labelledby="designer-heading">
        <h2 id="designer-heading">Workflow</h2>
        <p className="hint">Choose a workflow and press Open.</p>
      </section>
    );
  }

  const onKeyDown = (event: KeyboardEvent<HTMLUListElement>) => {
    if (event.key === 'Delete' || (event.altKey && (event.key === 'ArrowUp' || event.key === 'ArrowDown'))) {
      event.preventDefault();
      refocus.current = true;
      if (event.key === 'Delete') {
        studio.deleteSelected();
      } else {
        studio.moveSelected(event.key === 'ArrowUp' ? -1 : 1);
      }

      return;
    }

    const entries = indexDocument(document).entries;
    const at = entries.findIndex((e) => e.key === selectedKey);
    const target =
      event.key === 'ArrowDown' ? entries[Math.min(at + 1, entries.length - 1)]
      : event.key === 'ArrowUp' ? entries[Math.max(at - 1, 0)]
      : event.key === 'Home' ? entries[0]
      : event.key === 'End' ? entries[entries.length - 1]
      : undefined;
    if (target) {
      event.preventDefault();
      studio.select(target.key);
    }
  };

  const root = document.root;
  return (
    <section className="designer" aria-labelledby="designer-heading">
      <h2 id="designer-heading">Workflow</h2>
      <EditBar />
      <ul role="tree" aria-labelledby="designer-heading" ref={tree} onKeyDown={onKeyDown}>
        {isObject(root) && <TreeNode node={root} depth={1} />}
      </ul>
    </section>
  );
}

/** Structural commands and history. A disabled command's tooltip says why it is not available. */
function EditBar() {
  const studio = useStudio();
  const undo = useStudioState((s) => s.undo.at(-1)?.label);
  const redo = useStudioState((s) => s.redo.at(-1)?.label);
  const moveUp = useStudioState((s) => moveRefusalOf(s, -1));
  const moveDown = useStudioState((s) => moveRefusalOf(s, 1));
  const remove = useStudioState(deleteRefusalOf);

  return (
    <div className="editbar" role="toolbar" aria-label="Edit">
      <button type="button" onClick={() => studio.undo()} disabled={undo === undefined} title={undo ? `Undo: ${undo} (Ctrl+Z)` : 'Nothing to undo'}>
        Undo
      </button>
      <button type="button" onClick={() => studio.redo()} disabled={redo === undefined} title={redo ? `Redo: ${redo} (Ctrl+Y)` : 'Nothing to redo'}>
        Redo
      </button>
      <button type="button" onClick={() => studio.moveSelected(-1)} disabled={moveUp !== undefined} title={moveUp ?? 'Move the selected activity up (Alt+Up)'}>
        Move up
      </button>
      <button type="button" onClick={() => studio.moveSelected(1)} disabled={moveDown !== undefined} title={moveDown ?? 'Move the selected activity down (Alt+Down)'}>
        Move down
      </button>
      <button type="button" onClick={() => studio.deleteSelected()} disabled={remove !== undefined} title={remove ?? 'Delete the selected activity (Delete)'}>
        Delete
      </button>
    </div>
  );
}

/** One node. Memoized on the node object: an edit re-renders only the edited node and its ancestors. */
const TreeNode = memo(function TreeNode({ node, depth, slot }: { node: JsonObject; depth: number; slot?: string }) {
  const studio = useStudio();
  const key = keyOf(node);
  const id = typeof node.id === 'string' ? node.id : undefined;
  const type = typeof node.type === 'string' ? node.type : undefined;
  const selected = useStudioState((s) => s.selectedKey === key);
  const hasError = useStudioState((s) => id !== undefined && s.errorNodeIds.has(id));
  const status = useStudioState((s) => (id === undefined ? undefined : s.nodeStatus.get(id)));
  const activity = useStudioState((s) => (type === undefined ? undefined : s.catalog.get(type)));
  const children = childSteps(node);

  return (
    <li
      role="treeitem"
      aria-level={depth}
      aria-selected={selected}
      aria-expanded={children.length > 0 ? true : undefined}
      tabIndex={selected ? 0 : -1}
      data-key={key}
      data-node-id={id}
      onClick={(event) => {
        event.stopPropagation();
        studio.select(key);
      }}
    >
      <div className={`node${selected ? ' selected' : ''}${hasError ? ' has-error' : ''}`}>
        {slot !== undefined && <span className="slot">{slot}:</span>}
        <span className="label">{nodeLabel(node, activity)}</span>
        <span className="type">{type}</span>
        {id !== undefined && <span className="id">#{id}</span>}
        {hasError && <span className="badge error">error</span>}
        {status !== undefined && <span className={`badge status-${status.toLowerCase()}`}>{status}</span>}
      </div>
      {children.length > 0 && (
        <ul role="group">
          {children.map((child) => (
            <TreeNode key={keyOf(child.node)} node={child.node} depth={depth + 1} slot={child.slot} />
          ))}
        </ul>
      )}
    </li>
  );
});

function propertyDiagnostics(diagnostics: readonly Diagnostic[] | undefined, nodeId: Json | undefined, name: string): Diagnostic[] {
  return (diagnostics ?? []).filter((d) => d.nodeId === nodeId && d.path.endsWith(`.properties.${name}`));
}

function PropertiesPanel() {
  const studio = useStudio();
  const document = useStudioState((s) => s.document);
  const selectedKey = useStudioState((s) => s.selectedKey);
  const readOnlyReason = useStudioState((s) => s.file?.readOnlyReason);
  const diagnostics = useStudioState((s) => s.diagnostics);
  const catalog = useStudioState((s) => s.catalog);
  const entry = document && selectedKey ? indexDocument(document).byKey.get(selectedKey) : undefined;

  if (entry === undefined) {
    return (
      <aside className="properties" aria-labelledby="properties-heading">
        <h2 id="properties-heading">Properties</h2>
        <p className="hint">Select a node.</p>
      </aside>
    );
  }

  const node = entry.node;
  const type = typeof node.type === 'string' ? node.type : '';
  const activity = catalog.get(type);
  const properties = isObject(node.properties) ? node.properties : {};
  const known = new Set(activity?.properties.map((p) => p.name));
  const others = Object.entries(properties).filter(([name]) => !known.has(name));
  const nodeDiagnostics = (diagnostics ?? []).filter((d) => d.nodeId === node.id);
  const disabled = readOnlyReason !== undefined;

  return (
    <aside className="properties" aria-labelledby="properties-heading">
      <h2 id="properties-heading">Properties</h2>
      <dl className="facts">
        <dt>Type</dt>
        <dd data-testid="node-type">{type}</dd>
        <dt>Id</dt>
        <dd>{typeof node.id === 'string' ? node.id : ''}</dd>
      </dl>
      {activity?.description && <p className="hint">{activity.description}</p>}
      <TextField
        label="Display name"
        value={typeof node.displayName === 'string' ? node.displayName : ''}
        disabled={disabled}
        onChange={(text) => studio.editDisplayName(entry.key, text)}
      />
      {activity === undefined && <p className="hint">This activity is not in the catalog; its properties are read-only.</p>}
      {activity?.properties.map((descriptor) => (
        <PropertyEditor
          key={descriptor.name}
          nodeKey={entry.key}
          descriptor={descriptor}
          value={properties[descriptor.name]}
          disabled={disabled}
          errors={propertyDiagnostics(diagnostics, node.id, descriptor.name)}
        />
      ))}
      {others.map(([name, value]) => (
        <div className="field" key={name}>
          <span className="field-label">{name}</span>
          <output className="readonly">{JSON.stringify(value)}</output>
          <small>{activity ? 'Not a property of this activity.' : 'Read-only.'}</small>
        </div>
      ))}
      {nodeDiagnostics.length > 0 && (
        <ul className="node-problems" aria-label="Problems of this node">
          {nodeDiagnostics.map((d, i) => (
            <li key={i} className={d.severity.toLowerCase()}>
              {d.code}: {d.message}
            </li>
          ))}
        </ul>
      )}
    </aside>
  );
}

function TextField({ label, value, disabled, onChange }: { label: string; value: string; disabled: boolean; onChange: (text: string) => void }) {
  const id = useId();
  return (
    <div className="field">
      <label className="field-label" htmlFor={id}>
        {label}
      </label>
      <input id={id} value={value} disabled={disabled} spellCheck={false} onChange={(e) => onChange(e.target.value)} />
    </div>
  );
}

function PropertyEditor({
  nodeKey,
  descriptor,
  value,
  disabled,
  errors,
}: {
  nodeKey: string;
  descriptor: PropertyDescriptor;
  value: Json | undefined;
  disabled: boolean;
  errors: Diagnostic[];
}) {
  const studio = useStudio();
  const id = useId();
  const state = editability(descriptor, value);
  const describedBy = `${id}-hint${errors.length > 0 ? ` ${id}-error` : ''}`;
  const onChange = (text: string) => studio.editProperty(nodeKey, descriptor, text);
  const label = (
    <label className="field-label" htmlFor={id}>
      {descriptor.name}
      {descriptor.required && <span aria-label="required"> *</span>} <small>{descriptor.kind}</small>
    </label>
  );

  let editor;
  if (!state.editable) {
    editor = (
      <output id={id} className="readonly" aria-describedby={describedBy}>
        {value === undefined ? '' : JSON.stringify(value)} — {state.reason}
      </output>
    );
  } else if (descriptor.kind === 'Text' && descriptor.allowedValues.length > 0) {
    const options = [...(descriptor.required ? [] : ['']), ...descriptor.allowedValues];
    if (!options.includes(state.text)) {
      options.push(state.text);
    }

    editor = (
      <select id={id} value={state.text} disabled={disabled} aria-describedby={describedBy} onChange={(e) => onChange(e.target.value)}>
        {options.map((option) => (
          <option key={option} value={option}>
            {option === '' ? '(not set)' : option}
          </option>
        ))}
      </select>
    );
  } else {
    editor = (
      <input
        id={id}
        className={descriptor.kind === 'Expression' ? 'code' : undefined}
        value={state.text}
        disabled={disabled}
        spellCheck={false}
        aria-invalid={errors.length > 0}
        aria-describedby={describedBy}
        onChange={(e) => onChange(e.target.value)}
      />
    );
  }

  return (
    <div className={`field${errors.length > 0 ? ' invalid' : ''}`}>
      {label}
      {editor}
      <small id={`${id}-hint`}>{descriptor.description}</small>
      {errors.length > 0 && (
        <span id={`${id}-error`} className="field-error">
          {errors.map((e) => `${e.code}: ${e.message}`).join(' ')}
        </span>
      )}
    </div>
  );
}

function OutputPanel() {
  const studio = useStudio();
  const diagnostics = useStudioState((s) => s.diagnostics);
  const stale = useStudioState((s) => s.diagnostics !== undefined && s.validated !== s.document);
  const run = useStudioState((s) => s.run);
  const events = useStudioState((s) => s.events);

  return (
    <section className="output" aria-label="Output">
      <div className="problems">
        <h2>Problems{stale ? ' (the workflow changed since)' : ''}</h2>
        {diagnostics === undefined ? (
          <p className="hint">Not validated yet.</p>
        ) : diagnostics.length === 0 ? (
          <p data-testid="no-problems">No problems.</p>
        ) : (
          <ul aria-label="Problems">
            {diagnostics.map((d, i) => (
              <li key={i} className={d.severity.toLowerCase()}>
                <button type="button" disabled={!d.nodeId} onClick={() => d.nodeId && studio.selectNodeId(d.nodeId)}>
                  {d.severity} {d.code}
                  {d.nodeId ? ` (${d.nodeId})` : ''}: {d.message}
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
      <div className="run">
        <h2>Run</h2>
        <p data-testid="run-status">
          Status: <strong>{run?.status ?? 'Not run'}</strong>
          {run?.runId && <span className="hint"> · run {run.runId}</span>}
          {run?.result && <span className="hint"> · {Math.round(run.result.durationMs)} ms</span>}
        </p>
        {run?.error && (
          <p className="field-error">
            {run.error.code}
            {run.error.nodeId ? ` at ${run.error.nodeId}` : ''}: {run.error.message}
          </p>
        )}
        {run?.result && Object.keys(run.result.outputs).length > 0 && (
          <p data-testid="run-outputs">Outputs: {JSON.stringify(run.result.outputs)}</p>
        )}
        <ol className="events" aria-label="Execution events">
          {events.map((e, i) => (
            <li key={e.kind === 'stream.gap' ? `gap-${i}` : `${e.runId}:${e.sequence}`} className={`event event-${e.kind.replace('.', '-')}`}>
              <span className="seq">{e.sequence}</span> <span className="kind">{e.kind}</span>
              {e.nodeId && <span className="event-node"> {e.nodeId}</span>}
              {e.status && <span> {e.status}</span>}
              {e.kind === 'log' && (
                <span className="log">
                  {' '}
                  [{e.level}] {e.message}
                </span>
              )}
              {e.kind === 'stream.gap' && (
                <span>
                  {' '}
                  events {e.missingFromSequence}–{e.missingToSequence} are no longer available
                </span>
              )}
            </li>
          ))}
        </ol>
      </div>
    </section>
  );
}
