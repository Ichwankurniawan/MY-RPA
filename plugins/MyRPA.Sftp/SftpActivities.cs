using System.IO.Enumeration;
using System.Net.Sockets;
using MyRPA.Core.Activities;
using MyRPA.Sdk.Files;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MyRPA.Sftp;

/// <summary><c>Sftp.List</c>: the files and folders of a remote folder (ADR-0043).</summary>
public sealed class SftpListActivity(SftpOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Sftp.List"),
        "List Remote Files",
        Sftp.Category,
        "Lists a folder on an SFTP server named in the plugin configuration (inside its remoteRoot), sorted by name. Each entry is { name, path, size, modified, isFolder, isLink }.",
        [
            .. Sftp.Common(),
            Sftp.Input("folder", ActivityValueType.String, "The remote folder, relative to the server's remoteRoot.", defaultJson: "\".\""),
            Sftp.Input("pattern", ActivityValueType.String, "Only names matching this pattern (* and ?).", defaultJson: "\"*\""),
            new("result", ActivityPropertyKind.AssignmentTarget, isRequired: true, "Receives the entries (a List of Dictionaries).") { ValueType = ActivityValueType.List },
        ])
    { SideEffects = ActivitySideEffects.Network };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var pattern = Sftp.OptionalText(context, "pattern") ?? "*";
        var server = Sftp.ServerOf(context, options);
        var folder = Sftp.Remote(context, server, "folder", Sftp.OptionalText(context, "folder") ?? ".");
        var entries = await Sftp.TalkAsync(context, options, server, async (client, cancellationToken) =>
        {
            var result = new List<(string Name, IReadOnlyDictionary<string, object?> Entry)>();
            await foreach (var file in client.ListDirectoryAsync(folder, cancellationToken).ConfigureAwait(false))
            {
                if (file.Name is "." or ".." || !FileSystemName.MatchesSimpleExpression(pattern, file.Name, ignoreCase: false))
                {
                    continue;
                }

                if (result.Count == Sftp.MaxListEntries)
                {
                    throw new ActivityFailedException(FileErrorTypes.TooManyItems, $"The remote folder has more than {Sftp.MaxListEntries} matching entries; narrow the pattern.");
                }

                result.Add((file.Name, WorkflowValues.Dictionary(
                [
                    new("name", file.Name),
                    new("path", RemotePaths.Relative(server.RemoteRoot, file.FullName)),
                    new("size", file.IsDirectory ? null : file.Length),
                    new("modified", new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc))),
                    new("isFolder", file.IsDirectory),
                    new("isLink", file.IsSymbolicLink),
                ])));
            }

            return result.OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => (object?)e.Entry).ToList();
        }).ConfigureAwait(false);

        context.SetValue(context.GetName("result"), WorkflowValues.List(entries));
        return ActivityResult.Completed;
    }
}

