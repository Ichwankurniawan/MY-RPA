import { memo, useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { StudioContext, useStudio, useStudioState } from './context';
import { DataPanel } from './DataPanel';
import { installDragAndDrop } from './dragdrop';
import { childSteps, indexDocument, isObject, keyOf, nodeAt, nodeLabel } from './document';
import type { Position } from './placement';
import { PropertiesPanel } from './PropertyEditors';
import {
  currentRun,
  deleteRefusalOf,
  insertRefusal,
  isActive,
  isDirty,
  moveRefusalOf,
  runRefusalOf,
  stopRefusalOf,
  type RunDialogState,
  type RunView,
  type Studio,
  type StudioDialog,
  type StudioState,
  workflowKey,
} from './studio';
import type { ExecutionEvent, JsonObject, WorkflowFile } from './types';


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
        if (event.shiftKey) {
          void studio.stop();
        } else if (studio.store.get().runDialog === undefined) {
          void studio.requestRun();
        }
      }
    };
    const onLeave = (event: BeforeUnloadEvent) => {
      if (isDirty(studio.store.get())) {
        event.preventDefault();
      }
    };
    // Ctrl+X / C / V on activities (W7) use the browser's clipboard events, so pasting needs no permission prompt. In a
    // text field they stay the field's own cut, copy and paste.
    const inField = () => {
      const active = window.document.activeElement;
      return active instanceof HTMLElement && (active.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(active.tagName));
    };
    const onCopyOrCut = (event: ClipboardEvent) => {
      if (inField() || studio.store.get().document === undefined) {
        return;
      }

      const text = event.type === 'cut' ? studio.cutSelected() : studio.copySelected();
      if (text !== undefined) {
        event.clipboardData?.setData('text/plain', text);
        event.preventDefault();
      }
    };
    const onPaste = (event: ClipboardEvent) => {
      if (inField() || studio.store.get().document === undefined) {
        return;
      }

      event.preventDefault();
      studio.paste(event.clipboardData?.getData('text/plain') || undefined);
    };
    window.addEventListener('keydown', onKey);
    window.addEventListener('beforeunload', onLeave);
    window.addEventListener('copy', onCopyOrCut);
    window.addEventListener('cut', onCopyOrCut);
    window.addEventListener('paste', onPaste);
    return () => {
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('beforeunload', onLeave);
      window.removeEventListener('copy', onCopyOrCut);
      window.removeEventListener('cut', onCopyOrCut);
      window.removeEventListener('paste', onPaste);
    };
  }, [studio]);

  // Drag-and-drop (W7): one controller on the root, by event delegation; it renders nothing while dragging.
  const root = useRef<HTMLDivElement>(null);
  useEffect(() => (root.current ? installDragAndDrop(root.current, studio) : undefined), [studio]);

  return (
    <div className="studio" ref={root}>
      <Toolbar />
      {connection === 'ready' ? (
        <>
          <div className="sidebar">
            <FilesPanel />
            <Toolbox />
          </div>
          <WorkflowTree />
          <PropertiesPanel />
          <OutputPanel />
          <RunDialogHost />
          <StudioDialogHost />
        </>
      ) : (
        <ConnectionPanel />
      )}
      <StatusBar />
    </div>
  );
}

/** What a run's state means, in words. The status itself is the server's; only the wording is the Studio's. */
export function statusLabel(run: RunView): string {
  switch (run.status) {
    case 'Validating':
      return 'Validating…';
    case 'NotStarted':
      return run.notStarted?.reason === 'validation' ? 'Not started — validation failed' : 'Not started — refused by the server';
    case 'Starting':
      return run.cancelRequested ? 'Cancelling…' : 'Waiting to start';
    case 'Running':
      return run.cancelRequested ? 'Cancelling…' : 'Running';
    case 'Failed':
      return run.error?.code === 'MYRPA2004' ? 'Failed — arguments rejected, no activity ran' : 'Failed';
    case 'TimedOut':
      return 'Timed out';
    default:
      return run.status;
  }
}

