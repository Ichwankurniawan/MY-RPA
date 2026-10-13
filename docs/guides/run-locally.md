# Run Laconi Automation on one machine

Copy-paste steps for a local Server, the Studio and a Runner. Every command has a **PowerShell** and a **cmd.exe**
form: PowerShell reads environment variables as `$env:NAME`, cmd.exe as `%NAME%`. The prompt tells them apart:
`PS C:\…>` is PowerShell. Paths below assume the repository is in `C:\Projects\MYRPA`.

## 1. Build once

```text
cd C:\Projects\MYRPA
dotnet build MyRPA.sln
cd web\studio
npm ci
npm run build
```

`dotnet build` cannot replace files that a running Server or Runner has open ("The file is locked by: MyRPA.Server" or
"myrpa-runner"): stop them first (Ctrl+C in their window).

## 2. Plugins: one configuration file

A plugin configuration file lists the plugins and their settings. Relative `directory` and `fileRoot` values are
resolved against the file's own folder, so the file works from any folder. Example
(`C:\Laconi\plugins.json`, files confined to `C:\Laconi\files`):

```json
{ "pluginConfigVersion": "1.0", "plugins": [
  { "directory": "C:/Projects/MYRPA/plugins/MyRPA.Browser.Playwright/bin/Debug/net10.0", "settings": { "fileRoot": "files" } },
  { "directory": "C:/Projects/MYRPA/plugins/MyRPA.Files/bin/Debug/net10.0", "settings": { "fileRoot": "files" } },
  { "directory": "C:/Projects/MYRPA/plugins/MyRPA.Spreadsheet/bin/Debug/net10.0", "settings": { "fileRoot": "files" } } ] }
```

Create the `files` folder next to it. Use the same file for the Server and the Runner, so a workflow sees the same
plugins in the Studio and in a job. The browser plugin needs Chromium once:
`pwsh plugins/MyRPA.Browser.Playwright/bin/Debug/net10.0/playwright.ps1 install chromium`.

## 3. The Server (Studio and orchestrator)

PowerShell:

```powershell
cd C:\Projects\MYRPA
dotnet run --project src/MyRPA.Server -- --data "$env:LOCALAPPDATA\Laconi\Server" --plugin-config "C:\Laconi\plugins.json"
```

cmd.exe:

```bat
cd C:\Projects\MYRPA
dotnet run --project src/MyRPA.Server -- --data "%LOCALAPPDATA%\Laconi\Server" --plugin-config "C:\Laconi\plugins.json"
```

- It prints a **start link**: open it in the browser (it works once; a restart prints a new one).
- Projects come from `Documents\Laconi Projects` unless `--project` or `--projects-root` names others.
- `--data` turns on the orchestrator (Deployments, Monitoring, Robots, Admin).
- The Studio listens on port 5310 (`--port` changes it). A Runner on the same machine uses that port; `--runner-url`
  is only for Runners on other machines and needs a **different** port (and https with `--runner-cert`).

## 4. A Runner

1. In the Studio: **Admin → Create Runner**, copy the token (it is shown once).
2. Save it in a file, for example `C:\LaconiRunner\runner.token`, and create `C:\LaconiRunner\runner.json`:

   ```json
   { "serverUrl": "http://127.0.0.1:5310/", "tokenFile": "runner.token", "pluginConfig": "C:\\Laconi\\plugins.json" }
   ```

3. Check it, then start it:

   ```text
   cd C:\LaconiRunner
   dotnet run --project C:\Projects\MYRPA\src\MyRPA.Runner -- check --config runner.json
   dotnet run --project C:\Projects\MYRPA\src\MyRPA.Runner -- run --config runner.json
   ```

   `check` loads the plugins and contacts the Server without taking jobs. In the Studio, **Robots** shows the Runner as
   Available.

## 5. Publish and run

**Deployments** → a project → tick its entry points → **Publish** → **Deploy** the version → **Run now**, or add a
schedule (with its arguments). **Monitoring** shows the job, its events and outputs. Files a job writes stay on the
Runner, under its plugins' `fileRoot`.

## When something goes wrong

| Message | Cause and fix |
|---|---|
| `address already in use` | Another program (often a Server still running) uses the port: stop it, or use another `--port`. |
| `--runner-url uses port 5310, which is the Studio's port` | Leave out `--runner-url` for a Runner on this machine, or give it another port. |
| `The file is locked by: …` while building | Stop the Server or Runner named, then build again. |
| `file root … does not exist` | Create the folder `fileRoot` names (relative to the plugin configuration file). |
| Robots or Deployments: `reads myrpa-package/1, not myrpa-package/2: update this Runner` | The Runner is older than the Server: stop it, build, start it again. Its job waits instead of failing. |
| `MYRPA5001` (dependency missing) | The Runner lacks a plugin the package needs: add it to the Runner's plugin configuration. |
| `$env:…` or `%…%` appears in a path | The command was written for the other shell: use the matching form above. |