/// <summary><c>Sftp.Download</c>: a remote file to a local file under the plugin's fileRoot (ADR-0043).</summary>
public sealed class SftpDownloadActivity(SftpOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Sftp.Download"),
        "Download from SFTP",
        Sftp.Category,
        "Downloads a remote file to a file inside the SFTP plugin's fileRoot. The local file appears only once complete; it is never replaced unless overwrite; files larger than maxFileBytes are refused.",
        [
            .. Sftp.Common(),
            Sftp.Input("remotePath", ActivityValueType.String, "The remote file, relative to the server's remoteRoot.", required: true),
            Sftp.Input("localPath", ActivityValueType.String, "The local file, relative to the plugin's fileRoot.", required: true),
            Sftp.Input("overwrite", ActivityValueType.Boolean, "Replace the local file when it exists.", defaultJson: "false"),
            new("result", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the local path (relative to the fileRoot).") { ValueType = ActivityValueType.String },
        ])
    { SideEffects = ActivitySideEffects.Network | ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var files = options.RequireFiles("Sftp.Download");
        var localText = Sftp.Text(context, "localPath");
        var overwrite = Sftp.Flag(context, "overwrite", false);
        var local = files.ResolveFileToWrite(localText, overwrite);
        var remoteText = Sftp.Text(context, "remotePath");
        var server = Sftp.ServerOf(context, options);
        var remote = Sftp.Remote(context, server, "remotePath", remoteText);
        var partial = Path.Combine(Path.GetDirectoryName(local)!, $".{Path.GetFileName(local)}.{Path.GetRandomFileName()}.download");
        try
        {
            await Sftp.TalkAsync(context, options, server, async (client, cancellationToken) =>
            {
                var attributes = await client.GetAttributesAsync(remote, cancellationToken).ConfigureAwait(false);
                if (!attributes.IsRegularFile)
                {
                    throw new ActivityFailedException(FileErrorTypes.InvalidPath, $"'{remoteText}' on server '{server.Name}' is not a file.");
                }

                if (attributes.Size > options.MaxFileBytes)
                {
                    throw new ActivityFailedException(FileErrorTypes.FileTooLarge, $"'{remoteText}' is larger than {options.MaxFileBytes} bytes (setting maxFileBytes).");
                }

                var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await using (stream.ConfigureAwait(false))
                {
                    await client.DownloadFileAsync(remote, new LimitedStream(stream, options.MaxFileBytes, remoteText), cancellationToken).ConfigureAwait(false);
                }

                return true;
            }).ConfigureAwait(false);

            if (!overwrite && File.Exists(local))
            {
                throw new ActivityFailedException(FileErrorTypes.FileAlreadyExists, $"The file '{localText}' exists; set 'overwrite' to replace it.");
            }

            File.Move(partial, local, overwrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ActivityFailedException(FileErrorTypes.FileIoError, $"Cannot write '{localText}': it is in use, read-only or not accessible.", ex);
        }
        finally
        {
            Sftp.TryDelete(partial);
        }

        Sftp.Set(context, "result", files.Relative(local));
        return ActivityResult.Completed;
    }
}

/// <summary><c>Sftp.Upload</c>: a local file under the plugin's fileRoot to the server (ADR-0043).</summary>
public sealed class SftpUploadActivity(SftpOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Sftp.Upload"),
        "Upload to SFTP",
        Sftp.Category,
        "Uploads a file from the SFTP plugin's fileRoot to a server named in its configuration (inside the server's remoteRoot). An existing remote file is replaced only with overwrite (RemoteFileExists otherwise).",
        [
            .. Sftp.Common(),
            Sftp.Input("localPath", ActivityValueType.String, "The local file, relative to the plugin's fileRoot.", required: true),
            Sftp.Input("remotePath", ActivityValueType.String, "The remote file, relative to the server's remoteRoot.", required: true),
            Sftp.Input("overwrite", ActivityValueType.Boolean, "Replace the remote file when it exists.", defaultJson: "false"),
            new("result", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the remote path (relative to the remoteRoot).") { ValueType = ActivityValueType.String },
        ])
    { SideEffects = ActivitySideEffects.Network | ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var files = options.RequireFiles("Sftp.Upload");
        var localText = Sftp.Text(context, "localPath");
        var local = files.ResolveExistingFile(localText);
        if (new FileInfo(local).Length > options.MaxFileBytes)
        {
            throw new ActivityFailedException(FileErrorTypes.FileTooLarge, $"'{localText}' is larger than {options.MaxFileBytes} bytes (setting maxFileBytes).");
        }

        var remoteText = Sftp.Text(context, "remotePath");
        var overwrite = Sftp.Flag(context, "overwrite", false);
        var server = Sftp.ServerOf(context, options);
        var remote = Sftp.Remote(context, server, "remotePath", remoteText);
        var relative = await Sftp.TalkAsync(context, options, server, async (client, cancellationToken) =>
        {
            if (!overwrite && await client.ExistsAsync(remote, cancellationToken).ConfigureAwait(false))
            {
                throw new ActivityFailedException(SftpErrorTypes.RemoteFileExists, $"'{remoteText}' exists on server '{server.Name}'; set 'overwrite' to replace it.");
            }

            var source = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using (source.ConfigureAwait(false))
            {
                // CreateNew: the server refuses to replace a file that appeared since the check.
                var target = await client.OpenAsync(remote, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, cancellationToken).ConfigureAwait(false);
                await using (target.ConfigureAwait(false))
                {
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }
            }

            return RemotePaths.Relative(server.RemoteRoot, remote);
        }).ConfigureAwait(false);

        Sftp.Set(context, "result", relative);
        return ActivityResult.Completed;
    }
}

/// <summary><c>Sftp.Delete</c>: deletes one remote file (never a folder) (ADR-0043).</summary>
public sealed class SftpDeleteActivity(SftpOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Sftp.Delete"),
        "Delete Remote File",
        Sftp.Category,
        "Deletes one file on an SFTP server (inside its remoteRoot). Never deletes folders. A missing file fails (RemoteFileNotFound) unless missingOk.",
        [
            .. Sftp.Common(),
            Sftp.Input("remotePath", ActivityValueType.String, "The remote file, relative to the server's remoteRoot.", required: true),
            Sftp.Input("missingOk", ActivityValueType.Boolean, "Succeed when the file does not exist.", defaultJson: "false"),
        ])
    { SideEffects = ActivitySideEffects.Network };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var remoteText = Sftp.Text(context, "remotePath");
        var missingOk = Sftp.Flag(context, "missingOk", false);
        var server = Sftp.ServerOf(context, options);
        var remote = Sftp.Remote(context, server, "remotePath", remoteText);
        await Sftp.TalkAsync(context, options, server, async (client, cancellationToken) =>
        {
            if (!await client.ExistsAsync(remote, cancellationToken).ConfigureAwait(false))
            {
                return missingOk ? true : throw new ActivityFailedException(SftpErrorTypes.RemoteFileNotFound, $"'{remoteText}' does not exist on server '{server.Name}'.");
            }

            if ((await client.GetAttributesAsync(remote, cancellationToken).ConfigureAwait(false)).IsDirectory)
            {
                throw new ActivityFailedException(FileErrorTypes.InvalidPath, $"'{remoteText}' is a folder; Sftp.Delete deletes files only.");
            }

            await client.DeleteFileAsync(remote, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

        return ActivityResult.Completed;
    }
}

/// <summary><c>Sftp.Move</c>: moves or renames a remote file (ADR-0043).</summary>
public sealed class SftpMoveActivity(SftpOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Sftp.Move"),
        "Move Remote File",
        Sftp.Category,
        "Moves or renames a file on an SFTP server, both paths inside its remoteRoot. An existing target is replaced only with overwrite (RemoteFileExists otherwise).",
        [
            .. Sftp.Common(),
            Sftp.Input("from", ActivityValueType.String, "The remote file, relative to the server's remoteRoot.", required: true),
            Sftp.Input("to", ActivityValueType.String, "Its new path, relative to the server's remoteRoot.", required: true),
            Sftp.Input("overwrite", ActivityValueType.Boolean, "Replace the target when it exists.", defaultJson: "false"),
        ])
    { SideEffects = ActivitySideEffects.Network };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var fromText = Sftp.Text(context, "from");
        var toText = Sftp.Text(context, "to");
        var overwrite = Sftp.Flag(context, "overwrite", false);
        var server = Sftp.ServerOf(context, options);
        var from = Sftp.Remote(context, server, "from", fromText);
        var to = Sftp.Remote(context, server, "to", toText);
        await Sftp.TalkAsync(context, options, server, async (client, cancellationToken) =>
        {
            if (await client.ExistsAsync(to, cancellationToken).ConfigureAwait(false))
            {
                if (!overwrite)
                {
                    throw new ActivityFailedException(SftpErrorTypes.RemoteFileExists, $"'{toText}' exists on server '{server.Name}'; set 'overwrite' to replace it.");
                }

                await client.DeleteFileAsync(to, cancellationToken).ConfigureAwait(false);
            }

            await client.RenameFileAsync(from, to, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

        return ActivityResult.Completed;
    }
}

/// <summary><c>Sftp.CreateFolder</c>: creates a remote folder and its missing parents (ADR-0043).</summary>
public sealed class SftpCreateFolderActivity(SftpOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Sftp.CreateFolder"),
        "Create Remote Folder",
        Sftp.Category,
        "Creates a folder (and any missing parents) on an SFTP server, inside its remoteRoot; an existing folder is fine.",
        [.. Sftp.Common(), Sftp.Input("folder", ActivityValueType.String, "The remote folder, relative to the server's remoteRoot.", required: true)])
    { SideEffects = ActivitySideEffects.Network };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var server = Sftp.ServerOf(context, options);
        var folder = Sftp.Remote(context, server, "folder", Sftp.Text(context, "folder"));
        await Sftp.TalkAsync(context, options, server, async (client, cancellationToken) =>
        {
            var path = string.Empty;
            foreach (var segment in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                path += "/" + segment;
                if (path.Length <= server.RemoteRoot.Length)
                {
                    continue;
                }

                if (!await client.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    await client.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
                }
                else if (!(await client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false)).IsDirectory)
                {
                    throw new ActivityFailedException(FileErrorTypes.InvalidPath, $"'{RemotePaths.Relative(server.RemoteRoot, path)}' is a file on server '{server.Name}', not a folder.");
                }
            }

            return true;
        }).ConfigureAwait(false);

        return ActivityResult.Completed;
    }
}

