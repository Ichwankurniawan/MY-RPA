// W0 spike app shell: toolbox from the catalog API, designer, properties, results. Not the production UX.
import { Profiler, useEffect, useState } from 'react';
import { CatalogContext, Designer, type Descriptor } from './Designer';
import { Properties } from './Properties';
import { flatDocument, nestedDocument } from './model';
import { store, useDoc } from './store';
import { commits, runDesigner, runSse } from './bench';

export function App() {
  const doc = useDoc();
  const [catalog, setCatalog] = useState(new Map<string, Descriptor>());
  const [results, setResults] = useState('');

  useEffect(() => {
    fetch('/api/activities')
      .then((r) => r.json())
      .then((c: { activities: Descriptor[] }) => setCatalog(new Map(c.activities.map((a) => [a.type, a]))));
  }, []);

  useEffect(() => {
    (window as unknown as Record<string, unknown>).__bench = {
      designer: async (shape: 'flat' | 'nested', n = 3000) => {
        const r = await runDesigner(shape, n);
        setResults(JSON.stringify(r, null, 1));
        return r;
      },
      sse: async () => {
        const r = await runSse();
        setResults(JSON.stringify(r, null, 1));
        return r;
      },
      catalogSize: () => catalog.size,
    };
  }, [catalog]);

  return (
    <CatalogContext.Provider value={catalog}>
      <div className="app">
        <header>
          <strong>W0 spike</strong>
          <button onClick={() => store.load(flatDocument(3000))}>Load flat 3,000</button>
          <button onClick={() => store.load(nestedDocument(3000))}>Load nested 3,000</button>
          <button onClick={() => store.undo()}>Undo</button>
          <button onClick={() => store.redo()}>Redo</button>
          <span className="muted">{catalog.size} activities</span>
        </header>
        <aside className="toolbox">
          {[...new Set([...catalog.values()].map((d) => d.category))].map((category) => (
            <div key={category}>
              <div className="cat">{category}</div>
              {[...catalog.values()].filter((d) => d.category === category).map((d) => <div key={d.type} className="tool" title={d.description}>{d.displayName}</div>)}
            </div>
          ))}
        </aside>
        <main>
          <Profiler id="designer" onRender={(_id, _phase, duration) => commits.push(duration)}>
            {doc ? <Designer doc={doc} /> : <div className="muted">Load a document.</div>}
          </Profiler>
        </main>
        <section className="side">
          <Properties />
          <pre className="results">{results}</pre>
        </section>
      </div>
    </CatalogContext.Provider>
  );
}
