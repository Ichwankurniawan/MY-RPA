import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { App } from './App';
import { parseToolEntries } from './AgentTools';
import { serialize } from './document';
import { Studio } from './studio';
import { catalog, FakeApi, FakeEventSource, immediately, settle } from './test-support';
import type { ActivityDescriptor, JsonObject } from './types';

// Agent tools (ADR-0048 amendment): the AI Agent plugin's MCP servers and built-ins, and the tools picker, all driven by
// the server (the catalog's editor hint and /api/agent-tools); the Studio has no AI.Agent knowledge of its own.

const agent: ActivityDescriptor = {
  type: 'AI.Agent',
  displayName: 'AI Agent',
  category: 'AI',
  description: 'Works on one task with an AI model.',
  allowsChildren: false,
  properties: [
    { name: 'instructions', kind: 'Text', required: true, allowedValues: [], scopeSlots: [], valueType: 'String', multiline: true },
    { name: 'tools', kind: 'Text', required: false, allowedValues: [], scopeSlots: [], valueType: 'String', multiline: true, editor: 'agentTools' },
  ],
  slots: [],
};

const lookup: JsonObject = { schemaVersion: '1.0', id: 'lookup', name: 'Lookup', version: '1', arguments: [{ name: 'customer', direction: 'In', type: 'String', required: true }, { name: 'limit', direction: 'In', type: 'Int', default: 5 }, { name: 'found', direction: 'Out', type: 'Boolean' }], root: { id: 'main', type: 'Core.Sequence', children: [] } };

async function renderStudio(loaded = true) {
  const api = new FakeApi();
  api.agentPluginLoaded = loaded;
  api.activities = async () => [...catalog, agent];
  api.files.set('agent.json', {
    text: serialize({ schemaVersion: '1.0', id: 'agent', name: 'Agent', version: '1', root: { id: 'main', type: 'Core.Sequence', children: [{ id: 'ask', type: 'AI.Agent', properties: { instructions: 'Help.' } }] } }),
    etag: 1,
  });
  api.files.set('lookup.json', { text: serialize(lookup), etag: 1 });
  const studio = new Studio(api, (url) => new FakeEventSource(url), immediately, { validateDelayMs: undefined });
  render(<App studio={studio} />);
  await act(settle);
  return { studio, api };
}

const tools = (studio: Studio) => (studio.store.get().document!.root as { children: { properties: Record<string, unknown> }[] }).children[0].properties.tools;

async function selectAgent(studio: Studio) {
  await act(async () => {
    await studio.open('agent.json');
  });
  await act(async () => fireEvent.click(document.querySelector('[role="treeitem"][data-node-id="ask"] > .node')!));
  await act(settle);
  return within(screen.getByRole('group', { name: 'Pick tools for tools' }));
}

afterEach(cleanup);

describe('Agent tools page', () => {
  it('is in the rail only when the AI Agent plugin is loaded', async () => {
    await renderStudio(false);
    expect(within(screen.getByRole('navigation', { name: 'Main' })).queryByRole('button', { name: 'Agent tools' })).toBeNull();
  });

  it('lists the configured MCP servers without tokens, the built-ins, and tests a connection', async () => {
    const { api } = await renderStudio();
    await act(async () => fireEvent.click(within(screen.getByRole('navigation', { name: 'Main' })).getByRole('button', { name: 'Agent tools' })));
    await act(settle);
    const page = within(screen.getByRole('main', { name: 'Agent tools' }));

    const crm = within(page.getByRole('listitem', { name: 'MCP server crm' }));
    expect(crm.getByText('https://mcp.example.com/mcp')).toBeTruthy();
    expect(crm.getByText('get_customer, search_customers')).toBeTruthy();
    expect(crm.getByText('update_customer')).toBeTruthy();
    expect(crm.getByText(/not set for the Server/)).toBeTruthy();
    expect((within(page.getByRole('listitem', { name: 'MCP server old' })).getByRole('button', { name: 'Test connection' }) as HTMLButtonElement).disabled).toBe(true);
    expect(page.getByText('regex_extract')).toBeTruthy();
    expect(screen.queryByRole('toolbar')).toBeNull();

    const files = within(page.getByRole('listitem', { name: 'MCP server files' }));
    await act(async () => fireEvent.click(files.getByRole('button', { name: 'Test connection' })));
    await act(settle);
    expect(api.discovered).toEqual(['files']);
    expect(files.getByText('Connected in 412 ms: 2 tool(s).')).toBeTruthy();
    expect(files.getByText('read_text_file')).toBeTruthy();

    await act(async () => fireEvent.click(crm.getByRole('button', { name: 'Test connection' })));
    await act(settle);
    expect(crm.getByText(/needs a token: set the environment variable CRM_MCP_TOKEN/)).toBeTruthy();
  });
});

