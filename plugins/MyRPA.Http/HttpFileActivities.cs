using System.Net.Http.Headers;
using MyRPA.Core.Activities;
using MyRPA.Sdk.Files;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;
using static MyRPA.Http.HttpCore;

namespace MyRPA.Http;

/// <summary>
/// <c>Http.Download</c>: streams a GET response to a file under the plugin's fileRoot (ADR-0043). The file appears only
/// when the download is complete (written beside it, then moved), never larger than maxDownloadBytes, and never replaces
/// a file unless <c>overwrite</c> is true.
/// </summary>
public sealed class HttpDownloadActivity(HttpOptions options, HttpGateway gateway) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Http.Download"),
        "Download File",
        "HTTP",
        "Downloads a URL (GET) to a file inside the HTTP plugin's fileRoot. The file appears only once complete; a status of 400 or above fails (HttpStatus); a body larger than maxDownloadBytes fails (ResponseTooLarge) and leaves no file.",
        [
            Input("url", ActivityValueType.String, "The absolute http or https URL.", required: true),
            Input("path", ActivityValueType.String, "The file to write, relative to the plugin's fileRoot.", required: true),
            Input("overwrite", ActivityValueType.Boolean, "Replace the file when it exists (otherwise FileAlreadyExists).", defaultJson: "false"),
            .. AuthProperties(),
            .. TimingProperties("connection errors, 429 and 5xx"),
            Output("status", ActivityValueType.Int, "Receives the status code."),
            Output("bytes", ActivityValueType.Int, "Receives the size of the file written."),
            Output("file", ActivityValueType.String, "Receives the file's path relative to the fileRoot."),
        ])
    { SideEffects = ActivitySideEffects.Network | ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var core = new HttpCore(options, gateway);
        var url = core.ParseUrl(context);
        var files = options.RequireFiles("Http.Download");
        var pathText = Text(context, "path");
        var overwrite = Flag(context, "overwrite", false);
        var target = files.ResolveFileToWrite(pathText, overwrite);
        var call = new HttpCall(url, "GET", Headers(context), null, Credential(context), Safe(url));

        var (status, length) = await core.ExchangeAsync(context, call, Retry(context), async (response, cancellationToken) =>
        {
            EnsureSuccess(call, response, failOnErrorStatus: true);
            var written = await SaveAsync(response.Content, call, target, pathText, overwrite, cancellationToken).ConfigureAwait(false);
            return ((long)(int)response.StatusCode, written);
        }).ConfigureAwait(false);

        Set(context, "status", status);
        Set(context, "bytes", length);
        Set(context, "file", files.Relative(target));
        return ActivityResult.Completed;
    }

    /// <summary>Streams the body to a hidden file beside the target, then moves it into place.</summary>
    private async Task<long> SaveAsync(HttpContent content, HttpCall call, string target, string pathText, bool overwrite, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > options.MaxDownloadBytes)
        {
            throw TooLarge(call, options.MaxDownloadBytes, "maxDownloadBytes");
        }

        var partial = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Path.GetRandomFileName()}.download");
        try
        {
            long total = 0;
            var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var file = FileIo(pathText, () => new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true));
                await using (file.ConfigureAwait(false))
                {
                    var chunk = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > options.MaxDownloadBytes)
                        {
                            throw TooLarge(call, options.MaxDownloadBytes, "maxDownloadBytes");
                        }

                        await FileIoAsync(pathText, () => file.WriteAsync(chunk.AsMemory(0, read), cancellationToken)).ConfigureAwait(false);
                    }
                }
            }

            if (!overwrite && File.Exists(target))
            {
                // Created by someone else while downloading: never replaced without overwrite.
                throw new ActivityFailedException(FileErrorTypes.FileAlreadyExists, $"The file '{pathText}' exists; set 'overwrite' to replace it.");
            }

            FileIo(pathText, () =>
            {
                File.Move(partial, target, overwrite);
                return true;
            });
            return total;
        }
        finally
        {
            TryDelete(partial);
        }
    }

    private static T FileIo<T>(string path, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ActivityFailedException(FileErrorTypes.FileIoError, $"The file '{path}' could not be written: it is in use, read-only or not accessible.", ex);
        }
    }

    private static async Task FileIoAsync(string path, Func<ValueTask> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ActivityFailedException(FileErrorTypes.FileIoError, $"The file '{path}' could not be written: it is in use, read-only or not accessible.", ex);
        }
    }

    private static void TryDelete(string path)
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
}