/// <summary>The errorType values of the SFTP activities (ADR-0043).</summary>
public static class SftpErrorTypes
{
    /// <summary>No server with that name in the plugin configuration.</summary>
    public const string ServerNotFound = "ServerNotFound";

    /// <summary>The server presented a key other than the pinned one.</summary>
    public const string HostKeyMismatch = "HostKeyMismatch";

    /// <summary>The server could not be reached, or the connection failed.</summary>
    public const string SftpConnection = "SftpConnection";

    /// <summary>The server refused the user, password or key (or the key could not be read).</summary>
    public const string SftpAuthentication = "SftpAuthentication";

    /// <summary>A remote path leaves the server's remoteRoot.</summary>
    public const string RemotePathDenied = "RemotePathDenied";

    /// <summary>The remote file or folder does not exist.</summary>
    public const string RemoteFileNotFound = "RemoteFileNotFound";

    /// <summary>The remote target exists and overwrite was not allowed.</summary>
    public const string RemoteFileExists = "RemoteFileExists";

    /// <summary>The server refused the operation (permissions).</summary>
    public const string RemotePermissionDenied = "RemotePermissionDenied";

    /// <summary>No answer within the timeout.</summary>
    public const string Timeout = "Timeout";
}

/// <summary>Connections, host key pinning, remote paths and failure classification shared by the SFTP activities.</summary>
internal static class Sftp
{
    public const string Category = "SFTP";

