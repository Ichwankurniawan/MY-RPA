// Agent tools (ADR-0048 amendment): the AI Agent plugin's built-in tools and the MCP servers an administrator configured,
// with a connection test; and the picker that writes an AI.Agent's `tools` JSON. Servers are read-only here: they are
// configured in the plugin's settings, and nothing in the Studio can add a server, a URL or a command.
import { useEffect, useId, useState } from 'react';
import { useStudio, useStudioState } from './context';
import { Badge, PageHeader } from './Operations';
import type { AgentToolsInfo, McpDiscoveryView, McpServerView, PropertyDescriptor } from './types';

type Loaded = { readonly info?: AgentToolsInfo; readonly error?: string };

/** Loads the plugin's tools once per mount (the configuration changes only when the Server restarts). */
function useAgentTools(): Loaded {
  const studio = useStudio();
  const [loaded, setLoaded] = useState<Loaded>({});
  useEffect(() => {
    let live = true;
    studio.agentToolsInfo().then(
      (info) => live && setLoaded({ info }),
      (error: unknown) => live && setLoaded({ error: (error as Error).message }),
    );
    return () => {
      live = false;
    };
  }, [studio]);
  return loaded;
}

type DiscoveryState = McpDiscoveryView | 'busy' | { readonly failed: string };

/** One connection test per server, kept while the page is open. */
function useDiscovery() {
  const studio = useStudio();
  const [results, setResults] = useState<Readonly<Record<string, DiscoveryState>>>({});
  const discover = async (server: string) => {
    setResults((r) => ({ ...r, [server]: 'busy' }));
    try {
      const result = await studio.discoverMcp(server);
      setResults((r) => ({ ...r, [server]: result }));
    } catch (error) {
      setResults((r) => ({ ...r, [server]: { failed: (error as Error).message } }));
    }
  };
  return { results, discover };
}

const allowedText = (server: McpServerView): string => (server.allowedTools.includes('*') ? 'every tool the server lists' : server.allowedTools.join(', '));

function ServerRow({ server, result, onTest }: { server: McpServerView; result: DiscoveryState | undefined; onTest: () => void }) {
  return (
    <li className="ops-card mcp-server" aria-label={`MCP server ${server.name}`}>
      <div className="page-header">
        <h4>
          {server.name} <Badge value={server.enabled ? 'Enabled' : 'Disabled'} /> <small>{server.transport === 'http' ? 'Streamable HTTP' : 'stdio process'}</small>
        </h4>
        <button type="button" disabled={result === 'busy' || !server.enabled} title={server.enabled ? 'Connects to the server and lists its tools' : 'The server is disabled in the configuration'} onClick={onTest}>
          {result === 'busy' ? 'Testing…' : 'Test connection'}
        </button>
      </div>
      <dl className="ops-facts">
        <dt>{server.transport === 'http' ? 'URL' : 'Command'}</dt>
        <dd>
          <code>{server.endpoint}</code>
        </dd>
        <dt>Allowed tools</dt>
        <dd>{allowedText(server)}</dd>
        {server.approvalTools.length > 0 && (
          <>
            <dt>Need approval (always refused for now)</dt>
            <dd>{server.approvalTools.join(', ')}</dd>
          </>
        )}
        {server.tokenVariable && (
          <>
            <dt>Token</dt>
            <dd>
              from the environment variable <code>{server.tokenVariable}</code>: {server.tokenSet ? 'set' : 'not set for the Server'}
            </dd>
          </>
        )}
      </dl>
      {result !== undefined && result !== 'busy' && (
        <div aria-live="polite">
          {'failed' in result ? (
            <p className="ops-result error">{result.failed}</p>
          ) : !result.ok ? (
            <p className="ops-result error">Could not list the tools: {result.error}</p>
          ) : (
            <>
              <p className="ops-result ok">
                Connected in {result.durationMs} ms: {result.tools.length} tool(s).
              </p>
              <ul className="ops-list">
                {result.tools.map((t) => (
                  <li key={t.name}>
                    <code>{t.name}</code> {t.requiresApproval ? <Badge value="Approval" /> : t.allowed ? <Badge value="Allowed" /> : <Badge value="Not allowed" />} <span className="hint">{t.description}</span>
                  </li>
                ))}
              </ul>
            </>
          )}
        </div>
      )}
    </li>
  );
}

