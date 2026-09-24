using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;
using static MyRPA.Browser.Playwright.Activities.BrowserActivitySupport;

namespace MyRPA.Browser.Playwright.Activities;

/// <summary><c>Browser.Open</c>: launches a browser session for this run; optionally navigates and returns the session id.</summary>
/// <param name="sessions">The run's browser sessions.</param>
/// <param name="options">Plugin settings.</param>
public sealed class OpenActivity(BrowserSessions sessions, BrowserPluginOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        Name("Open"), "Open Browser", Category, "Launches a browser (own process, context and page) that is closed when the run ends.",
        [
            new("browser", ActivityPropertyKind.Text, isRequired: false, "Browser engine (default chromium).", allowedValues: [BrowserLaunchOptions.Chromium]),
            new("headless", ActivityPropertyKind.Expression, isRequired: false, "Boolean; defaults to the plugin's headless setting (true)."),
            new("url", ActivityPropertyKind.Expression, isRequired: false, "URL to open after launching."),
            new("to", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the session id (String)."),
            TimeoutProperty,
        ]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limits = Limits(context, options);
        var launch = new BrowserLaunchOptions(
            context.GetTextOrDefault("browser", BrowserLaunchOptions.Chromium),
            context.HasProperty("headless") ? context.EvaluateBoolean("headless") : options.Headless,
            options.DefaultTimeout);
        var url = context.HasProperty("url") ? NavigateActivity.ParseUrl(context.EvaluateText("url")) : null;

        var session = await sessions.OpenAsync(launch, limits).ConfigureAwait(false);
        if (url is not null)
        {
            await session.NavigateAsync(url, BrowserWaitUntil.Load, limits).ConfigureAwait(false);
        }

        if (context.HasProperty("to"))
        {
            context.SetValue(context.GetName("to"), session.Info.Id);
        }

        return ActivityResult.Completed;
    }
}

/// <summary><c>Browser.Navigate</c>: navigates the session's page.</summary>
/// <param name="sessions">The run's browser sessions.</param>
/// <param name="options">Plugin settings.</param>
public sealed class NavigateActivity(BrowserSessions sessions, BrowserPluginOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        Name("Navigate"), "Navigate", Category, "Navigates to an http(s) URL; HTTP errors (400+) and network errors fail the node.",
        [
            new("url", ActivityPropertyKind.Expression, isRequired: true, "Absolute http, https or about:blank URL."),
            new("waitUntil", ActivityPropertyKind.Text, isRequired: false, "load (default), domcontentloaded, networkidle or commit.",
                allowedValues: ["load", "domcontentloaded", "networkidle", "commit"]),
            SessionProperty,
            TimeoutProperty,
        ]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limits = Limits(context, options);
        var url = ParseUrl(context.EvaluateText("url"));
        var waitUntil = context.GetTextOrDefault("waitUntil", "load") switch
        {
            "domcontentloaded" => BrowserWaitUntil.DomContentLoaded,
            "networkidle" => BrowserWaitUntil.NetworkIdle,
            "commit" => BrowserWaitUntil.Commit,
            _ => BrowserWaitUntil.Load,
        };

        await Session(context, sessions).NavigateAsync(url, waitUntil, limits).ConfigureAwait(false);
        return ActivityResult.Completed;
    }

    /// <summary>Accepts absolute http and https URLs and about:blank; file: and other schemes are refused.</summary>
    /// <param name="text">URL text.</param>
    internal static Uri ParseUrl(string text)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var url)
            && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps || string.Equals(text, "about:blank", StringComparison.Ordinal)))
        {
            return url;
        }

        throw new ActivityFailedException(BrowserErrorTypes.InvalidUrl, $"'{text}' is not an absolute http, https or about:blank URL.");
    }
}

/// <summary><c>Browser.Click</c>: clicks the element (waits until it is visible, enabled and stable).</summary>
/// <param name="sessions">The run's browser sessions.</param>
/// <param name="options">Plugin settings.</param>
public sealed class ClickActivity(BrowserSessions sessions, BrowserPluginOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        Name("Click"), "Click", Category, "Clicks the single element matching the selector.", [SelectorProperty, SessionProperty, TimeoutProperty]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limits = Limits(context, options);
        var element = await Session(context, sessions).LocateAsync(Selector(context), limits).ConfigureAwait(false);
        await element.ClickAsync(limits.CancellationToken).ConfigureAwait(false);
        return ActivityResult.Completed;
    }
}