    public const string InvalidInput = "InvalidInput";

    public const int MaxListEntries = 10_000;

    public static ActivityPropertyDefinition Input(string name, ActivityValueType type, string description, bool required = false, string? defaultJson = null) =>
        new(name, ActivityPropertyKind.Expression, required, description) { ValueType = type, DefaultValue = defaultJson };

    /// <summary>server, username, password, passphrase.</summary>
    public static IEnumerable<ActivityPropertyDefinition> Common() =>
    [
        new("server", ActivityPropertyKind.Text, isRequired: true, "The name of a server in the SFTP plugin's configuration.") { ValueType = ActivityValueType.String },
        Input("username", ActivityValueType.String, "The user (default: the server's configured user)."),
        new("password", ActivityPropertyKind.Expression, isRequired: false, "The password: an argument or variable, never a value written here.") { ValueType = ActivityValueType.String, IsSecret = true },
        new("passphrase", ActivityPropertyKind.Expression, isRequired: false, "The passphrase of the server's configured private key: an argument or variable, never a value written here.") { ValueType = ActivityValueType.String, IsSecret = true },
    ];

    public static string Text(IActivityContext context, string name) =>
        context.Evaluate(name) is string text ? text : throw Invalid(context, name, "text");

    public static string? OptionalText(IActivityContext context, string name) =>
        !context.HasProperty(name) ? null : context.Evaluate(name) is { } value ? value as string ?? throw Invalid(context, name, "text") : null;

    public static bool Flag(IActivityContext context, string name, bool defaultValue) =>
        !context.HasProperty(name) ? defaultValue : context.Evaluate(name) is bool b ? b : throw Invalid(context, name, "true or false");

    public static void Set(IActivityContext context, string name, object? value)
    {
        if (context.HasProperty(name))
        {
            context.SetValue(context.GetName(name), value);
        }
    }

    public static ActivityFailedException Invalid(IActivityContext context, string name, string expected) =>
        new(InvalidInput, $"'{name}' of {context.Node.Type} '{context.Node.Id}' must be {expected}.");

    /// <summary>The server named by the <c>server</c> property.</summary>
    public static SftpServer ServerOf(IActivityContext context, SftpOptions options)
    {
        var name = context.GetText("server");
        if (options.Servers.TryGetValue(name, out var server))
        {
            return server;
        }

        var known = options.Servers.Count == 0 ? "none is configured" : "configured: " + string.Join(", ", options.Servers.Keys.Order(StringComparer.Ordinal));
        throw new ActivityFailedException(SftpErrorTypes.ServerNotFound, $"There is no SFTP server '{name}' ({known}).");
    }

