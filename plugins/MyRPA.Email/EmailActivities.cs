using System.IO.Enumeration;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MimeKit;
using MimeKit.Utils;
using MyRPA.Core.Activities;
using MyRPA.Sdk.Files;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;
using static MyRPA.Email.Mail;

namespace MyRPA.Email;

/// <summary>
/// <c>Email.Send</c>: sends one message through the plugin's SMTP server (ADR-0043). TLS is required unless the operator
/// set the server's security to None; attachments come from the plugin's fileRoot.
/// </summary>
public sealed class EmailSendActivity(EmailOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Email.Send"),
        "Send Email",
        Category,
        "Sends an email through the email plugin's SMTP server: to, cc and bcc (an address, several separated by commas, or a List), a subject, a text or HTML body and attachments from the plugin's fileRoot. TLS is required unless the operator allowed a plain local relay. Fails with EmailRejected when the server refuses a sender or recipient.",
        [
            Input("to", ActivityValueType.Any, "The recipients: an address, several separated by commas, or a List.", required: true),
            Input("cc", ActivityValueType.Any, "Copy recipients."),
            Input("bcc", ActivityValueType.Any, "Blind copy recipients (not shown to the others)."),
            Input("from", ActivityValueType.String, "The sender (default: the plugin's defaultFrom)."),
            Input("subject", ActivityValueType.String, "The subject (one line).", required: true),
            Input("body", ActivityValueType.String, "The message text."),
            Input("html", ActivityValueType.Boolean, "The body is HTML.", defaultJson: "false"),
            Input("attachments", ActivityValueType.Any, "Files to attach: a path or a List of paths, relative to the plugin's fileRoot."),
            .. Credentials(),
            new("messageId", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the sent message's Message-ID.") { ValueType = ActivityValueType.String },
        ])
    { SideEffects = ActivitySideEffects.Network | ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = new MimeMessage();
        var from = OptionalText(context, "from") ?? options.DefaultFrom ?? throw Invalid(context, "from", "an address (the plugin has no defaultFrom)");
        var sender = Address(context, "from", from);
        message.From.Add(sender);
        AddAll(context, "to", message.To);
        AddAll(context, "cc", message.Cc);
        AddAll(context, "bcc", message.Bcc);
        if (message.To.Count + message.Cc.Count + message.Bcc.Count == 0)
        {
            throw Invalid(context, "to", "at least one address");
        }

        var subject = Text(context, "subject");
        if (subject.Contains('\r') || subject.Contains('\n'))
        {
            throw Invalid(context, "subject", "one line of text");
        }

        message.Subject = subject;
        var builder = new BodyBuilder();
        var body = OptionalText(context, "body") ?? string.Empty;
        if (Flag(context, "html", false))
        {
            builder.HtmlBody = body;
        }
        else
        {
            builder.TextBody = body;
        }

        var paths = Texts(context, "attachments");
        long size = body.Length;
        if (paths.Count > 0)
        {
            var files = options.RequireFiles("Email.Send");
            foreach (var path in paths)
            {
                var full = files.ResolveExistingFile(path);
                size += new FileInfo(full).Length;
                if (size > options.MaxMessageBytes)
                {
                    throw new ActivityFailedException(EmailErrorTypes.MessageTooLarge, $"The message with its attachments is larger than {options.MaxMessageBytes} bytes (setting maxMessageBytes).");
                }

                await builder.Attachments.AddAsync(full, context.CancellationToken).ConfigureAwait(false);
            }
        }

        message.Body = builder.ToMessageBody();
        message.MessageId = MimeUtils.GenerateMessageId(sender.Domain is { Length: > 0 } domain ? domain : "localhost");
        await TalkAsync(context, options, options.Smtp, "smtp", async cancellationToken =>
        {
            using var client = new SmtpClient();
            await ConnectAsync(client, options.Smtp, context, options.TimeoutMs, cancellationToken).ConfigureAwait(false);
            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

        Set(context, "messageId", message.MessageId);
        return ActivityResult.Completed;
    }

    private static MailboxAddress Address(IActivityContext context, string name, string text) =>
        MailboxAddress.TryParse(text.Trim(), out var address) && address.Address.Contains('@')
            ? address
            : throw Invalid(context, name, "email addresses such as ada@example.com");

    private static void AddAll(IActivityContext context, string name, InternetAddressList list)
    {
        foreach (var value in Texts(context, name))
        {
            foreach (var part in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                list.Add(Address(context, name, part));
            }
        }
    }
}

/// <summary>
/// <c>Email.Read</c>: messages of an IMAP folder matching filters (ADR-0043), oldest first, at most maxMessages. Reading
/// does not mark a message read; Email.MarkRead does.
/// </summary>
public sealed class EmailReadActivity(EmailOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Email.Read"),
        "Read Emails",
        Category,
        "Reads messages from a folder of the email plugin's IMAP server, oldest first: by default the unread ones. Each message is a Dictionary { id, from, fromAddress, to, cc, subject, date, text, html, attachments, size, tooLarge }; the id names it for Email.SaveAttachments, Email.MarkRead and Email.Move. Reading does not mark messages read.",
        [
            Input("folder", ActivityValueType.String, "The folder.", defaultJson: "\"INBOX\""),
            Input("unreadOnly", ActivityValueType.Boolean, "Only messages not yet read.", defaultJson: "true"),
            Input("fromContains", ActivityValueType.String, "Only messages whose sender contains this text."),
            Input("subjectContains", ActivityValueType.String, "Only messages whose subject contains this text."),
            Input("since", ActivityValueType.DateTime, "Only messages received on or after this date."),
            Input("maxMessages", ActivityValueType.Int, "The most messages to return (also capped by the plugin's maxMessages).", defaultJson: "10"),
            .. Credentials(),
            new("result", ActivityPropertyKind.AssignmentTarget, isRequired: true, "Receives the messages (a List of Dictionaries).") { ValueType = ActivityValueType.List },
        ])
    { SideEffects = ActivitySideEffects.Network };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var folderName = OptionalText(context, "folder") ?? "INBOX";
        var query = Query(context);
        var limit = !context.HasProperty("maxMessages") ? 10
            : context.Evaluate("maxMessages") is long n && n > 0 ? (int)Math.Min(n, int.MaxValue)
            : throw Invalid(context, "maxMessages", "a positive whole number");
        limit = Math.Min(limit, options.MaxMessages);

        var messages = await TalkAsync(context, options, options.Imap, "imap", async cancellationToken =>
        {
            using var client = new ImapClient();
            await ConnectAsync(client, options.Imap, context, options.TimeoutMs, cancellationToken).ConfigureAwait(false);
            var folder = await OpenFolderAsync(client, folderName, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            var uids = (await folder.SearchAsync(query, cancellationToken).ConfigureAwait(false)).OrderBy(u => u.Id).Take(limit).ToList();
            var sizes = uids.Count == 0
                ? []
                : (await folder.FetchAsync(uids, new FetchRequest(MessageSummaryItems.Size | MessageSummaryItems.UniqueId), cancellationToken).ConfigureAwait(false))
                    .ToDictionary(s => s.UniqueId, s => (long)(s.Size ?? 0));
            var result = new List<object?>();
            foreach (var uid in uids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = new MessageId(folderName, folder.UidValidity, uid.Id).ToString();
                var size = sizes.GetValueOrDefault(uid);
                if (size > options.MaxMessageBytes)
                {
                    result.Add(WorkflowValues.Dictionary([new("id", id), new("size", size), new("tooLarge", true)]));
                    continue;
                }

                var message = await folder.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
                result.Add(Describe(id, message, size));
            }

            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);

        context.SetValue(context.GetName("result"), WorkflowValues.List(messages));
        return ActivityResult.Completed;
    }

    private static SearchQuery Query(IActivityContext context)
    {
        SearchQuery query = Flag(context, "unreadOnly", true) ? SearchQuery.NotSeen : SearchQuery.All;
        if (OptionalText(context, "fromContains") is { Length: > 0 } from)
        {
            query = query.And(SearchQuery.FromContains(from));
        }

        if (OptionalText(context, "subjectContains") is { Length: > 0 } subject)
        {
            query = query.And(SearchQuery.SubjectContains(subject));
        }

        if (context.HasProperty("since") && context.Evaluate("since") is { } since)
        {
            // IMAP compares dates only: "after the day before" is "on or after the day".
            var day = (since as DateTimeOffset? ?? throw Invalid(context, "since", "a date and time")).UtcDateTime.Date;
            query = query.And(SearchQuery.DeliveredAfter(day.AddDays(-1)));
        }

        return query;
    }

    private static IReadOnlyDictionary<string, object?> Describe(string id, MimeMessage message, long size)
    {
        var sender = message.From.Mailboxes.FirstOrDefault();
        return WorkflowValues.Dictionary(
        [
            new("id", id),
            new("from", message.From.ToString()),
            new("fromAddress", sender?.Address),
            new("to", List(message.To.Mailboxes.Select(m => m.Address))),
            new("cc", List(message.Cc.Mailboxes.Select(m => m.Address))),
            new("subject", message.Subject ?? string.Empty),
            new("date", message.Date == DateTimeOffset.MinValue ? null : message.Date),
            new("text", message.TextBody),
            new("html", message.HtmlBody),
            new("attachments", List(message.Attachments.Select(AttachmentName))),
            new("size", size),
            new("tooLarge", false),
        ]);
    }

    /// <summary>An attachment's file name as saved: no folders, no characters a file name cannot hold.</summary>
    internal static string AttachmentName(MimeEntity attachment, int index)
    {
        var name = attachment is MimePart part ? part.FileName : (attachment as MessagePart)?.Message?.Subject + ".eml";
        name = Path.GetFileName((name ?? string.Empty).Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string([.. name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]).Trim().TrimEnd('.');
        return clean.Length == 0 ? $"attachment-{index + 1}" : clean;
    }
}

/// <summary><c>Email.SaveAttachments</c>: saves a message's attachments into a folder under the plugin's fileRoot (ADR-0043).</summary>
public sealed class EmailSaveAttachmentsActivity(EmailOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Email.SaveAttachments"),
        "Save Attachments",
        Category,
        "Saves the attachments of a message (by its id from Read Emails) whose names match a pattern such as *.pdf into a folder inside the email plugin's fileRoot. Names are cleaned (no folders); an existing file fails with FileAlreadyExists unless overwrite.",
        [
            Input("id", ActivityValueType.String, "The message id from Read Emails.", required: true),
            Input("folder", ActivityValueType.String, "The folder to save into, relative to the plugin's fileRoot (created when missing).", required: true),
            Input("pattern", ActivityValueType.String, "Only attachments whose name matches this pattern (* and ?), such as *.pdf.", defaultJson: "\"*\""),
            Input("overwrite", ActivityValueType.Boolean, "Replace files that exist.", defaultJson: "false"),
            .. Credentials(),
            new("result", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the saved files' paths (a List of String).") { ValueType = ActivityValueType.List },
        ])
    { SideEffects = ActivitySideEffects.Network | ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = MessageId.Parse(context, "id", context.Evaluate("id"));
        var files = options.RequireFiles("Email.SaveAttachments");
        var folderText = Text(context, "folder");
        var folderFull = files.Resolve(folderText);
        if (File.Exists(folderFull))
        {
            throw new ActivityFailedException(FileErrorTypes.InvalidPath, $"'{folderText}' is a file, not a folder.");
        }

        var pattern = OptionalText(context, "pattern") ?? "*";
        if (pattern.Length == 0 || pattern.Contains('/') || pattern.Contains('\\'))
        {
            throw Invalid(context, "pattern", "a name pattern such as *.pdf");
        }

        var overwrite = Flag(context, "overwrite", false);
        var message = await TalkAsync(context, options, options.Imap, "imap", async cancellationToken =>
        {
            using var client = new ImapClient();
            await ConnectAsync(client, options.Imap, context, options.TimeoutMs, cancellationToken).ConfigureAwait(false);
            var folder = await OpenFolderAsync(client, id.Folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            var uid = Uid(folder, id);
            var summary = (await folder.FetchAsync([uid], new FetchRequest(MessageSummaryItems.Size | MessageSummaryItems.UniqueId), cancellationToken).ConfigureAwait(false)).FirstOrDefault()
                ?? throw new ActivityFailedException(EmailErrorTypes.MessageNotFound, $"Message '{id}' is no longer in folder '{id.Folder}'.");
            if (summary.Size > options.MaxMessageBytes)
            {
                throw new ActivityFailedException(EmailErrorTypes.MessageTooLarge, $"Message '{id}' is larger than {options.MaxMessageBytes} bytes (setting maxMessageBytes).");
            }

            var mime = await folder.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
            return mime;
        }).ConfigureAwait(false);

        // Every target is checked before anything is written.
        var plan = new List<(MimeEntity Attachment, string Full)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var attachment in message.Attachments)
        {
            var name = EmailReadActivity.AttachmentName(attachment, index++);
            if (!FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
            {
                continue;
            }

            var unique = name;
            for (var n = 2; !names.Add(unique); n++)
            {
                unique = $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";
            }

            plan.Add((attachment, files.ResolveFileToWrite(Path.Combine(folderText, unique), overwrite)));
        }

        var saved = new List<object?>();
        foreach (var (attachment, full) in plan)
        {
            try
            {
                var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await using (stream.ConfigureAwait(false))
                {
                    if (attachment is MimePart { Content: { } content })
                    {
                        await content.DecodeToAsync(stream, context.CancellationToken).ConfigureAwait(false);
                    }
                    else if (attachment is MessagePart { Message: { } attached })
                    {
                        await attached.WriteToAsync(stream, context.CancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ActivityFailedException(FileErrorTypes.FileIoError, $"Cannot write '{files.Relative(full)}': it is in use, read-only or not accessible.", ex);
            }

            saved.Add(files.Relative(full));
        }

        Set(context, "result", WorkflowValues.List(saved));
        return ActivityResult.Completed;
    }
}

/// <summary><c>Email.MarkRead</c>: marks messages read or unread (ADR-0043).</summary>
public sealed class EmailMarkReadActivity(EmailOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Email.MarkRead"),
        "Mark Emails Read",
        Category,
        "Marks messages (by their ids from Read Emails) read, or unread with read false.",
        [
            Input("ids", ActivityValueType.Any, "A message id or a List of ids from Read Emails.", required: true),
            Input("read", ActivityValueType.Boolean, "Mark read (true) or unread (false).", defaultJson: "true"),
            .. Credentials(),
        ])
    { SideEffects = ActivitySideEffects.Network };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ids = MessageId.ParseMany(context, "ids");
        var read = Flag(context, "read", true);
        await TalkAsync(context, options, options.Imap, "imap", async cancellationToken =>
        {
            using var client = new ImapClient();
            await ConnectAsync(client, options.Imap, context, options.TimeoutMs, cancellationToken).ConfigureAwait(false);
            foreach (var group in ids.GroupBy(i => i.Folder, StringComparer.Ordinal))
            {
                var folder = await OpenFolderAsync(client, group.Key, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
                var request = new StoreFlagsRequest(read ? StoreAction.Add : StoreAction.Remove, MessageFlags.Seen) { Silent = true };
                await folder.StoreAsync([.. group.Select(i => Uid(folder, i))], request, cancellationToken).ConfigureAwait(false);
            }

            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

        return ActivityResult.Completed;
    }
}

/// <summary><c>Email.Move</c>: moves messages to another folder (ADR-0043).</summary>
public sealed class EmailMoveActivity(EmailOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Email.Move"),
        "Move Emails",
        Category,
        "Moves messages (by their ids from Read Emails) to another folder of the same account, for example Processed. Their old ids no longer name them.",
        [
            Input("ids", ActivityValueType.Any, "A message id or a List of ids from Read Emails.", required: true),
            Input("destination", ActivityValueType.String, "The folder to move to.", required: true),
            .. Credentials(),
        ])
    { SideEffects = ActivitySideEffects.Network };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ids = MessageId.ParseMany(context, "ids");
        var destinationName = Text(context, "destination");
        await TalkAsync(context, options, options.Imap, "imap", async cancellationToken =>
        {
            using var client = new ImapClient();
            await ConnectAsync(client, options.Imap, context, options.TimeoutMs, cancellationToken).ConfigureAwait(false);
            var destination = destinationName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : await client.GetFolderAsync(destinationName, cancellationToken).ConfigureAwait(false);
            foreach (var group in ids.GroupBy(i => i.Folder, StringComparer.Ordinal))
            {
                var folder = await OpenFolderAsync(client, group.Key, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
                await folder.MoveToAsync([.. group.Select(i => Uid(folder, i))], destination, cancellationToken).ConfigureAwait(false);
            }

            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

        return ActivityResult.Completed;
    }
}