/// <summary><c>Browser.TypeText</c>: replaces (or appends to) the element's text.</summary>
/// <param name="sessions">The run's browser sessions.</param>
/// <param name="options">Plugin settings.</param>
public sealed class TypeTextActivity(BrowserSessions sessions, BrowserPluginOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        Name("TypeText"), "Type Text", Category, "Types text into an input, textarea or editable element.",
        [
            SelectorProperty,
            new("text", ActivityPropertyKind.Expression, isRequired: true, "Text to type."),
            new("clear", ActivityPropertyKind.Expression, isRequired: false, "Boolean: replace the current content (default true) or type after it."),
            SessionProperty,
            TimeoutProperty,
        ]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limits = Limits(context, options);
        var text = WorkflowValues.ToDisplayString(context.Evaluate("text"));
        var clear = !context.HasProperty("clear") || context.EvaluateBoolean("clear");
        var element = await Session(context, sessions).LocateAsync(Selector(context), limits).ConfigureAwait(false);
        if (clear)
        {
            await element.TypeTextAsync(text, limits.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            await element.AppendTextAsync(text, limits.CancellationToken).ConfigureAwait(false);
        }

        return ActivityResult.Completed;
    }
}

/// <summary><c>Browser.GetText</c>: reads the element's rendered text.</summary>
/// <param name="sessions">The run's browser sessions.</param>
/// <param name="options">Plugin settings.</param>
public sealed class GetTextActivity(BrowserSessions sessions, BrowserPluginOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        Name("GetText"), "Get Text", Category, "Reads the rendered (inner) text of an element.",
        [SelectorProperty, new("to", ActivityPropertyKind.AssignmentTarget, isRequired: true, "Receives the text (String)."), SessionProperty, TimeoutProperty]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limits = Limits(context, options);
        var element = await Session(context, sessions).LocateAsync(Selector(context), limits).ConfigureAwait(false);
        context.SetValue(context.GetName("to"), await element.GetTextAsync(limits.CancellationToken).ConfigureAwait(false));
        return ActivityResult.Completed;
    }
}

/// <summary><c>Browser.GetAttribute</c>: reads an attribute (null when absent).</summary>
/// <param name="sessions">The run's browser sessions.</param>
/// <param name="options">Plugin settings.</param>
public sealed class GetAttributeActivity(BrowserSessions sessions, BrowserPluginOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        Name("GetAttribute"), "Get Attribute", Category, "Reads an attribute of an element; null when the attribute is absent.",
        [
            SelectorProperty,
            new("name", ActivityPropertyKind.Text, isRequired: true, "Attribute name, e.g. href."),
            new("to", ActivityPropertyKind.AssignmentTarget, isRequired: true, "Receives the value (String or null)."),
            SessionProperty,
            TimeoutProperty,
        ]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limits = Limits(context, options);
        var element = await Session(context, sessions).LocateAsync(Selector(context), limits).ConfigureAwait(false);
        context.SetValue(context.GetName("to"), await element.GetAttributeAsync(context.GetText("name"), limits.CancellationToken).ConfigureAwait(false));
        return ActivityResult.Completed;
    }
}

/// <summary><c>Browser.WaitForElement</c>: waits until the element is attached, visible, hidden or detached.</summary>
/// <param name="sessions">The run's browser sessions.</param>
/// <param name="options">Plugin settings.</param>
public sealed class WaitForElementActivity(BrowserSessions sessions, BrowserPluginOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        Name("WaitForElement"), "Wait For Element", Category, "Waits until the element reaches a state; fails when the timeout elapses.",
        [
            SelectorProperty,
            new("state", ActivityPropertyKind.Text, isRequired: false, "visible (default), attached, hidden or detached.",
                allowedValues: ["visible", "attached", "hidden", "detached"]),
            SessionProperty,
            TimeoutProperty,
        ]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limits = Limits(context, options);
        var state = context.GetTextOrDefault("state", "visible") switch
        {
            "attached" => BrowserElementState.Attached,
            "hidden" => BrowserElementState.Hidden,
            "detached" => BrowserElementState.Detached,
            _ => BrowserElementState.Visible,
        };

        await Session(context, sessions).WaitForAsync(Selector(context), state, limits).ConfigureAwait(false);
        return ActivityResult.Completed;
    }
}