    /// <summary>The full remote path of a property's value, inside the server's remoteRoot.</summary>
    public static string Remote(IActivityContext context, SftpServer server, string name, string path) =>
        path.Length > 0 && RemotePaths.Resolve(server.RemoteRoot, path) is { } full
            ? full
            : throw new ActivityFailedException(SftpErrorTypes.RemotePathDenied, $"'{name}' of {context.Node.Type} '{context.Node.Id}' leaves the remote folder the server '{server.Name}' allows (remoteRoot).");

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover partial file is hidden and named as one; the failure that matters is already reported.
        }
    }

    /// <summary>
    /// Connects to the named server (refusing any host key but the pinned one), logs in with the password or the
    /// configured key, and runs <paramref name="talk"/> within the plugin's timeout. The password, passphrase and file
    /// contents never appear in messages.
    /// </summary>
    public static async Task<T> TalkAsync<T>(IActivityContext context, SftpOptions options, SftpServer server, Func<SftpClient, CancellationToken, Task<T>> talk)
    {
        var name = server.Name;
        var user = OptionalText(context, "username") ?? server.Username
            ?? throw new ActivityFailedException(InvalidInput, $"{context.Node.Type} '{context.Node.Id}' needs a username (the server '{name}' has none configured).");
        var password = OptionalText(context, "password");
        var passphrase = OptionalText(context, "passphrase");
        var methods = new List<AuthenticationMethod>();
        if (server.PrivateKeyPath is not null)
        {
            try
            {
                methods.Add(new PrivateKeyAuthenticationMethod(user, passphrase is null ? new PrivateKeyFile(server.PrivateKeyPath) : new PrivateKeyFile(server.PrivateKeyPath, passphrase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SshException or InvalidOperationException or ArgumentException)
            {
                throw new ActivityFailedException(SftpErrorTypes.SftpAuthentication, $"The private key of server '{name}' could not be read (a missing file, or a wrong or missing passphrase).", ex);
            }
        }

        if (password is not null)
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));
        }

        if (methods.Count == 0)
        {
            throw new ActivityFailedException(InvalidInput, $"{context.Node.Type} '{context.Node.Id}' needs a password (the server '{name}' has no private key configured).");
        }

        var timeout = TimeSpan.FromMilliseconds(options.TimeoutMs);
        if (context.Deadline is { } deadline && deadline - context.TimeProvider.GetUtcNow() < timeout)
        {
            var remaining = deadline - context.TimeProvider.GetUtcNow();
            timeout = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
        }

        var milliseconds = ((long)timeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var info = new ConnectionInfo(server.Host, server.Port, user, [.. methods]) { Timeout = timeout };
        using var client = new SftpClient(info) { OperationTimeout = timeout };
        string? presented = null;
        client.HostKeyReceived += (_, e) =>
        {
            presented = "SHA256:" + e.FingerPrintSHA256;
            e.CanTrust = SameFingerprint(presented, server.HostKey);
        };

        using var timer = new CancellationTokenSource(timeout, context.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timer.Token);
        var where = $"SFTP server '{name}'";
        try
        {
            await client.ConnectAsync(linked.Token).ConfigureAwait(false);
            var result = await talk(client, linked.Token).ConfigureAwait(false);
            client.Disconnect();
            return result;
        }
        catch (SshConnectionException ex) when (presented is not null && !SameFingerprint(presented, server.HostKey))
        {
            throw new ActivityFailedException(SftpErrorTypes.HostKeyMismatch, $"The {where} presented the host key {presented}, not the pinned {server.HostKey}: the connection was refused. If the server's key really changed, update server.{name}.hostKey.", ex);
        }
        catch (OperationCanceledException ex) when (!context.CancellationToken.IsCancellationRequested)
        {
            throw new ActivityFailedException(SftpErrorTypes.Timeout, $"The {where} did not answer within {milliseconds} ms.", ex);
        }
        catch (SshOperationTimeoutException ex)
        {
            throw new ActivityFailedException(SftpErrorTypes.Timeout, $"The {where} did not answer within {milliseconds} ms.", ex);
        }
        catch (SshAuthenticationException ex)
        {
            throw new ActivityFailedException(SftpErrorTypes.SftpAuthentication, $"The {where} refused the user, password or key.", ex);
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new ActivityFailedException(SftpErrorTypes.RemoteFileNotFound, $"A remote path does not exist on the {where}.", ex);
        }
        catch (SftpPermissionDeniedException ex)
        {
            throw new ActivityFailedException(SftpErrorTypes.RemotePermissionDenied, $"The {where} refused the operation (permission denied).", ex);
        }
        catch (Exception ex) when (ex is SshException or SocketException)
        {
            throw new ActivityFailedException(SftpErrorTypes.SftpConnection, $"The conversation with the {where} failed: {Clean(ex.Message)}", ex);
        }
    }

    private static bool SameFingerprint(string a, string b) =>
        string.Equals(a.TrimEnd('='), b.TrimEnd('='), StringComparison.Ordinal);

    private static string Clean(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}

/// <summary>A write-only stream that fails with FileTooLarge past a limit (a remote size can change during a download).</summary>
internal sealed class LimitedStream(Stream inner, long limit, string name) : Stream
{
    private long _written;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _written;

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Count(count);
        inner.Write(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Count(buffer.Length);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private void Count(int count)
    {
        _written += count;
        if (_written > limit)
        {
            throw new ActivityFailedException(FileErrorTypes.FileTooLarge, $"'{name}' is larger than {limit} bytes (setting maxFileBytes).");
        }
    }
}