/// <summary>
/// <c>Http.Upload</c>: a multipart/form-data request with files from the plugin's fileRoot and text fields (ADR-0043).
/// The files are read again for each attempt or redirect, so a repeat sends the same content.
/// </summary>
public sealed class HttpUploadActivity(HttpOptions options, HttpGateway gateway) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Http.Upload"),
        "Upload Files",
        "HTTP",
        "Sends files (from the HTTP plugin's fileRoot) and text fields as a multipart form (POST or PUT). The response is handled as in HTTP Request; a status of 400 or above fails (HttpStatus) unless failOnErrorStatus is false.",
        [
            new("method", ActivityPropertyKind.Text, isRequired: false, "The HTTP method.", ["POST", "PUT"]) { ValueType = ActivityValueType.String, DefaultValue = "\"POST\"" },
            Input("url", ActivityValueType.String, "The absolute http or https URL.", required: true),
            Input("files", ActivityValueType.Dictionary, "The files: a Dictionary of form field name → path relative to the fileRoot.", required: true),
            Input("fields", ActivityValueType.Dictionary, "Text form fields: a Dictionary of name → value."),
            .. AuthProperties(),
            .. TimingProperties("connection errors, 429 and 5xx; PUT only, unless retryUnsafe"),
            Input("retryUnsafe", ActivityValueType.Boolean, "Also repeat POST (only when the server ignores a repeated upload).", defaultJson: "false"),
            Input("failOnErrorStatus", ActivityValueType.Boolean, "Fail (HttpStatus) when the status is 400 or above.", defaultJson: "true"),
            Input("parseJson", ActivityValueType.Boolean, "Turn a JSON response into a workflow value (otherwise the body is text).", defaultJson: "true"),
            Output("status", ActivityValueType.Int, "Receives the status code."),
            Output("responseHeaders", ActivityValueType.Dictionary, "Receives the response headers (lower-case names)."),
            Output("responseBody", ActivityValueType.Any, "Receives the body: a workflow value for JSON, otherwise text; null when empty."),
        ])
    { SideEffects = ActivitySideEffects.Network | ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var core = new HttpCore(options, gateway);
        var method = context.GetTextOrDefault("method", "POST");
        if (method is not ("POST" or "PUT"))
        {
            throw Invalid(context, "method", "POST or PUT");
        }

        var url = core.ParseUrl(context);
        var parts = Files(context, options.RequireFiles("Http.Upload"));
        var fields = Fields(context);
        var call = new HttpCall(url, method, Headers(context), () => Form(parts, fields), Credential(context), Safe(url));
        var parseJson = Flag(context, "parseJson", true);
        var failOnErrorStatus = Flag(context, "failOnErrorStatus", true);

        await core.ExchangeAsync(context, call, Retry(context), async (response, cancellationToken) =>
        {
            EnsureSuccess(call, response, failOnErrorStatus);
            var bytes = await ReadAsync(response.Content, call, options.MaxResponseBytes, "maxResponseBytes", cancellationToken).ConfigureAwait(false);
            var value = Decode(response.Content, bytes, parseJson, call);
            Set(context, "status", (long)(int)response.StatusCode);
            Set(context, "responseHeaders", ResponseHeaders(response));
            Set(context, "responseBody", value);
            return true;
        }).ConfigureAwait(false);

        return ActivityResult.Completed;
    }

    /// <summary>The files to send, resolved and size-checked before anything is sent.</summary>
    private List<(string Field, string FullPath, string FileName)> Files(IActivityContext context, FileRootPolicy policy)
    {
        var map = context.Evaluate("files") as IReadOnlyDictionary<string, object?> ?? throw Invalid(context, "files", "a Dictionary of form field names and file paths");
        if (map.Count == 0)
        {
            throw Invalid(context, "files", "a Dictionary with at least one file");
        }

        var parts = new List<(string, string, string)>();
        long total = 0;
        foreach (var (field, raw) in map)
        {
            var path = raw as string ?? throw Invalid(context, "files", "a Dictionary whose values are file paths (text)");
            var full = policy.ResolveExistingFile(path);
            total += new FileInfo(full).Length;
            if (total > options.MaxUploadBytes)
            {
                throw new ActivityFailedException(FileErrorTypes.FileTooLarge, $"The files of {context.Node.Type} '{context.Node.Id}' are larger than {options.MaxUploadBytes} bytes (setting maxUploadBytes).");
            }

            parts.Add((field, full, Path.GetFileName(full)));
        }

        return parts;
    }

    private static List<KeyValuePair<string, string>> Fields(IActivityContext context)
    {
        if (!context.HasProperty("fields"))
        {
            return [];
        }

        var map = context.Evaluate("fields") as IReadOnlyDictionary<string, object?> ?? throw Invalid(context, "fields", "a Dictionary of field names and values");
        return [.. map.Where(f => f.Value is not null).Select(f => new KeyValuePair<string, string>(f.Key, f.Value as string ?? WorkflowValues.ToDisplayString(f.Value)))];
    }

    /// <summary>A new form for each attempt: the files are opened again (read-only, shared) and closed with the request.</summary>
    private static MultipartFormDataContent Form(List<(string Field, string FullPath, string FileName)> parts, List<KeyValuePair<string, string>> fields)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value) in fields)
        {
            form.Add(new StringContent(value), name);
        }

        foreach (var (field, fullPath, fileName) in parts)
        {
            var file = new StreamContent(new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, field, fileName);
        }

        return form;
    }
}
