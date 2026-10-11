# MyRPA.Ai.OpenAiCompatible

The first AI provider of the Studio's **AI Workflow Assistant** ([ADR-0047](../../docs/adr/0047-ai-provider-and-studio-assistant.md),
[the Phase 8 plan](../../docs/architecture/ai-automation-plan.md)). It speaks the OpenAI chat-completions format, so it
works with OpenAI, Azure OpenAI and local servers such as Ollama or LM Studio.

It is a plugin with **no activities**: workflows never call it. It registers an `IAiProvider`
(`MyRPA.Ai.Contracts`), which the server's assistant uses when a person asks for help. It runs in-process and is fully
trusted (ADR-0015); its load context is not a sandbox. It uses only the .NET `HttpClient`: no vendor SDK.

## Settings (plugin configuration, ADR-0019)

| Setting | Default | Meaning |
|---|---|---|
| `endpoint` | required | The API base address: `https://api.openai.com/v1`, `https://<name>.openai.azure.com/openai/deployments/<deployment>`, `http://localhost:11434/v1` (Ollama). HTTPS, or HTTP only to this machine. Requests go to `<endpoint>/chat/completions`. |
| `model` | required | The model name, such as `gpt-4.1` or `llama3.1` |
| `apiKeyVariable` | `OPENAI_API_KEY` | The **environment variable** that holds the key. Empty for an endpoint without a key (local models). |
| `authHeader` | `bearer` | `bearer` (`Authorization: Bearer`) or `api-key` (Azure OpenAI) |
| `apiVersion` | none | An `api-version` query value (Azure OpenAI) |
| `structuredOutput` | `json_schema` | `json_schema` (the answer is constrained to the assistant's schema), `json_object`, or `none` (the schema goes into the instructions). The assistant validates every answer either way. |
| `maxTokensParameter` | `max_tokens` | `max_tokens` or `max_completion_tokens` (newer OpenAI models) |
| `timeoutMs` | 120000 | The time one attempt may take |
| `maxRetries` | 2 | Retries after a rate limit (429, honouring `Retry-After`, at most 60 s) or a server or network failure; never after a timeout, a refusal or bad credentials |
| `retryDelayMs` | 1000 | The wait before a retry when the endpoint does not say |
| `maxResponseBytes` | 4194304 | The largest answer read |

**The key is never a setting:** a setting named `apiKey`, `key`, `token`, `password` or `secret` is refused, so the key
cannot end up in a configuration file. Set the environment variable for the server, for example:

```powershell
$env:OPENAI_API_KEY = "..."   # then start MyRPA.Server with a plugin configuration that names this plugin
```

```json
{ "pluginConfigVersion": "1.0", "plugins": [
  { "directory": "plugins/MyRPA.Ai.OpenAiCompatible/bin/Debug/net10.0",
    "settings": { "endpoint": "https://api.openai.com/v1", "model": "gpt-4.1" } } ] }
```

A local model needs no key: `"settings": { "endpoint": "http://localhost:11434/v1", "model": "llama3.1", "apiKeyVariable": "" }`.

## Behaviour

- **Transport:** one endpoint; redirects are refused, not followed; TLS validation is never turned off; no cookies.
- **The key:** read when each request is made, sent only to the endpoint (`Authorization` or `api-key`), and never
  written to an error message or log.
- **Failures** map to `AiFailureKind`:
  - `Authentication` (401/403);
  - `RateLimited` (429 after retries);
  - `NotConfigured` (404, or the key variable not set);
  - `TooLarge` (413, or an answer over `maxResponseBytes`);
  - `UnsupportedStructuredOutput` (400 about `response_format`);
  - `Network` (5xx or unreachable after retries, or a redirect);
  - `Timeout`;
  - `InvalidResponse` (not JSON, empty, cut off at the output limit, or declined).
- **Cancellation** of the Studio's request cancels the HTTP request.

## Tests

`tests/MyRPA.Ai.OpenAiCompatible.Tests` loads the plugin through the real plugin host and talks to a local test server:
the request shape, the key handling, every failure kind, retries, timeouts, cancellation and the settings rules. No
test calls a real AI service.
