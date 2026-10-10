using System.Globalization;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MyRPA.Core.Activities;
using MyRPA.Sdk.Files;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Email;

/// <summary>The errorType values of the email activities (ADR-0043).</summary>
public static class EmailErrorTypes
{
    /// <summary>The plugin has no smtpHost (Email.Send) or imapHost (the others).</summary>
    public const string HostNotConfigured = "HostNotConfigured";

    /// <summary>The server could not be reached, refused TLS, or the connection failed.</summary>
    public const string EmailConnection = "EmailConnection";

    /// <summary>The server refused the user name and password.</summary>
    public const string EmailAuthentication = "EmailAuthentication";

    /// <summary>The server refused the message, a sender or a recipient.</summary>
    public const string EmailRejected = "EmailRejected";

    /// <summary>The mail folder does not exist.</summary>
    public const string FolderNotFound = "FolderNotFound";

    /// <summary>The message id names no message (deleted, moved, or the folder was rebuilt).</summary>
    public const string MessageNotFound = "MessageNotFound";

    /// <summary>A message (or a message to send) is larger than maxMessageBytes.</summary>
    public const string MessageTooLarge = "MessageTooLarge";

    /// <summary>No answer within the timeout.</summary>
    public const string Timeout = "Timeout";

    /// <summary>A property has a value of the wrong kind.</summary>
    public const string InvalidInput = "InvalidInput";
}

/// <summary>One mail server from the plugin settings.</summary>
/// <param name="Host">Its host name, or null when not configured.</param>
/// <param name="Port">Its port.</param>
/// <param name="Security">How the connection is protected.</param>
public sealed record MailServer(string? Host, int Port, SecureSocketOptions Security);

/// <summary>Plugin settings, built once in <see cref="EmailPlugin.Initialize"/>.</summary>
/// <param name="Smtp">The server Email.Send uses.</param>
/// <param name="Imap">The server the reading activities use.</param>
/// <param name="Files">Where attachments are read from and saved to; null when fileRoot is not set.</param>
/// <param name="DefaultFrom">The sender when Email.Send gives none.</param>
/// <param name="MaxMessageBytes">The largest message sent or read.</param>
/// <param name="MaxMessages">The most messages one Email.Read returns.</param>
/// <param name="TimeoutMs">The timeout of one activity's mail conversation.</param>
public sealed record EmailOptions(MailServer Smtp, MailServer Imap, FileRootPolicy? Files, string? DefaultFrom, long MaxMessageBytes, int MaxMessages, int TimeoutMs)
{
    /// <summary>The file policy, or a FileAccessDenied failure when the plugin has no fileRoot.</summary>
    /// <param name="activity">The activity type, for the message.</param>
    /// <returns>The policy.</returns>
    public FileRootPolicy RequireFiles(string activity) =>
        Files ?? throw new ActivityFailedException(FileErrorTypes.FileAccessDenied, $"{activity} needs the email plugin's fileRoot setting (the only folder it may use for attachments); it is not set.");
}

