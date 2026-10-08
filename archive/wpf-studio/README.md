# Archived: the WPF Studio (Phase 5, ADR-0018)

**Not built, not tested, not maintained.** Archived in W10 ([ADR-0036](../../docs/adr/0036-wpf-studio-archived.md)); the
MyRPA Studio is the Web Studio (`web/studio`, served by `MyRPA.Server`; see
[docs/architecture/web-studio.md](../../docs/architecture/web-studio.md)).

| Folder | Was |
|---|---|
| `src/MyRPA.Studio` | The WPF shell (`net10.0-windows`, composition root) |
| `src/MyRPA.Studio.Core` | Platform-neutral Studio logic: document model, edits, undo/redo, clipboard, validation mapping, view models |
| `tests/MyRPA.Studio.Tests` | WPF window smoke tests and code rules on the WPF assembly (Windows) |
| `tests/MyRPA.Studio.Core.Tests` | Studio logic, headless; `CorpusParityTests` wrote `tests/corpus/expected/locations.json` |
| `studio.md` | The WPF Studio documentation and its manual test script |

The project files here still point at `../MyRPA.Core` and other paths that only exist at their original locations, so
they do not build from this folder. To build or run the WPF Studio, use the tag where it was last in the solution:

```bash
git worktree add ../myrpa-wpf wpf-studio-final
cd ../myrpa-wpf
dotnet run --project src/MyRPA.Studio -- samples/control-flow.json   # Windows
```