const time = (iso: string) => new Date(iso).toLocaleTimeString([], { hour12: false });

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
  const runRefusal = useStudioState(runRefusalOf);
  const stopRefusal = useStudioState((s) => stopRefusalOf(currentRun(s)));
  const run = useStudioState(currentRun);
  const [choice, setChoice] = useState('');
  const openPath = file?.path;

  // The choice follows the open file, however it was opened (Files panel, --open, rename, Save as).
  useEffect(() => {
    if (openPath !== undefined) {
      setChoice(openPath);
    }
  }, [openPath]);

  // With unsaved changes, the Studio asks in its own dialog (Save, Discard, Cancel).
  const open = () => {
    if (choice !== '') {
      void studio.requestOpen(choice);
    }
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
      <button
        type="button"
        onClick={() => studio.startName('save-as')}
        disabled={!hasDocument || file?.readOnlyReason !== undefined || busy !== undefined}
        title={file?.readOnlyReason ? `The workflow is read-only: ${file.readOnlyReason}` : 'Save the workflow as a new file'}
      >
        Save as…
      </button>
      <button type="button" onClick={() => void studio.validate()} disabled={!hasDocument || busy !== undefined}>
        Validate
      </button>
      <button type="button" onClick={() => void studio.requestRun()} disabled={runRefusal !== undefined} title={runRefusal ?? 'Validate, then run (F5)'}>
        Run
      </button>
      <button type="button" onClick={() => void studio.stop()} disabled={stopRefusal !== undefined} title={stopRefusal ?? 'Stop the run (Shift+F5)'}>
        Stop
      </button>
      <span className="toolbar-status" data-testid="toolbar-run-status">
        {run ? `Status: ${statusLabel(run)}` : ''}
      </span>
    </header>
  );
}

const descriptionId = (type: string) => `activity-description-${type.replace(/[^A-Za-z0-9_-]/g, '-')}`;