/** The Agent tools page: built-ins and MCP servers of the AI Agent plugin. */
export function AgentToolsPage() {
  const loaded = useAgentTools();
  const { results, discover } = useDiscovery();
  return (
    <main className="home ops-page" aria-labelledby="agent-tools-heading">
      <PageHeader id="agent-tools-heading" icon="plugin" title="Agent tools" />
      <p className="hint">
        An AI Agent step may call only the tools its author lists: project workflows, the built-ins below, and tools of the MCP servers below. Servers are configured by an administrator in the AI Agent plugin&apos;s settings (the Server&apos;s plugin configuration); a model, a workflow or this page can never add one. Changes take effect when the Server restarts.
      </p>
      {loaded.info === undefined ? (
        loaded.error !== undefined ? (
          <p className="ops-result error" role="alert">
            Could not load the agent tools: {loaded.error}
          </p>
        ) : (
          <p className="hint" aria-live="polite">
            Loading…
          </p>
        )
      ) : !loaded.info.available ? (
        <p className="ops-result error">{loaded.info.reason}</p>
      ) : (
        <>
          <section className="ops-card" aria-labelledby="mcp-heading">
            <h3 id="mcp-heading">MCP servers</h3>
            {loaded.info.servers.length === 0 ? (
              <p className="hint">
                No MCP server is configured. Add <code>mcp.&lt;name&gt;.*</code> settings to the AI Agent plugin (see plugins/MyRPA.Ai.Agent/README.md).
              </p>
            ) : (
              <ul className="mcp-servers">
                {loaded.info.servers.map((s) => (
                  <ServerRow key={s.name} server={s} result={results[s.name]} onTest={() => void discover(s.name)} />
                ))}
              </ul>
            )}
          </section>
          <section className="ops-card" aria-labelledby="builtins-heading">
            <h3 id="builtins-heading">Built-in tools</h3>
            <ul className="ops-list">
              {loaded.info.builtIns.map((b) => (
                <li key={b.name}>
                  <code>{b.name}</code> <span className="hint">{b.description}</span>
                </li>
              ))}
            </ul>
          </section>
        </>
      )}
    </main>
  );
}

// ───────────────────────────── The tools picker ─────────────────────────────

type ToolEntry = { readonly [key: string]: unknown };

/** The entries of a `tools` value, or why the picker cannot read it (then only the text box edits it). */
export function parseToolEntries(text: string): { entries: ToolEntry[] } | { error: string } {
  if (text.trim() === '') {
    return { entries: [] };
  }

  try {
    const value: unknown = JSON.parse(text);
    return Array.isArray(value) && value.every((e) => typeof e === 'object' && e !== null && !Array.isArray(e))
      ? { entries: value as ToolEntry[] }
      : { error: 'The tools are not a JSON array of entries; fix the text to use the picker.' };
  } catch {
    return { error: 'The tools are not valid JSON; fix the text to use the picker.' };
  }
}

const describeEntry = (e: ToolEntry): string =>
  typeof e.builtin === 'string'
    ? `Built-in ${e.builtin}`
    : typeof e.mcp === 'string'
      ? `MCP ${e.mcp}: ${e.tool === '*' ? 'every allowed tool' : String(e.tool ?? '?')}`
      : typeof e.workflow === 'string'
        ? `Workflow ${e.workflow} as ${String(e.name ?? '?')}`
        : 'Unknown entry';

const same = (a: ToolEntry, b: ToolEntry) => JSON.stringify(a) === JSON.stringify(b);

