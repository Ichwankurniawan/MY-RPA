# MyRPA.Sftp

SFTP activities (Phase 7.1, [ADR-0043](../../docs/adr/0043-more-enterprise-integrations.md)). A product plugin: it
references the Automation SDK and [SSH.NET](https://github.com/sshnet/SSH.NET) 2026.0.0 (MIT), which only this plugin
may reference. It runs in-process and is fully trusted (ADR-0015); its load context is not a sandbox.

## Settings (plugin configuration, ADR-0019)

Servers are named in the configuration; a workflow refers to a server by name only.

| Setting | Default | Meaning |
|---|---|---|
| `server.NAME.host`, `server.NAME.port` | —, 22 | The server |
| `server.NAME.hostKey` | **required** | The server's host key fingerprint as `ssh-keygen -lf` prints it (`SHA256:…`). Any other key is refused (`HostKeyMismatch`); a server without one is refused when the plugin loads |
| `server.NAME.username` | not set | The default user (an activity's `username` overrides it) |
| `server.NAME.privateKeyPath` | not set | A private key file for key login (its passphrase is the activity's secret `passphrase`) |
| `server.NAME.remoteRoot` | `/` | The only remote folder tree the activities may use (an absolute path) |
| `fileRoot` | not set | The only local folder tree downloads write to and uploads read from; not set, transfers are refused (`FileAccessDenied`) |
| `maxFileBytes` | 524288000 (500 MB) | The largest file transferred |
| `timeoutMs` | 60000 | The timeout of one activity's conversation (also capped by the run's deadline) |

To read the fingerprint: `ssh-keyscan -p 22 sftp.example.com | ssh-keygen -lf -`, then compare it with the one the
server's operator gives you.

```json
{ "directory": "plugins/MyRPA.Sftp/bin/Debug/net10.0",
  "settings": {
    "server.bank.host": "sftp.bank.example",
    "server.bank.hostKey": "SHA256:vF8NhUDyYLU+pvl8ApoySNwIsAAvyKSbRLUFAknZHJM",
    "server.bank.username": "acme-robot",
    "server.bank.remoteRoot": "/inbound",
    "fileRoot": "work" } }
```

## Activities

Every activity takes `server`, `username`, `password` (**secret**) and `passphrase` (**secret**, for the configured
key). Secrets must name an argument or variable (validation `MYRPA1066`).

| Type | Properties | Output `result` |
|---|---|---|
| `Sftp.List` | `folder` (default `.`), `pattern` | Sorted entries `{ name, path, size, modified, isFolder, isLink }` |
| `Sftp.Download` | `remotePath`, `localPath` (under `fileRoot`), `overwrite` | The local path; the file appears only once complete |
| `Sftp.Upload` | `localPath`, `remotePath`, `overwrite` | The remote path; an existing file is never replaced unless `overwrite` (the server refuses it even if it appears during the upload) |
| `Sftp.Delete` | `remotePath`, `missingOk` | — (files only, never folders) |
| `Sftp.Move` | `from`, `to`, `overwrite` | — |
| `Sftp.CreateFolder` | `folder` | — (with its missing parents; an existing folder is fine) |

Remote paths are relative to `remoteRoot` (or absolute inside it). A path that leaves it, for example through `..`, is
refused **before connecting** (`RemotePathDenied`). The server decides what a symbolic link on its side points to, so
give the robot an account whose home or chroot is its `remoteRoot`.

| errorType | When |
|---|---|
| `ServerNotFound` | No server with that name (the message lists the configured names) |
| `HostKeyMismatch` | The server presented another key; the message names it, so an operator can verify and update `hostKey` |
| `SftpConnection`, `Timeout` | Unreachable, or no answer within `timeoutMs` |
| `SftpAuthentication` | The user, password or key was refused, or the key could not be read (never naming the secret) |
| `RemotePathDenied`, `RemoteFileNotFound`, `RemoteFileExists`, `RemotePermissionDenied` | Remote paths |
| `FileAccessDenied`, `FileAlreadyExists`, `FileTooLarge`, `InvalidPath` | Local paths and limits |

## Tests

`tests/MyRPA.Sftp.Tests` checks the configuration, remote root confinement, local paths and missing credentials without
a server. Transfers, host key pinning and wrong passwords run against a real server when `MYRPA_TEST_SFTP` is set;
otherwise they are reported as skipped, never as passed. For example, with atmoz/sftp:

```bash
docker run -d -p 2222:22 atmoz/sftp:alpine robot:secret:1001::upload
ssh-keyscan -p 2222 127.0.0.1 | ssh-keygen -lf -      # the ED25519 line gives the fingerprint
export MYRPA_TEST_SFTP="host=127.0.0.1;port=2222;hostKey=SHA256:…;user=robot;password=secret;root=/upload"
```

In Git Bash on Windows, also set `MSYS2_ENV_CONV_EXCL=MYRPA_TEST_SFTP`, so the shell does not rewrite `/upload` as a
Windows path. CI runs them in the `Integration services` job.