function Toolbox() {
  const studio = useStudio();
  const activities = useStudioState((s) => s.activities);
  const refusal = useStudioState(insertRefusal);
  const [query, setQuery] = useState('');
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());
  // The catalog (built-in and plugin activities, ADR-0020) grouped by category; the search also matches descriptions.
  const groups = useMemo(() => {
    const q = query.trim().toLowerCase();
    const shown = q === '' ? activities : activities.filter((a) => `${a.displayName} ${a.type} ${a.category} ${a.description ?? ''}`.toLowerCase().includes(q));
    const byCategory = new Map<string, typeof activities>();
    for (const activity of shown) {
      byCategory.set(activity.category, [...(byCategory.get(activity.category) ?? []), activity]);
    }

    return [...byCategory].sort(([a], [b]) => a.localeCompare(b));
  }, [activities, query]);
  const searching = query.trim() !== '';
  const toggle = (category: string) => setCollapsed((current) => new Set(current.has(category) ? [...current].filter((c) => c !== category) : [...current, category]));

  return (
    <aside className="toolbox" aria-labelledby="toolbox-heading">
      <h2 id="toolbox-heading">Activities</h2>
      <input type="search" placeholder="Search activities" aria-label="Search activities" value={query} onChange={(e) => setQuery(e.target.value)} />
      <p className="hint" id="toolbox-hint">
        {refusal ?? 'Inserts after the selected activity, or at the end of a selected Sequence.'}
      </p>
      {groups.length === 0 && <p className="hint">{searching ? `No activity matches "${query.trim()}".` : 'The server has no activities.'}</p>}
      <ul aria-label="Activity catalog" className="catalog">
        {groups.map(([category, members]) => {
          const open = searching || !collapsed.has(category);
          return (
            <li key={category} className="category">
              <button type="button" className="category-toggle" aria-expanded={open} onClick={() => toggle(category)}>
                <span aria-hidden="true">{open ? '▾' : '▸'}</span> {category} <small>({members.length})</small>
              </button>
              {open && (
                <ul aria-label={category}>
                  {members.map((a) => (
                    <li key={a.type}>
                      {/* The accessible name ("Insert Log (Core.Log)") contains the visible text; the description is outside. */}
                      <button
                        type="button"
                        className="insert"
                        data-activity={a.type}
                        title={a.description}
                        aria-label={`Insert ${a.displayName} (${a.type})`}
                        aria-describedby={a.description ? `toolbox-hint ${descriptionId(a.type)}` : 'toolbox-hint'}
                        disabled={refusal !== undefined}
                        onClick={() => studio.insertActivity(a.type)}
                      >
                        <span>{a.displayName}</span> <small>({a.type})</small>
                      </button>
                      {a.description && (
                        <small className="description" id={descriptionId(a.type)}>
                          {a.description}
                        </small>
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </li>
          );
        })}
      </ul>
      <LoadedPlugins />
    </aside>
  );
}

interface FolderNode {
  readonly name: string;
  readonly path: string;
  readonly folders: FolderNode[];
  readonly files: WorkflowFile[];
}

/** The project's files as folders (sorted by name) with their files. */
function fileTree(files: readonly WorkflowFile[]): FolderNode {
  const root: FolderNode = { name: '', path: '', folders: [], files: [] };
  for (const file of files) {
    let folder = root;
    for (const segment of file.path.split('/').slice(0, -1)) {
      let next = folder.folders.find((f) => f.name === segment);
      if (!next) {
        next = { name: segment, path: folder.path ? `${folder.path}/${segment}` : segment, folders: [], files: [] };
        folder.folders.push(next);
        folder.folders.sort((a, b) => a.name.localeCompare(b.name));
      }

      folder = next;
    }

    folder.files.push(file);
  }

  return root;
}

/** The files in display order (each folder's subfolders first, then its files). */
function displayOrder(folder: FolderNode): string[] {
  return [...folder.folders.flatMap(displayOrder), ...folder.files.map((f) => f.path)];
}

/** The project's workflow files as a tree. Click selects, double-click or Enter opens, F2 renames, Delete deletes. */
function FilesPanel() {
  const studio = useStudio();
  const files = useStudioState((s) => s.files);
  const project = useStudioState((s) => s.project);
  const openPath = useStudioState((s) => s.file?.path);
  const [selected, setSelected] = useState<string>();
  const list = useRef<HTMLUListElement>(null);
  const tree = useMemo(() => fileTree(files), [files]);
  const order = useMemo(() => displayOrder(tree), [tree]);
  const target = selected !== undefined && order.includes(selected) ? selected : openPath;

  const select = (path: string) => {
    setSelected(path);
    // Compared, not interpolated into a selector: paths may contain quotes or brackets.
    [...(list.current?.querySelectorAll<HTMLElement>('[data-path]') ?? [])].find((item) => item.dataset.path === path)?.focus();
  };

  const onKeyDown = (event: KeyboardEvent<HTMLUListElement>) => {
    const at = target === undefined ? -1 : order.indexOf(target);
    const next =
      event.key === 'ArrowDown' ? order[Math.min(at + 1, order.length - 1)]
      : event.key === 'ArrowUp' ? order[Math.max(at - 1, 0)]
      : event.key === 'Home' ? order[0]
      : event.key === 'End' ? order.at(-1)
      : undefined;
    if (next !== undefined) {
      event.preventDefault();
      select(next);
    } else if (target !== undefined && event.key === 'Enter') {
      event.preventDefault();
      void studio.requestOpen(target);
    } else if (target !== undefined && event.key === 'F2') {
      event.preventDefault();
      studio.startName('rename', target);
    } else if (target !== undefined && event.key === 'Delete') {
      event.preventDefault();
      studio.startDelete(target);
    }
  };

  const renderFolder = (folder: FolderNode, depth: number) => (
    <>
      {folder.folders.map((sub) => (
        <li key={`folder:${sub.path}`} role="treeitem" aria-level={depth} aria-expanded={true} aria-selected={false} className="folder">
          <span className="folder-name">{sub.name}/</span>
          <ul role="group">{renderFolder(sub, depth + 1)}</ul>
        </li>
      ))}
      {folder.files.map((file) => {
        const name = file.path.split('/').at(-1);
        return (
          <li
            key={file.path}
            role="treeitem"
            aria-level={depth}
            aria-selected={file.path === target}
            aria-current={file.path === openPath ? 'true' : undefined}
            tabIndex={file.path === target || (target === undefined && file.path === order[0]) ? 0 : -1}
            data-path={file.path}
            className={`file${file.path === target ? ' selected' : ''}${file.path === openPath ? ' open' : ''}`}
            onClick={() => select(file.path)}
            onDoubleClick={() => void studio.requestOpen(file.path)}
          >
            {name}
          </li>
        );
      })}
    </>
  );

  return (
    <section className="files" aria-labelledby="files-heading">
      <div className="files-header">
        <h2 id="files-heading">Files</h2>
        <div className="editbar" role="toolbar" aria-label="Files">
          <button type="button" onClick={() => studio.startName('new')} disabled={project === undefined} title="Create a new workflow in the project">
            New…
          </button>
          <button type="button" onClick={() => target && studio.startName('rename', target)} disabled={target === undefined} title={target ? `Rename or move ${target} (F2)` : 'Select a file'}>
            Rename…
          </button>
          <button type="button" onClick={() => target && studio.startDelete(target)} disabled={target === undefined} title={target ? `Delete ${target} (Delete)` : 'Select a file'}>
            Delete…
          </button>
        </div>
      </div>
      <p className="hint">Double-click or Enter opens a file.</p>
      <ul role="tree" aria-label="Workflow files" ref={list} onKeyDown={onKeyDown}>
        {renderFolder(tree, 1)}
      </ul>
    </section>
  );
}

/** A modal dialog (native `<dialog>`, CSP-safe); Esc calls `onCancel`. */
function Modal({ title, onCancel, children }: { title: string; onCancel: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null);
  const id = useId();
  useEffect(() => {
    const element = ref.current;
    if (element && !element.open) {
      if (typeof element.showModal === 'function') {
        element.showModal();
      } else {
        element.setAttribute('open', '');
      }
    }
  }, []);

  return (
    <dialog
      ref={ref}
      className="run-dialog"
      aria-labelledby={id}
      onCancel={(e) => {
        e.preventDefault();
        onCancel();
      }}
    >
      <h2 id={id}>{title}</h2>
      {children}
    </dialog>
  );
}

/** The file-management dialogs (W6): unsaved changes, a path, delete, save conflict, recovery. */
function StudioDialogHost() {
  const dialog = useStudioState((s) => s.dialog);
  return dialog ? <StudioDialogView key={`${dialog.kind}:${'purpose' in dialog ? dialog.purpose : ''}`} dialog={dialog} /> : null;
}

function StudioDialogView({ dialog }: { dialog: StudioDialog }) {
  const studio = useStudio();
  const close = () => studio.closeDialog();
  switch (dialog.kind) {
    case 'unsaved':
      return (
        <Modal title="Unsaved changes" onCancel={() => void studio.resolveUnsaved('cancel')}>
          <p>
            {dialog.path} has unsaved changes. Save them before opening {dialog.next}?
          </p>
          <div className="dialog-buttons">
            <button type="button" onClick={() => void studio.resolveUnsaved('save')}>
              Save
            </button>
            <button type="button" onClick={() => void studio.resolveUnsaved('discard')}>
              Discard
            </button>
            <button type="button" onClick={() => void studio.resolveUnsaved('cancel')}>
              Cancel
            </button>
          </div>
        </Modal>
      );
    case 'name':
      return <NameDialog dialog={dialog} />;
    case 'delete':
      return (
        <Modal title={`Delete ${dialog.path}`} onCancel={close}>
          <p>
            Delete {dialog.path} from the project?{dialog.dirty ? ' It is open with unsaved changes, which will be lost.' : ''} This cannot be undone.
          </p>
          <div className="dialog-buttons">
            <button type="button" onClick={() => void studio.confirmDelete()}>
              Delete
            </button>
            <button type="button" onClick={close}>
              Cancel
            </button>
          </div>
        </Modal>
      );
    case 'conflict':
      return (
        <Modal title="The file changed on disk" onCancel={() => void studio.resolveConflict('cancel')}>
          <p>{dialog.path} was changed outside this Studio since it was opened. Your changes are kept until you choose.</p>
          <div className="dialog-buttons">
            <button type="button" onClick={() => void studio.resolveConflict('reload')}>
              Reload from disk
            </button>
            <button type="button" onClick={() => void studio.resolveConflict('overwrite')}>
              Overwrite with mine
            </button>
            <button type="button" onClick={() => void studio.resolveConflict('save-as')}>
              Save mine as…
            </button>
            <button type="button" onClick={() => void studio.resolveConflict('cancel')}>
              Cancel
            </button>
          </div>
        </Modal>
      );
    case 'recover':
      return (
        <Modal title="Recover unsaved changes" onCancel={close}>
          <p>
            Unsaved changes to {dialog.path} from {new Date(dialog.savedAt).toLocaleString()} were found in this browser.
            {dialog.stale ? ' The file has changed on disk since then: restoring replaces those changes when you save.' : ''}
          </p>
          <div className="dialog-buttons">
            <button type="button" onClick={() => studio.resolveRecovery('restore')}>
              Restore
            </button>
            <button type="button" onClick={() => studio.resolveRecovery('discard')}>
              Discard
            </button>
          </div>
        </Modal>
      );
  }
}

function NameDialog({ dialog }: { dialog: Extract<StudioDialog, { kind: 'name' }> }) {
  const studio = useStudio();
  const id = useId();
  const [path, setPath] = useState(dialog.initial);
  const title = dialog.purpose === 'new' ? 'New workflow' : dialog.purpose === 'rename' ? `Rename ${dialog.from}` : 'Save as';
  const action = dialog.purpose === 'new' ? 'Create' : dialog.purpose === 'rename' ? 'Rename' : 'Save';
  return (
    <Modal title={title} onCancel={() => studio.closeDialog()}>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          void studio.submitName(path);
        }}
      >
        <div className="field">
          <label className="field-label" htmlFor={id}>
            Path in the project
          </label>
          <input id={id} value={path} spellCheck={false} aria-invalid={dialog.error !== undefined} aria-describedby={`${id}-error`} onChange={(e) => setPath(e.target.value)} />
          <span id={`${id}-error`} className="field-error" role="status">
            {dialog.error}
          </span>
        </div>
        <div className="dialog-buttons">
          <button type="submit" disabled={path.trim() === ''}>
            {action}
          </button>
          <button type="button" onClick={() => studio.closeDialog()}>
            Cancel
          </button>
        </div>
      </form>
    </Modal>
  );
}

function WorkflowTree() {
  const studio = useStudio();
  const document = useStudioState((s) => s.document);
  const selectedKey = useStudioState((s) => s.selectedKey);
  const showsRun = useStudioState((s) => s.treeShowsRun);
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
      <main className="designer" aria-labelledby="designer-heading">
        <h2 id="designer-heading">Workflow</h2>
        <p className="hint">Choose a workflow and press Open.</p>
      </main>
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
    <main className="designer" aria-labelledby="designer-heading">
      <h2 id="designer-heading">Workflow</h2>
      <PluginNotice />
      <EditBar />
      <Breadcrumbs />
      {/* While a run of this file is shown, nodes without a run state were not executed (styled as such). */}
      <ul role="tree" aria-labelledby="designer-heading" ref={tree} onKeyDown={onKeyDown} className={showsRun ? 'shows-run' : undefined}>
        {isObject(root) && <TreeNode node={root} depth={1} />}
      </ul>
    </main>
  );
}

/** Where the selection is: Workflow › ancestors › selected node (slot names included); each step selects it. */
function Breadcrumbs() {
  const studio = useStudio();
  const document = useStudioState((s) => s.document);
  const selectedKey = useStudioState((s) => s.selectedKey);
  const catalog = useStudioState((s) => s.catalog);
  if (document === undefined) {
    return null;
  }

  const path = selectedKey === undefined ? undefined : indexDocument(document).byKey.get(selectedKey)?.path;
  const crumbs =
    path === undefined
      ? []
      : Array.from({ length: path.length + 1 }, (_, depth) => {
          const node = nodeAt(document, path.slice(0, depth));
          const step = depth === 0 ? undefined : path[depth - 1];
          const type = typeof node.type === 'string' ? node.type : undefined;
          return { key: keyOf(node), slot: step && 'slot' in step ? step.slot : undefined, label: nodeLabel(node, type ? catalog.get(type) : undefined) };
        });

  return (
    <nav className="breadcrumbs" aria-label="Selection">
      <ol>
        <li>
          <button type="button" className="link" aria-current={selectedKey === workflowKey ? 'location' : undefined} onClick={() => studio.selectWorkflow()}>
            Workflow
          </button>
        </li>
        {crumbs.map((crumb, i) => (
          <li key={crumb.key}>
            <span aria-hidden="true"> › </span>
            {crumb.slot !== undefined && <span className="slot">{crumb.slot}: </span>}
            <button type="button" className="link" aria-current={i === crumbs.length - 1 ? 'location' : undefined} onClick={() => studio.select(crumb.key)}>
              {crumb.label}
            </button>
          </li>
        ))}
      </ol>
    </nav>
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
  const hasSelection = useStudioState((s) => s.document !== undefined && s.selectedKey !== undefined && indexDocument(s.document).byKey.has(s.selectedKey));
  const pasteRefusal = useStudioState(insertRefusal);

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
      <button type="button" onClick={() => toSystemClipboard(studio.cutSelected())} disabled={remove !== undefined} title={remove ?? 'Cut the selected activity (Ctrl+X)'}>
        Cut
      </button>
      <button type="button" onClick={() => toSystemClipboard(studio.copySelected())} disabled={!hasSelection} title={hasSelection ? 'Copy the selected activity (Ctrl+C)' : 'Select an activity to copy'}>
        Copy
      </button>
      <button type="button" onClick={() => studio.paste()} disabled={pasteRefusal !== undefined} title={pasteRefusal ?? 'Paste what was copied in this tab (Ctrl+V pastes the clipboard)'}>
        Paste
      </button>
    </div>
  );
}

/** Also puts copied activities on the system clipboard (allowed on a click, without a prompt); failures are ignored. */
function toSystemClipboard(text: string | undefined): void {
  if (text !== undefined) {
    void navigator.clipboard?.writeText(text).catch(() => undefined);
  }
}

/** One node. Memoized on the node object: an edit re-renders only the edited node and its ancestors. */
const TreeNode = memo(function TreeNode({ node, depth, slot }: { node: JsonObject; depth: number; slot?: string }) {
  const studio = useStudio();
  const key = keyOf(node);
  const id = typeof node.id === 'string' ? node.id : undefined;
  const type = typeof node.type === 'string' ? node.type : undefined;
  // One subscription for everything per-node that changes often (selection, error, run state, picked zone): with
  // 3,000 nodes each extra subscription is 3,000 more listener calls per keystroke. The snapshot is a string, so the
  // store's identity check stays exact. Fields are separated by NUL (never in ids, statuses or slot names).
  const view = useStudioState(
    (s) =>
      `${s.selectedKey === key ? 1 : 0}\0${id !== undefined && s.errorNodeIds.has(id) ? 1 : 0}\0${id === undefined ? '' : (s.nodeStatus.get(id) ?? '')}\0${s.insertTarget?.parentKey === key ? JSON.stringify(s.insertTarget.position) : ''}`,
  );
  const [selectedFlag, errorFlag, statusText, pickedText] = view.split('\0');
  const selected = selectedFlag === '1';
  const hasError = errorFlag === '1';
  const status = statusText === '' ? undefined : statusText;
  const picked = pickedText === '' ? undefined : pickedText;
  const activity = useStudioState((s) => (type === undefined ? undefined : s.catalog.get(type)));
  const children = childSteps(node);
  // Containers (a list or slots) get a visible boundary; empty ones say so in the card (not as tree items).
  const container = activity !== undefined && (activity.allowsChildren || activity.slots.length > 0);
  const emptyList = activity?.allowsChildren === true && !(Array.isArray(node.children) && node.children.length > 0);
  const presentSlots = isObject(node.slots) ? node.slots : {};
  const missingSlots = activity?.slots.filter((s) => !s.prefix && !(s.name in presentSlots)) ?? [];
  const prefixSlots = activity?.slots.filter((s) => s.prefix) ?? [];

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
      <div className={`node${container ? ' container' : ''}${selected ? ' selected' : ''}${hasError ? ' has-error' : ''}`} data-run-status={status}>
        {slot !== undefined && <span className="slot">{slot}:</span>}
        <span className="label">{nodeLabel(node, activity)}</span>
        <span className="type">{type}</span>
        {id !== undefined && <span className="id">#{id}</span>}
        {hasError && <span className="badge error">error</span>}
        {status !== undefined && <span className={`badge status-${status.toLowerCase()}`}>{status}</span>}
        {activity === undefined && type !== undefined && <span className="badge">not in catalog</span>}
        {(emptyList || missingSlots.length > 0 || prefixSlots.length > 0) && (
          <span className="zones">
            {emptyList && <Zone parentKey={key} position={{ index: 0 }} label="Empty list: insert here" picked={picked} />}
            {missingSlots.map((s) => (
              <Zone key={s.name} parentKey={key} position={{ slot: s.name }} label={`${s.name}: empty${s.required ? ' (required)' : ''}`} required={s.required} picked={picked} />
            ))}
            {prefixSlots.map((s) => (
              <CaseZone key={s.name} parentKey={key} prefix={s.name} picked={picked} />
            ))}
          </span>
        )}
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

/**
 * An empty slot or an empty list inside a card (W7): clicking it (or Enter) picks it as the place for the next insert
 * or paste; it is also a drop target (`data-drop-*`, see dragdrop.ts).
 */
function Zone({ parentKey, position, label, required, picked }: { parentKey: string; position: Position; label: string; required?: boolean; picked: string | undefined }) {
  const studio = useStudio();
  const isPicked = picked === JSON.stringify(position);
  return (
    <button
      type="button"
      className={`drop-zone${required ? ' required-slot' : ''}${isPicked ? ' picked' : ''}`}
      data-drop-parent={parentKey}
      data-drop-index={'index' in position ? position.index : undefined}
      data-drop-slot={'slot' in position ? position.slot : undefined}
      aria-pressed={isPicked}
      title={isPicked ? 'The next insert or paste goes here' : 'Insert or paste here next'}
      onClick={(event) => {
        event.stopPropagation();
        studio.setInsertTarget(isPicked ? undefined : { parentKey, position });
      }}
    >
      {label}
    </button>
  );
}

/** A new prefix slot (Switch `case:<value>`): type the value, then pick it as the place for the next insert or drop. */
function CaseZone({ parentKey, prefix, picked }: { parentKey: string; prefix: string; picked: string | undefined }) {
  const studio = useStudio();
  const [value, setValue] = useState('');
  const slot = `${prefix}${value.trim()}`;
  const pickedSlot = picked === undefined ? undefined : (JSON.parse(picked) as Position);
  const isPicked = pickedSlot !== undefined && 'slot' in pickedSlot && pickedSlot.slot.startsWith(prefix);
  const pick = () => value.trim() !== '' && studio.setInsertTarget({ parentKey, position: { slot } });
  return (
    <span className={`drop-zone case-zone${isPicked ? ' picked' : ''}`} data-drop-parent={value.trim() !== '' ? parentKey : undefined} data-drop-slot={value.trim() !== '' ? slot : undefined} onClick={(e) => e.stopPropagation()}>
      + {prefix}
      <input
        className="code"
        aria-label={`New ${prefix.replace(/:$/, '')} value`}
        value={value}
        placeholder="value"
        spellCheck={false}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={(e) => {
          e.stopPropagation();
          if (e.key === 'Enter') {
            pick();
          }
        }}
      />
      <button type="button" className="small" disabled={value.trim() === ''} onClick={pick} title={`Insert or paste into ${slot} next`}>
        Pick
      </button>
    </span>
  );
}

/** The lower area: workflow data (problems, variables, arguments) beside the execution of runs. */
function OutputPanel() {
  return (
    <section className="output" aria-label="Output">
      <DataPanel />
      <ExecutionPanel />
    </section>
  );
}

/** The current time, refreshed every second while `active`. */
function useNow(active: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) {
      return;
    }

    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [active]);
  return now;
}

/** " (Core.Throw, ElementNotFound)": the failing activity's type and the error's classification, when known. */
function failureKind(error: { activityType?: string | null; errorType?: string | null }): string {
  const parts = [error.activityType, error.errorType].filter((p): p is string => typeof p === 'string' && p !== '');
  return parts.length > 0 ? ` (${parts.join(', ')})` : '';
}

/** Plugins that failed to load (optional ones; a required one stops the server) and what each said. */
function PluginNotice() {
  const diagnostics = useStudioState((s) => s.plugins?.diagnostics);
  if (!diagnostics || diagnostics.length === 0) {
    return null;
  }

  return (
    <div className="notice" role="alert">
      <strong>Plugin problems ({diagnostics.length}).</strong> Activities of a plugin that did not load are shown as not in the catalog.
      <ul>
        {diagnostics.map((d, i) => (
          <li key={i}>
            {d.severity} {d.code}
            {d.pluginId ? ` (${d.pluginId})` : ''}: {d.message}
          </li>
        ))}
      </ul>
    </div>
  );
}

/** The plugins the server loaded, under the activity catalog. */
function LoadedPlugins() {
  const plugins = useStudioState((s) => s.plugins?.plugins);
  if (!plugins || plugins.length === 0) {
    return null;
  }

  return (
    <p className="hint loaded-plugins">
      Plugins: {plugins.map((p) => `${p.name} ${p.version} (${p.activities.length} activities)`).join('; ')}
    </p>
  );
}

const duration = (ms: number) => (ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(1)} s`);

/** The failed node, when the run ran the open file and the node is in it (an invoked workflow's node is not). */
function failedNodeOf(state: StudioState): string | undefined {
  const run = currentRun(state);
  const id = run?.error?.nodeId;
  const sameFile = run !== undefined && run.project === state.file?.project && run.path === state.file.path;
  return id && sameFile && state.document && indexDocument(state.document).byNodeId.has(id) ? id : undefined;
}

/** The current run (or one chosen from the recent runs): its state, timing, failure, outputs and events. */
function ExecutionPanel() {
  const studio = useStudio();
  const runs = useStudioState((s) => s.runs);
  const run = useStudioState(currentRun);
  const stream = useStudioState((s) => s.stream);
  const failedNode = useStudioState(failedNodeOf);
  const active = run !== undefined && isActive(run);
  const now = useNow(active && run.startedAt !== undefined);
  const elapsed = run?.durationMs ?? (run?.startedAt && active ? Math.max(0, now - Date.parse(run.startedAt)) : undefined);
  const executing = active ? run.runningNodes.at(-1) : undefined;

  return (
    <div className="run" role="region" aria-labelledby="execution-heading">
      <h2 id="execution-heading">Execution</h2>
      {runs.length > 1 && (
        <label className="recent-runs">
          Recent runs{' '}
          <select value={run?.key} onChange={(e) => studio.selectRun(e.target.value)}>
            {runs.map((r) => (
              <option key={r.key} value={r.key}>
                {time(r.requestedAt)} {r.path} — {statusLabel(r)}
              </option>
            ))}
          </select>
        </label>
      )}
      {stream === 'reconnecting' && <p className="hint" data-testid="stream-reconnecting">The event stream was interrupted; reconnecting…</p>}
      <p data-testid="run-status">
        Status: <strong>{run ? statusLabel(run) : 'Not run'}</strong>
        {run?.runId && <span className="hint"> · run {run.runId}</span>}
      </p>
      {run && (
        <dl className="facts run-facts">
          <dt>Workflow</dt>
          <dd>{run.path}</dd>
          {run.startedAt && (
            <>
              <dt>Started</dt>
              <dd data-testid="run-started">{time(run.startedAt)}</dd>
            </>
          )}
          {elapsed !== undefined && (
            <>
              <dt>{run.durationMs !== undefined ? 'Duration' : 'Elapsed'}</dt>
              <dd data-testid="run-elapsed">{duration(elapsed)}</dd>
            </>
          )}
          {executing && (
            <>
              <dt>Running</dt>
              <dd data-testid="run-current">{executing}</dd>
            </>
          )}
          {run.result && (
            <>
              <dt>Execution id</dt>
              <dd data-testid="run-execution-id">{run.result.executionId}</dd>
              <dt>Correlation id</dt>
              <dd data-testid="run-correlation-id">{run.result.correlationId}</dd>
            </>
          )}
        </dl>
      )}
      {run?.notStarted && (
        <p className="field-error" data-testid="run-not-started">
          {run.notStarted.message}
        </p>
      )}
      {run?.error && (
        <p className="field-error" data-testid="run-error">
          {run.error.code}
          {run.error.nodeId ? ` at ${run.error.nodeId}` : ''}
          {failureKind(run.error)}: {run.error.message}{' '}
          {failedNode && (
            <button type="button" onClick={() => studio.selectNodeId(failedNode)}>
              Select failed node
            </button>
          )}
        </p>
      )}
      {run !== undefined && run.missingEvents > 0 && (
        <p className="hint" data-testid="run-gap">
          {run.missingEvents} earlier event(s) of this run are no longer available on the server.
        </p>
      )}
      {run?.result && Object.keys(run.result.outputs).length > 0 && <p data-testid="run-outputs">Outputs: {JSON.stringify(run.result.outputs)}</p>}
      {run !== undefined && run.events.length > 0 && (
        <button type="button" className="small" onClick={() => studio.clearLog(run.key)} title="Clear the events and logs shown for this run">
          Clear log
        </button>
      )}
      <ol className="events" aria-label="Execution events">
        {(run?.events ?? []).map((e, i) => (
          <EventRow key={e.kind === 'stream.gap' ? `gap-${i}` : `${e.runId}:${e.sequence}`} event={e} />
        ))}
      </ol>
    </div>
  );
}

/** One event. Memoized on the event object: a batch of new events renders only the new rows. */
const EventRow = memo(function EventRow({ event: e }: { event: ExecutionEvent }) {
  return (
    <li className={`event event-${e.kind.replace('.', '-')}`}>
      <span className="time">[{time(e.time)}]</span> <span className="seq">{e.sequence}</span> <span className="kind">{e.kind}</span>
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
  );
});

/** The largest `timeoutMs` the server accepts (a 32-bit integer, about 24.8 days). */
const maxTimeoutMs = 2_147_483_647;

function RunDialogHost() {
  const dialog = useStudioState((s) => s.runDialog);
  return dialog ? <RunDialog dialog={dialog} /> : null;
}

/**
 * Run configuration: one text field per input argument. The text goes to the server as typed and is parsed there like
 * the CLI's --arg (ADR-0030); blank keeps the declared default. Only the `required` flag is checked here.
 */
function RunDialog({ dialog }: { dialog: RunDialogState }) {
  const studio = useStudio();
  const ref = useRef<HTMLDialogElement>(null);
  const id = useId();
  const [values, setValues] = useState<Record<string, string>>(() => ({ ...dialog.values }));
  const [timeout, setTimeoutText] = useState(dialog.timeoutMs?.toString() ?? '');

  useEffect(() => {
    const element = ref.current;
    if (element && !element.open) {
      if (typeof element.showModal === 'function') {
        element.showModal();
      } else {
        element.setAttribute('open', '');
      }
    }
  }, []);

  const missing = dialog.arguments.filter((a) => a.required && (values[a.name] ?? '').trim() === '').map((a) => a.name);
  const timeoutMs = timeout.trim() === '' ? undefined : Number(timeout);
  const refusal =
    missing.length > 0 ? `Enter the required argument(s): ${missing.join(', ')}.`
    : timeoutMs !== undefined && !(Number.isInteger(timeoutMs) && timeoutMs > 0 && timeoutMs <= maxTimeoutMs)
      ? `The timeout must be a whole number of milliseconds from 1 to ${maxTimeoutMs}.`
    : undefined;

  return (
    <dialog
      ref={ref}
      className="run-dialog"
      aria-labelledby={`${id}-title`}
      onCancel={(e) => {
        e.preventDefault();
        studio.closeRunDialog();
      }}
    >
      <form
        method="dialog"
        onSubmit={(e) => {
          e.preventDefault();
          if (refusal === undefined) {
            void studio.startFromDialog(values, timeoutMs);
          }
        }}
      >
        <h2 id={`${id}-title`}>Run {dialog.path}</h2>
        <p className="hint">Values are read like the command line's --arg. Leave a field blank to use its default.</p>
        {dialog.arguments.map((a) => (
          <div className="field" key={a.name}>
            <label className="field-label" htmlFor={`${id}-${a.name}`}>
              {a.name}
              {a.required && <span aria-label="required"> *</span>} <small>{a.type}</small>
            </label>
            <input
              id={`${id}-${a.name}`}
              value={values[a.name] ?? ''}
              spellCheck={false}
              placeholder={a.defaultJson !== undefined ? `default: ${a.defaultJson}` : a.required ? 'required' : 'no default'}
              onChange={(e) => setValues({ ...values, [a.name]: e.target.value })}
            />
          </div>
        ))}
        <div className="field">
          <label className="field-label" htmlFor={`${id}-timeout`}>
            Timeout (ms) <small>optional</small>
          </label>
          <input id={`${id}-timeout`} inputMode="numeric" value={timeout} placeholder="engine default" onChange={(e) => setTimeoutText(e.target.value)} />
        </div>
        <p className="hint" id={`${id}-refusal`} role="status">
          {refusal}
        </p>
        <div className="dialog-buttons">
          <button type="submit" disabled={refusal !== undefined} aria-describedby={`${id}-refusal`}>
            Start
          </button>
          <button type="button" onClick={() => studio.closeRunDialog()}>
            Cancel
          </button>
        </div>
      </form>
    </dialog>
  );
}