/// <summary>A message's address in a folder: <c>folder/uidValidity/uid</c> (the folder may itself contain '/').</summary>
internal readonly record struct MessageId(string Folder, uint UidValidity, uint Uid)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Folder}/{UidValidity}/{Uid}");

    public static MessageId Parse(IActivityContext context, string name, object? value)
    {
        var text = value as string ?? throw Mail.Invalid(context, name, "a message id from Email.Read");
        var last = text.LastIndexOf('/');
        var middle = last > 0 ? text.LastIndexOf('/', last - 1) : -1;
        return middle > 0
            && uint.TryParse(text.AsSpan(middle + 1, last - middle - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var validity)
            && uint.TryParse(text.AsSpan(last + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var uid) && uid > 0
            ? new MessageId(text[..middle], validity, uid)
            : throw Mail.Invalid(context, name, "a message id from Email.Read");
    }

    /// <summary>One id (text) or a List of ids.</summary>
    public static List<MessageId> ParseMany(IActivityContext context, string name)
    {
        var value = context.Evaluate(name);
        return value is IReadOnlyList<object?> list
            ? [.. list.Select(v => Parse(context, name, v))]
            : [Parse(context, name, value)];
    }
}

/// <summary>Connections, timeouts and failure classification shared by the email activities.</summary>
internal static class Mail
{
    public const string Category = "Email";

    public static ActivityPropertyDefinition Input(string name, ActivityValueType type, string description, bool required = false, string? defaultJson = null) =>
        new(name, ActivityPropertyKind.Expression, required, description) { ValueType = type, DefaultValue = defaultJson };

    public static IEnumerable<ActivityPropertyDefinition> Credentials() =>
    [
        Input("username", ActivityValueType.String, "The account's user name (none: the server takes mail without logging in)."),
        new("password", ActivityPropertyKind.Expression, isRequired: false, "The account's password (or app password): an argument or variable, never a value written here.") { ValueType = ActivityValueType.String, IsSecret = true },
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
        new(EmailErrorTypes.InvalidInput, $"'{name}' of {context.Node.Type} '{context.Node.Id}' must be {expected}.");

    /// <summary>Text values of a property that is one text or a List of texts (empty when absent or null).</summary>
    public static List<string> Texts(IActivityContext context, string name)
    {
        if (!context.HasProperty(name))
        {
            return [];
        }

        return context.Evaluate(name) switch
        {
            null => [],
            string text => [text],
            IReadOnlyList<object?> list when list.All(v => v is string) => [.. list.Cast<string>()],
            _ => throw Invalid(context, name, "text or a List of texts"),
        };
    }

    /// <summary>
    /// Runs one mail conversation with the plugin's timeout (capped by the run's deadline) and classifies failures; the
    /// password never appears in a message.
    /// </summary>
    public static async Task<T> TalkAsync<T>(IActivityContext context, EmailOptions options, MailServer server, string what, Func<CancellationToken, Task<T>> talk)
    {
        if (server.Host is null)
        {
            throw new ActivityFailedException(EmailErrorTypes.HostNotConfigured, $"{context.Node.Type} needs the email plugin's {what}Host setting; it is not set.");
        }

        var timeout = TimeSpan.FromMilliseconds(options.TimeoutMs);
        if (context.Deadline is { } deadline && deadline - context.TimeProvider.GetUtcNow() < timeout)
        {
            var remaining = deadline - context.TimeProvider.GetUtcNow();
            timeout = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
        }

        using var timer = new CancellationTokenSource(timeout, context.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timer.Token);
        var where = $"{what.ToUpperInvariant()} server {server.Host}";
        try
        {
            return await talk(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!context.CancellationToken.IsCancellationRequested)
        {
            throw new ActivityFailedException(EmailErrorTypes.Timeout, $"The {where} did not answer within {options.TimeoutMs} ms.", ex);
        }
        catch (AuthenticationException ex)
        {
            throw new ActivityFailedException(EmailErrorTypes.EmailAuthentication, $"The {where} refused the user name or password.", ex);
        }
        catch (SslHandshakeException ex)
        {
            throw new ActivityFailedException(EmailErrorTypes.EmailConnection, $"The secure (TLS) connection to the {where} failed (certificate or protocol).", ex);
        }
        catch (NotSupportedException ex) when (ex.Message.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase))
        {
            throw new ActivityFailedException(EmailErrorTypes.EmailConnection, $"The {where} does not offer TLS (STARTTLS), which the plugin requires; set its security to None only for a trusted local relay.", ex);
        }
        catch (SmtpCommandException ex)
        {
            throw new ActivityFailedException(EmailErrorTypes.EmailRejected, $"The {where} refused the message ({(int)ex.StatusCode}): {Clean(ex.Message)}", ex);
        }
        catch (FolderNotFoundException ex)
        {
            throw new ActivityFailedException(EmailErrorTypes.FolderNotFound, $"The {where} has no folder '{ex.FolderName}'.", ex);
        }
        catch (MessageNotFoundException ex)
        {
            throw new ActivityFailedException(EmailErrorTypes.MessageNotFound, $"A message is no longer in its folder on the {where}.", ex);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ProtocolException or ServiceNotConnectedException or ServiceNotAuthenticatedException or ImapCommandException)
        {
            throw new ActivityFailedException(EmailErrorTypes.EmailConnection, $"The conversation with the {where} failed: {Clean(ex.Message)}", ex);
        }
    }

    /// <summary>A server's text as messages show it: one line, shortened.</summary>
    private static string Clean(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    /// <summary>Connects and, with a user name, logs in. Certificates are always validated by the platform.</summary>
    public static async Task ConnectAsync(MailService client, MailServer server, IActivityContext context, int timeoutMs, CancellationToken cancellationToken)
    {
        client.Timeout = timeoutMs;
        await client.ConnectAsync(server.Host!, server.Port, server.Security, cancellationToken).ConfigureAwait(false);
        var user = OptionalText(context, "username");
        if (user is { Length: > 0 })
        {
            var password = OptionalText(context, "password") ?? string.Empty;
            await client.AuthenticateAsync(user, password, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Opens a folder of an IMAP account.</summary>
    public static async Task<IMailFolder> OpenFolderAsync(ImapClient client, string name, FolderAccess access, CancellationToken cancellationToken)
    {
        var folder = name.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : await client.GetFolderAsync(name, cancellationToken).ConfigureAwait(false);
        await folder.OpenAsync(access, cancellationToken).ConfigureAwait(false);
        return folder;
    }

    /// <summary>The folder's unique id for a message id, checking that the folder was not rebuilt since it was read.</summary>
    public static UniqueId Uid(IMailFolder folder, MessageId id) =>
        folder.UidValidity == id.UidValidity
            ? new UniqueId(id.UidValidity, id.Uid)
            : throw new ActivityFailedException(EmailErrorTypes.MessageNotFound, $"The message ids of folder '{id.Folder}' changed (the folder was rebuilt); read the messages again.");

    public static IReadOnlyList<object?> List(IEnumerable<string> values) => WorkflowValues.List(values.Cast<object?>());
}
