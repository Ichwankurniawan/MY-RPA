// W0 spike: property editors generated from the catalog; expressions use CodeMirror 6.
// Every keystroke updates the document (coalesced into one undo step), so there is no "typed but not committed" state.
import { useContext, useEffect, useRef } from 'react';
import { Annotation, EditorState } from '@codemirror/state';
import { EditorView } from '@codemirror/view';
import { CatalogContext } from './Designer';
import { type Json, type Path, getNode, pathKey, setProperty } from './model';
import { store, useDoc, useSelected } from './store';

export function Properties() {
  const doc = useDoc();
  const selected = useSelected();
  const catalog = useContext(CatalogContext);
  if (!doc || !selected) {
    return <div className="props muted">Select a block.</div>;
  }
  const node = getNode(doc.root, selected);
  const descriptor = catalog.get(node.type);
  return (
    <div className="props">
      <h3>{descriptor?.displayName ?? node.type}</h3>
      <div className="muted">{node.id}</div>
      {(descriptor?.properties ?? []).map((p) => (
        <label key={`${pathKey(selected)}:${p.name}`} className="prop">
          <span>{p.name}{p.required ? ' *' : ''} <em>{p.kind}</em></span>
          {p.kind === 'Expression' ? (
            <ExpressionEditor path={selected} name={p.name} value={node.properties?.[p.name]} />
          ) : (
            <input
              value={String(node.properties?.[p.name] ?? '')}
              onChange={(e) => store.apply(setProperty(store.doc!, selected, p.name, e.target.value), `${pathKey(selected)}:${p.name}`)}
            />
          )}
        </label>
      ))}
    </div>
  );
}

function ExpressionEditor({ path, name, value }: { path: Path; name: string; value: Json | undefined }) {
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);
  const text = value === undefined ? '' : typeof value === 'string' ? value : JSON.stringify(value);

  useEffect(() => {
    const started = performance.now();
    const editor = new EditorView({
      parent: host.current!,
      state: EditorState.create({
        doc: text,
        extensions: [
          EditorView.lineWrapping,
          EditorView.contentAttributes.of({ 'aria-label': name }),
          EditorView.updateListener.of((update) => {
            if (update.docChanged && !update.transactions.some((t) => t.annotation(External))) {
              store.apply(setProperty(store.doc!, path, name, update.state.doc.toString()), `${pathKey(path)}:${name}`);
            }
          }),
        ],
      }),
    });
    view.current = editor;
    (window as unknown as { __lastEditorMountMs?: number }).__lastEditorMountMs = performance.now() - started;
    return () => editor.destroy();
    // Created once per (node, property); external changes (undo) are synced below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pathKey(path), name]);

  useEffect(() => {
    const editor = view.current;
    if (editor && editor.state.doc.toString() !== text) {
      editor.dispatch({ changes: { from: 0, to: editor.state.doc.length, insert: text }, annotations: External.of(true) });
    }
  }, [text]);

  return <div ref={host} className="cm-host" data-property={name} />;
}

const External = Annotation.define<boolean>();

export function editorFor(name: string): EditorView | null {
  const host = document.querySelector(`.cm-host[data-property="${name}"] .cm-editor`);
  return host ? EditorView.findFromDOM(host as HTMLElement) : null;
}