describe('Agent tools picker', () => {
  it('adds an MCP server tool, a built-in and a project workflow with its inputs, and removes one, writing the tools JSON', async () => {
    const { studio, api } = await renderStudio();
    const picker = await selectAgent(studio);
    expect(picker.getByText('No tool yet: the agent can only answer from the task.')).toBeTruthy();

    // MCP: the server's tools come from the configuration, or from the server after "Load tools from server".
    await act(async () => fireEvent.change(picker.getByLabelText('Server'), { target: { value: 'files' } }));
    await act(async () => fireEvent.click(picker.getByRole('button', { name: 'Load tools from server' })));
    await act(settle);
    expect(api.discovered).toEqual(['files']);
    await act(async () => fireEvent.change(picker.getByLabelText('Tool'), { target: { value: 'read_text_file' } }));
    await act(async () => fireEvent.click(picker.getByRole('button', { name: 'Add tool' })));
    expect(JSON.parse(tools(studio) as string)).toEqual([{ mcp: 'files', tool: 'read_text_file' }]);
    expect((picker.getByRole('option', { name: 'old (disabled)' }) as HTMLOptionElement).disabled).toBe(true);

    await act(async () => fireEvent.change(picker.getByLabelText('Add'), { target: { value: 'builtin' } }));
    await act(async () => fireEvent.change(picker.getByLabelText('Tool'), { target: { value: 'regex_extract' } }));
    await act(async () => fireEvent.click(picker.getByRole('button', { name: 'Add tool' })));

    await act(async () => fireEvent.change(picker.getByLabelText('Add'), { target: { value: 'workflow' } }));
    await act(async () => fireEvent.change(picker.getByLabelText('Workflow'), { target: { value: 'lookup.json' } }));
    await act(async () => fireEvent.click(picker.getByRole('button', { name: 'Add tool' })));
    await act(settle);

    expect(JSON.parse(tools(studio) as string)).toEqual([
      { mcp: 'files', tool: 'read_text_file' },
      { builtin: 'regex_extract' },
      { workflow: 'lookup.json', name: 'lookup', description: 'Runs lookup.json.', input: { customer: 'String', limit: 'Int?' } },
    ]);

    await act(async () => fireEvent.click(picker.getByRole('button', { name: 'Remove Built-in regex_extract' })));
    expect(JSON.parse(tools(studio) as string)).toHaveLength(2);
    await act(async () => fireEvent.click(picker.getByRole('button', { name: 'Remove MCP files: read_text_file' })));
    await act(async () => fireEvent.click(picker.getByRole('button', { name: 'Remove Workflow lookup.json as lookup' })));
    expect(tools(studio)).toBeUndefined();
  });

  it('does not add the same entry twice', async () => {
    const { studio } = await renderStudio();
    const picker = await selectAgent(studio);

    await act(async () => fireEvent.click(picker.getByRole('button', { name: 'Add tool' })));
    await act(async () => fireEvent.click(picker.getByRole('button', { name: 'Add tool' })));

    expect(JSON.parse(tools(studio) as string)).toEqual([{ mcp: 'crm', tool: '*' }]);
    expect(picker.getByRole('status').textContent).toBe('MCP crm: every allowed tool is already listed.');
  });

  it('leaves text it cannot read to the text box', () => {
    expect(parseToolEntries('')).toEqual({ entries: [] });
    expect(parseToolEntries('[{"builtin":"time_now"}]')).toEqual({ entries: [{ builtin: 'time_now' }] });
    expect(parseToolEntries('{"builtin":"time_now"}')).toHaveProperty('error');
    expect(parseToolEntries('[1]')).toHaveProperty('error');
    expect(parseToolEntries('[{')).toHaveProperty('error');
  });
});
