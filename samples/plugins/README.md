# Sample plugin

`MyRPA.Samples.DemoPlugin` is a complete, deterministic plugin that shows the Automation SDK without touching a real
technology:

| Contribution | What it shows |
|---|---|
| `DemoPlugin` (`IPlugin`) | Manifest-named entry point, settings in `Initialize` (`echoPrefix`), explicit `Register` |
| `Demo.Echo` | An activity with an injected plugin-owned instance (`DemoOptions`) and correlated logging |
| `Demo.Text` provider (`IDemoTextProvider : IAutomationProvider, ISelectorResolver`) | A technology interface plus its implementation, registered with plugin lifetime |
| `Demo.GetField` | The provider pattern end to end: `Selector` → `ISelectorResolver` → `SelectorMatch` → `IAutomationElement`; a missing field fails with `errorType` `ElementNotFound` |

Run it:

```bash
dotnet build samples/plugins/MyRPA.Samples.DemoPlugin
```

```bash
dotnet run --project src/MyRPA.Cli -- --plugin samples/plugins/MyRPA.Samples.DemoPlugin/bin/Debug/net10.0 run samples/plugins/demo-plugin.json --arg customer=Grace
```

The build output directory is the plugin directory: `myrpa-plugin.json`, the DLL and its `.deps.json`, and nothing
from the host. Use `myrpa --plugin <dir> plugins` to see the SHA-256 digest you would pin.

Plugins run with full trust inside MyRPA: `AssemblyLoadContext` isolates loading, it is not a security boundary.
See [docs/architecture/plugin-system.md](../../docs/architecture/plugin-system.md).
