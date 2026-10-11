# ADR-0046: A projects folder, and creating and deleting projects from the Studio

- Status: **Accepted** (owner, 2026-10-11: a Projects page "with option project creation or deletion"; choices: a
  projects folder, delete moves to a trash folder, Projects as its own rail item).
- Amends: ADR-0022 (project structure), ADR-0025 (local server), ADR-0031 (project and file management in the Studio).

## Context

Until now a project was a folder named on the server's command line (`--project`). The Studio could only list those
folders: creating a project meant making a folder by hand and restarting the server. The owner asked for a Projects
page that creates and deletes projects.

The rule behind `--project` stays important: the browser must never choose where on disk the server writes. A local
page or extension that reaches the server must not be able to create or delete folders anywhere.

## Decision

1. **A projects folder.** `--projects-root <folder>` names a folder whose subfolders are projects (created if missing).
   - **No project named at all:** `MyRPA.Server` with no `--project`, no `--projects-root` and no `--open` uses
     `Documents/Laconi Projects`, so the server alone starts a usable Studio.
   - **`--project` stays as it is.** Those projects are listed first, then the projects folder's subfolders by name.
   - **Left out:** hidden folders (including `.trash`), links, `bin`/`obj`/`node_modules`, and a subfolder whose name a
     `--project` already uses (the `--project` wins).
   - **Read on each request:** a folder added by hand appears without a restart.
2. **Create.** `POST /api/projects` `{ name }` creates an empty folder in the projects folder only.
   - **The name is one safe folder name on every platform:** 1 to 100 characters, no leading or trailing spaces, not
     starting or ending with `.`, none of `< > : " / \ | ? *` or control characters, and no Windows device name.
   - **Responses:**
     - 201 when created;
     - 400 with the reason for a refused name;
     - 409 when the name is taken or the server has no projects folder.
3. **Delete moves to the trash.** `DELETE /api/projects/{name}` moves the project's folder to
   `<projects folder>/.trash/<name>-<yyyyMMdd-HHmmss>`, so it can be restored by hand. It returns that path.
   - **Never deleted from the Studio:** a `--project`, which is the operator's choice on the command line (409).
   - **A folder Windows has locked** gives a 409 with the reason.
   - **Nothing is erased:** emptying the trash is left to the operator.
4. **The Studio.**
   - **Its own rail item:** Projects sits between Home and Workflows, with no editing toolbar.
   - **New project…** is disabled, with the reason, when the server has no projects folder. After creating, the
     Studio opens the new project's workspace.
   - **Delete…** is offered only for projects of the projects folder. The project's name must be typed to confirm. An
     open workflow of that project is closed.
   - **Workflows** is the chosen project's workspace.
5. **Security is unchanged.** Both requests are state-changing and need the session, the Origin check and the
   anti-forgery header (ADR-0025). No path from the browser is ever used: only a single folder name, inside the folder
   the operator chose.

## Consequences

- A user can start the server with no arguments and work entirely from the Studio.
- Projects are still plain folders. Publishing (a versioned package of a project) is Phase 10/12 and not part of this.
- Restoring from the trash, renaming a project and moving a project between folders are manual for now.
