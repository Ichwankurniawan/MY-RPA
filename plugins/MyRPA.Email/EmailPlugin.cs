using System.Globalization;
using MailKit.Security;
using MyRPA.Sdk.Files;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Email;

/// <summary>
/// Entry point of the email plugin (Phase 7.1, ADR-0043). The servers are named only here, never by a workflow. Settings:
/// <list type="bullet">
/// <item><c>smtpHost</c>, <c>smtpPort</c> (587), <c>smtpSecurity</c> (StartTls; SslOnConnect; None only for a trusted local relay).</item>
/// <item><c>imapHost</c>, <c>imapPort</c> (993), <c>imapSecurity</c> (SslOnConnect; StartTls; None).</item>
/// <item><c>fileRoot</c> — the only folder tree attachments are read from and saved to (not set: attachments are refused).</item>
/// <item><c>defaultFrom</c> — the sender when Email.Send gives none.</item>
/// <item><c>maxMessageBytes</c> (25 MB), <c>maxMessages</c> (100), <c>timeoutMs</c> (60000).</item>
/// </list>
/// </summary>
public sealed class EmailPlugin : IPlugin
{
    private EmailOptions? _options;

    /// <inheritdoc />
    public void Initialize(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        string? Text(string name) => settings.TryGetValue(name, out var value) && value.Trim().Length > 0 ? value.Trim() : null;
        _options = new EmailOptions(
            new MailServer(Text("smtpHost"), (int)Number(settings, "smtpPort", 587, 1, 65535), Security(settings, "smtpSecurity", SecureSocketOptions.StartTls)),
            new MailServer(Text("imapHost"), (int)Number(settings, "imapPort", 993, 1, 65535), Security(settings, "imapSecurity", SecureSocketOptions.SslOnConnect)),
            Text("fileRoot") is { } root ? new FileRootPolicy(root) : null,
            Text("defaultFrom"),
            Number(settings, "maxMessageBytes", 25L * 1024 * 1024, 1, int.MaxValue),
            (int)Number(settings, "maxMessages", 100, 1, 10_000),
            (int)Number(settings, "timeoutMs", 60_000, 1, 3_600_000));
    }

    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar
            .AddInstance(_options ?? throw new InvalidOperationException("Initialize must run before Register."))
            .AddActivity<EmailSendActivity>(EmailSendActivity.Descriptor)
            .AddActivity<EmailReadActivity>(EmailReadActivity.Descriptor)
            .AddActivity<EmailSaveAttachmentsActivity>(EmailSaveAttachmentsActivity.Descriptor)
            .AddActivity<EmailMarkReadActivity>(EmailMarkReadActivity.Descriptor)
            .AddActivity<EmailMoveActivity>(EmailMoveActivity.Descriptor);
    }

    /// <summary>StartTls, SslOnConnect or None (plain text: only for a trusted local relay).</summary>
    private static SecureSocketOptions Security(IReadOnlyDictionary<string, string> settings, string name, SecureSocketOptions defaultValue) =>
        !settings.TryGetValue(name, out var text) ? defaultValue
        : text.Trim() switch
        {
            "StartTls" => SecureSocketOptions.StartTls,
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            "None" => SecureSocketOptions.None,
            _ => throw new ArgumentException($"Setting '{name}' must be StartTls, SslOnConnect or None, not '{text}'.", nameof(settings)),
        };

    private static long Number(IReadOnlyDictionary<string, string> settings, string name, long defaultValue, long min, long max)
    {
        if (!settings.TryGetValue(name, out var text))
        {
            return defaultValue;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new ArgumentException($"Setting '{name}' must be a whole number from {min} to {max}, not '{text}'.", nameof(settings));
    }
}