/// <summary><c>Browser.SelectOption</c>: selects options of a select element by value or label.</summary>
/// <param name="sessions">The run's browser sessions.</param>
/// <param name="options">Plugin settings.</param>
public sealed class SelectOptionActivity(BrowserSessions sessions, BrowserPluginOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        Name("SelectOption"), "Select Option", Category, "Selects one or more options (by value or label) of a select element.",
        [
            SelectorProperty,
            new("value", ActivityPropertyKind.Expression, isRequired: true, "Option value or label (String), or a List of them."),
            new("to", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the selected values (List)."),
            SessionProperty,
            TimeoutProperty,
        ]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limits = Limits(context, options);
        var values = Strings(context, "value");
        var element = await Session(context, sessions).LocateAsync(Selector(context), limits).ConfigureAwait(false);
        var selected = await element.SelectOptionsAsync(values, limits.CancellationToken).ConfigureAwait(false);
        if (context.HasProperty("to"))
        {
            context.SetValue(context.GetName("to"), WorkflowValues.List(selected));
        }

        return ActivityResult.Completed;
    }
}

/// <summary><c>Browser.UploadFile</c>: sets the files of a file input. Files must be inside the plugin's file root.</summary>
/// <param name="sessions">The run's browser sessions.</param>
/// <param name="options">Plugin settings.</param>
public sealed class UploadFileActivity(BrowserSessions sessions, BrowserPluginOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        Name("UploadFile"), "Upload File", Category, "Sets the files of an input type=file element; paths are confined to the plugin's fileRoot.",
        [
            SelectorProperty,
            new("files", ActivityPropertyKind.Expression, isRequired: true, "Path (String) or List of paths, absolute or relative to fileRoot."),
            SessionProperty,
            TimeoutProperty,
        ]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limits = Limits(context, options);
        var paths = Strings(context, "files").Select(options.Files.ResolveUpload).ToList();
        var element = await Session(context, sessions).LocateAsync(Selector(context), limits).ConfigureAwait(false);
        await element.SetInputFilesAsync(paths, limits.CancellationToken).ConfigureAwait(false);
        return ActivityResult.Completed;
    }
}

/// <summary><c>Browser.DownloadFile</c>: clicks an element and saves the download it starts inside the plugin's file root.</summary>
/// <param name="sessions">The run's browser sessions.</param>
/// <param name="options">Plugin settings.</param>
public sealed class DownloadFileActivity(BrowserSessions sessions, BrowserPluginOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        Name("DownloadFile"), "Download File", Category, "Clicks the element and saves the download it starts; the destination is confined to fileRoot.",
        [
            SelectorProperty,
            new("path", ActivityPropertyKind.Expression, isRequired: true, "Destination file path, absolute or relative to fileRoot."),
            new("overwrite", ActivityPropertyKind.Expression, isRequired: false, "Boolean: replace an existing file (default false)."),
            new("to", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the saved file's full path (String)."),
            SessionProperty,
            TimeoutProperty,
        ]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limits = Limits(context, options);
        var overwrite = context.HasProperty("overwrite") && context.EvaluateBoolean("overwrite");
        var destination = options.Files.ResolveDownload(context.EvaluateText("path"), overwrite);
        await Session(context, sessions).DownloadAsync(Selector(context), destination, limits).ConfigureAwait(false);
        if (context.HasProperty("to"))
        {
            context.SetValue(context.GetName("to"), destination);
        }

        return ActivityResult.Completed;
    }
}

/// <summary><c>Browser.Close</c>: closes a session's page, context and browser.</summary>
/// <param name="sessions">The run's browser sessions.</param>
public sealed class CloseActivity(BrowserSessions sessions) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        Name("Close"), "Close Browser", Category, "Closes the session's browser. Sessions still open when the run ends are closed automatically.",
        [SessionProperty]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        await sessions.CloseAsync(context.HasProperty("session") ? context.EvaluateText("session") : null).ConfigureAwait(false);
        return ActivityResult.Completed;
    }
}