/** A tool name from a file name: letters, digits and _ (the AI Agent's rule). */
const toolName = (path: string): string => {
  const base = path.split('/').pop()!.replace(/\.json$/i, '');
  const name = base.replace(/[^A-Za-z0-9_]+/g, '_').replace(/^_+|_+$/g, '');
  return name === '' ? 'tool' : name;
};

/**
 * The picker under an AI.Agent's `tools` text (catalog editor hint `agentTools`): lists the entries with Remove, and
 * adds built-ins, MCP server tools and project workflows. It writes the same JSON the text box shows.
 */
export function AgentToolsEditor({ descriptor, text, disabled, onChange }: { descriptor: PropertyDescriptor; text: string; disabled: boolean; onChange: (text: string) => void }) {
  const studio = useStudio();
  const loaded = useAgentTools();
  const { results, discover } = useDiscovery();
  const files = useStudioState((s) => s.files);
  const current = useStudioState((s) => s.file?.path);
  const id = useId();
  const [source, setSource] = useState<'builtin' | 'mcp' | 'workflow'>('mcp');
  const [builtin, setBuiltin] = useState('');
  const [server, setServer] = useState('');
  const [tool, setTool] = useState('*');
  const [workflow, setWorkflow] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string>();
  const parsed = parseToolEntries(text);
  const info = loaded.info;

  // Workflow tools resolve relative to the agent's workflow and stay in its folder (like Invoke Workflow).
  const folder = current?.includes('/') ? current.slice(0, current.lastIndexOf('/') + 1) : '';
  const workflows = files
    .map((f) => f.path)
    .filter((p) => p !== current && p.startsWith(folder))
    .map((p) => p.slice(folder.length));
  const servers = info?.servers ?? [];
  const chosenServer = servers.find((s) => s.name === server && s.enabled) ?? servers.find((s) => s.enabled);
  const discovered = chosenServer ? results[chosenServer.name] : undefined;
  const serverTools =
    discovered !== undefined && discovered !== 'busy' && !('failed' in discovered) && discovered.ok
      ? discovered.tools.filter((t) => t.allowed && !t.requiresApproval).map((t) => t.name)
      : (chosenServer?.allowedTools ?? []).filter((t) => t !== '*' && !(chosenServer?.approvalTools ?? []).includes(t));

  if ('error' in parsed) {
    return (
      <p className="hint agent-tools-picker" role="note">
        {parsed.error}
      </p>
    );
  }

  const write = (entries: readonly ToolEntry[]) => onChange(entries.length === 0 ? '' : JSON.stringify(entries, null, 2));
  const add = async () => {
    setMessage(undefined);
    let entry: ToolEntry | undefined;
    if (source === 'builtin') {
      entry = builtin === '' ? undefined : { builtin };
    } else if (source === 'mcp') {
      entry = chosenServer === undefined ? undefined : { mcp: chosenServer.name, tool };
    } else if (workflow !== '') {
      setBusy(true);
      try {
        const input = await studio.workflowToolInput(folder + workflow);
        entry = { workflow, name: toolName(workflow), description: `Runs ${workflow}.`, input };
      } catch (error) {
        setMessage(`Could not read ${workflow}: ${(error as Error).message}`);
      } finally {
        setBusy(false);
      }
    }

    if (entry === undefined) {
      return;
    }

    const added = entry;
    if (parsed.entries.some((e) => same(e, added))) {
      setMessage(`${describeEntry(added)} is already listed.`);
      return;
    }

    write([...parsed.entries, added]);
    setMessage(`Added ${describeEntry(added)}.`);
  };

  const reason = disabled
    ? 'The workflow is read-only'
    : source === 'builtin' && builtin === ''
      ? 'Choose a built-in tool'
      : source === 'mcp' && chosenServer === undefined
        ? 'No enabled MCP server is configured'
        : source === 'workflow' && workflow === ''
          ? 'Choose a workflow'
          : undefined;
  return (
    <div className="agent-tools-picker" role="group" aria-label={`Pick tools for ${descriptor.name}`}>
      {parsed.entries.length === 0 ? (
        <p className="hint">No tool yet: the agent can only answer from the task.</p>
      ) : (
        <ul className="agent-tool-list" aria-label="Listed tools">
          {parsed.entries.map((e, i) => (
            <li key={i}>
              <span>{describeEntry(e)}</span>
              <button type="button" className="small" disabled={disabled} aria-label={`Remove ${describeEntry(e)}`} onClick={() => write(parsed.entries.filter((_, j) => j !== i))}>
                Remove
              </button>
            </li>
          ))}
        </ul>
      )}
      <div className="agent-tool-add">
        <label htmlFor={`${id}-source`}>Add</label>
        <select id={`${id}-source`} value={source} disabled={disabled} onChange={(e) => setSource(e.target.value as typeof source)}>
          <option value="mcp">MCP server tool</option>
          <option value="builtin">Built-in tool</option>
          <option value="workflow">Project workflow</option>
        </select>
        {source === 'builtin' && (
          <>
            <label htmlFor={`${id}-builtin`}>Tool</label>
            <select id={`${id}-builtin`} value={builtin} disabled={disabled} onChange={(e) => setBuiltin(e.target.value)}>
              <option value="">Choose…</option>
              {(info?.builtIns ?? []).map((b) => (
                <option key={b.name} value={b.name} title={b.description}>
                  {b.name}
                </option>
              ))}
            </select>
          </>
        )}
        {source === 'mcp' && (
          <>
            <label htmlFor={`${id}-server`}>Server</label>
            <select
              id={`${id}-server`}
              value={chosenServer?.name ?? ''}
              disabled={disabled || servers.length === 0}
              onChange={(e) => {
                setServer(e.target.value);
                setTool('*');
              }}
            >
              {servers.length === 0 && <option value="">{info === undefined ? (loaded.error ?? 'Loading…') : (info.reason ?? 'No server configured')}</option>}
              {servers.map((s) => (
                <option key={s.name} value={s.name} disabled={!s.enabled}>
                  {s.name}
                  {s.enabled ? '' : ' (disabled)'}
                </option>
              ))}
            </select>
            <label htmlFor={`${id}-tool`}>Tool</label>
            <select id={`${id}-tool`} value={tool} disabled={disabled || chosenServer === undefined} onChange={(e) => setTool(e.target.value)}>
              <option value="*">* (every allowed tool)</option>
              {serverTools.map((t) => (
                <option key={t} value={t}>
                  {t}
                </option>
              ))}
            </select>
            {chosenServer !== undefined && (
              <button type="button" className="small" disabled={discovered === 'busy'} title="Connects to the server and lists its tools" onClick={() => void discover(chosenServer.name)}>
                {discovered === 'busy' ? 'Loading…' : 'Load tools from server'}
              </button>
            )}
          </>
        )}
        {source === 'workflow' && (
          <>
            <label htmlFor={`${id}-workflow`}>Workflow</label>
            <select id={`${id}-workflow`} value={workflow} disabled={disabled} onChange={(e) => setWorkflow(e.target.value)}>
              <option value="">Choose…</option>
              {workflows.map((p) => (
                <option key={p} value={p}>
                  {p}
                </option>
              ))}
            </select>
          </>
        )}
        <button type="button" className="small primary" disabled={busy || reason !== undefined} title={reason} onClick={() => void add()}>
          Add tool
        </button>
      </div>
      {discovered !== undefined && discovered !== 'busy' && ('failed' in discovered || !discovered.ok) && <p className="ops-result error">{'failed' in discovered ? discovered.failed : discovered.error}</p>}
      {message && (
        <p className="hint" role="status">
          {message}
        </p>
      )}
    </div>
  );
}
