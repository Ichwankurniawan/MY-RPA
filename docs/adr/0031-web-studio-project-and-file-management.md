# ADR-0031: Web Studio project and file management (W6)

- Status: Accepted for W6
- Date: 2026-10-07
- Phase: Web Studio W6
- Builds on: [ADR-0022](0022-server-control-plane-and-project-structure.md) (server serves the Studio),
  [ADR-0025](0025-local-mode-security.md) (local security), [ADR-0028](0028-web-studio-first-slice.md) (W3 document
  model), [ADR-0030](0030-web-studio-execution-ux.md) (W5)

## Context
Up to W5 the Studio could open and save existing files only, discarded unsaved changes through `window.confirm`,
reported a save conflict without a way out, lost unsaved work when the tab closed, and needed `--web` plus a project
to start. ADR-0021 exit criteria 9 (projects and files) and 13 (single-command local start) ask for a file tree,
create/rename/delete/save-as, an in-app unsaved-changes prompt, crash recovery, the WPF startup options, and a
single-command start. The owner decided two questions before W6: rename is a new server endpoint, and the server
bundles the Studio.

## Decision
1. **Rename is a server operation.** `POST /api/projects/{project}/move` `{ from, to }` with `If-Match` renames or moves
   a file within its project under the store's write lock: atomic (`File.Move`, no overwrite), content and ETag
   unchanged, both paths confined like every other path (ADR-0025). 409 if the target exists, 412 on a stale ETag, 428
   without `If-Match`. Create + delete from the client was rejected: a failure between the two leaves two files.
2. **Create, Save as and Delete use the existing endpoints.** New and Save as are `PUT` with `If-None-Match: *` (never
   overwrite); Delete is `DELETE` with the ETag as last read. A new workflow is a valid v1.0 document with an empty
   root Sequence, its id derived from the file name; the server validates it like any other.
3. **The server bundles the Studio.** The server's build copies `web/studio/dist` (when it was built) to `wwwroot` next
   to the server, its default web root; `--web` still overrides. The .NET build never runs npm, so the CI build and
   test job is unchanged; without a built Studio the server starts without a UI and says so. The start link is still
   printed, not opened (starting a process is banned, ADR-0025).
4. **`--open <file>`** is the WPF Studio's file argument. The file must be inside a project; given alone, its folder
   becomes the project, so `MyRPA.Server --open flow.json` is a complete start. `/api/info` reports it as
   `open: { project, path }` (additive) and the Studio opens it after connecting.
5. **Prompts are the Studio's own dialogs** (native `<dialog>`, CSP-safe), never `window.confirm`: unsaved changes
   (Save / Discard / Cancel; Save that fails stays on the file), delete (says when unsaved changes would be lost), a
   path for New / Rename / Save as (the server's refusal is shown in the dialog), save conflict, recovery. The browser's
   own `beforeunload` prompt remains for leaving the page.
6. **A save conflict (412) is resolved by the user**: Reload from disk (discard), Overwrite with mine (save over the
   current disk version; re-create it if it was deleted), or Save mine as another file. Edits are kept until then.
7. **Crash recovery is local to the browser.** While a document has unsaved changes, it is written to `localStorage`
   (`myrpa.draft:<project>/<path>`: text, the ETag it started from, time) once typing pauses for a second. Saving,
   discarding, deleting or returning to the saved version removes it. Opening the file again offers it (Restore /
   Discard); a draft identical to the file on disk is dropped silently, and a draft whose file changed on disk since is
   marked as such. Restoring is one undoable edit on top of the file as opened. Storage failures are ignored: recovery
   is a convenience, never the only copy of a save. Drafts never leave the browser.

## Consequences
- ADR-0021 exit criterion 9 is covered: file tree; create, rename, delete, save-as; in-app unsaved prompt; crash
  recovery; the WPF startup option (a file to open). Criterion 13's single-command local start is covered; plugin
  load failures in the Studio and visible browser runs remain open.
- The Files panel shows the project's `.json` files as folders and files: click selects, double-click or Enter opens,
  F2 renames, Delete deletes; New…, Rename…, Delete… buttons; Save as… in the toolbar.
- Not in W6: folder operations (empty folders are not listed and not removed), moving files between projects,
  multi-select, a project switcher that creates projects, watching the disk for outside changes (a conflict is found
  when saving), and recovery across browsers or machines.
