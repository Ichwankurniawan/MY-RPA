using System.Globalization;
using MyRPA.Browser.Playwright.Activities;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Browser.Playwright;

/// <summary>
/// Entry point of the browser plugin (ADR-0017). Settings:
/// <list type="bullet">
/// <item><c>defaultTimeoutMilliseconds</c> — timeout of browser operations without their own (default 30000).</item>
/// <item><c>fileRoot</c> — directory tree for uploads and downloads (default: the host's working directory).</item>
/// <item><c>headless</c> — default for <c>Browser.Open</c> (default true).</item>
/// </list>
/// </summary>
public sealed class BrowserPlugin : IPlugin
{
    private BrowserPluginOptions? _options;

    /// <inheritdoc />
    public void Initialize(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;

        var timeout = TimeSpan.FromSeconds(30);
        if (settings.TryGetValue("defaultTimeoutMilliseconds", out var timeoutText))
        {
            timeout = int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms > 0
                ? TimeSpan.FromMilliseconds(ms)
                : throw new ArgumentException($"Setting 'defaultTimeoutMilliseconds' must be a positive integer, not '{timeoutText}'.", nameof(context));
        }

        var headless = true;
        if (settings.TryGetValue("headless", out var headlessText) && !bool.TryParse(headlessText, out headless))
        {
            throw new ArgumentException($"Setting 'headless' must be true or false, not '{headlessText}'.", nameof(context));
        }

        var root = settings.TryGetValue("fileRoot", out var rootText) ? rootText : Environment.CurrentDirectory;
        _options = new BrowserPluginOptions(timeout, headless, new BrowserFilePolicy(root));
    }

    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar
            .AddInstance(_options ?? throw new InvalidOperationException("Initialize must run before Register."))
            .AddProvider<IBrowserProvider, PlaywrightBrowserProvider>(PlaywrightBrowserProvider.Id)
            .AddService<BrowserSessions, BrowserSessions>(PluginServiceLifetime.Run)
            .AddActivity<OpenActivity>(OpenActivity.Descriptor)
            .AddActivity<NavigateActivity>(NavigateActivity.Descriptor)
            .AddActivity<ClickActivity>(ClickActivity.Descriptor)
            .AddActivity<TypeTextActivity>(TypeTextActivity.Descriptor)
            .AddActivity<GetTextActivity>(GetTextActivity.Descriptor)
            .AddActivity<GetAttributeActivity>(GetAttributeActivity.Descriptor)
            .AddActivity<WaitForElementActivity>(WaitForElementActivity.Descriptor)
            .AddActivity<SelectOptionActivity>(SelectOptionActivity.Descriptor)
            .AddActivity<UploadFileActivity>(UploadFileActivity.Descriptor)
            .AddActivity<DownloadFileActivity>(DownloadFileActivity.Descriptor)
            .AddActivity<CloseActivity>(CloseActivity.Descriptor);
    }
}

/// <summary>Plugin settings, built once in <see cref="BrowserPlugin.Initialize"/>.</summary>
/// <param name="DefaultTimeout">Timeout of operations without their own <c>timeoutMilliseconds</c>.</param>
/// <param name="Headless">Default for <c>Browser.Open</c>'s <c>headless</c>.</param>
/// <param name="Files">The upload/download file policy.</param>
public sealed record BrowserPluginOptions(TimeSpan DefaultTimeout, bool Headless, BrowserFilePolicy Files);
