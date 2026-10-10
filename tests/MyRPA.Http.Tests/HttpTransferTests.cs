using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MyRPA.Workflow.Values;
using static MyRPA.Http.Tests.HttpHost;

namespace MyRPA.Http.Tests;

/// <summary>Retries (ADR-0043): connection errors, 429 and 5xx, backoff, Retry-After, safe methods only.</summary>
public sealed class HttpRetryTests(HttpHost host) : IClassFixture<HttpHost>
{
    private static int _keys;

    private static string Key() => $"k{Interlocked.Increment(ref _keys)}";

    private string Flaky(string key, int fail, int code = 503, string? retryAfter = null) =>
        host.Server.Url($"flaky?key={key}&fail={fail}&code={code}" + (retryAfter is null ? string.Empty : $"&retryAfter={retryAfter}"));

    private int Count(string key) => host.Server.Received.Count(r => r.PathAndQuery.Contains($"key={key}&", StringComparison.Ordinal));

    [Fact]
    public async Task Get_FailingTwiceWith503_SucceedsOnTheThirdAttempt()
    {
        var key = Key();
        var result = await host.RunAsync(Request($$""" "url": "'{{Flaky(key, 2)}}'", "retries": 2, "retryDelayMs": 1, "responseBody": "body" """), ["body"]);

        AssertSucceeded(result);
        Assert.Equal("ok after 3", result.Outputs["body"]);
        Assert.Equal(3, Count(key));
    }

    [Fact]
    public async Task Get_WithoutRetries_FailsOnTheFirst503()
    {
        var key = Key();
        var result = await host.RunAsync(Request($$""" "url": "'{{Flaky(key, 1)}}'" """));

        AssertFailed(result, ErrorTypes.HttpStatus);
        Assert.Equal(1, Count(key));
    }

    [Fact]
    public async Task Retries_WhenExhausted_FailWithTheLastStatus()
    {
        var key = Key();
        var result = await host.RunAsync(Request($$""" "url": "'{{Flaky(key, 9, 502)}}'", "retries": 2, "retryDelayMs": 1 """));

        AssertFailed(result, ErrorTypes.HttpStatus);
        Assert.Contains("502", result.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(3, Count(key));
    }

    [Fact]
    public async Task RetryAfter_IsHonoured_InsteadOfTheBackoff()
    {
        var key = Key();
        var clock = Stopwatch.StartNew();
        var result = await host.RunAsync(Request($$""" "url": "'{{Flaky(key, 1, 429, "1")}}'", "retries": 1, "retryDelayMs": 60000 """));

        AssertSucceeded(result);
        Assert.Equal(2, Count(key));
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task RetryAfter_LongerThanTheCap_EndsTheRetries()
    {
        var key = Key();
        var result = await host.RunAsync(Request($$""" "url": "'{{Flaky(key, 1, 503, "120")}}'", "retries": 3, "retryDelayMs": 1 """));

        AssertFailed(result, ErrorTypes.HttpStatus);
        Assert.Equal(1, Count(key));
    }

    [Fact]
    public async Task Post_IsNotRepeated_UnlessRetryUnsafe()
    {
        var once = Key();
        var unsafeKey = Key();
        var refused = await host.RunAsync(Request($$""" "method": "POST", "url": "'{{Flaky(once, 1)}}'", "body": "'x'", "retries": 2, "retryDelayMs": 1 """));
        var repeated = await host.RunAsync(Request($$""" "method": "POST", "url": "'{{Flaky(unsafeKey, 1)}}'", "body": "'x'", "retries": 2, "retryDelayMs": 1, "retryUnsafe": true """));

        AssertFailed(refused, ErrorTypes.HttpStatus);
        Assert.Equal(1, Count(once));
        AssertSucceeded(repeated);
        Assert.Equal(2, Count(unsafeKey));
    }

    [Fact]
    public async Task ClientErrors_AreNotRepeated()
    {
        var key = Key();
        var result = await host.RunAsync(Request($$""" "url": "'{{Flaky(key, 1, 404)}}'", "retries": 3, "retryDelayMs": 1 """));

        AssertFailed(result, ErrorTypes.HttpStatus);
        Assert.Equal(1, Count(key));
    }

    [Fact]
    public async Task ConnectionErrors_AreRepeated_ThenFailAsHttpConnection()
    {
        var clock = Stopwatch.StartNew();
        var result = await host.RunAsync(Request($$""" "url": "'http://localhost:{{TestServer.FreePort()}}/'", "retries": 2, "retryDelayMs": 200 """));

        AssertFailed(result, ErrorTypes.HttpConnection);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(550), $"two waits of 200 and 400 ms: {clock.Elapsed}");
    }

    [Fact]
    public async Task Retries_OutOfRange_AreRefused()
    {
        var result = await host.RunAsync(Request($$""" "url": "'{{host.Server.Url("text")}}'", "retries": 11 """));

        AssertFailed(result, ErrorTypes.InvalidInput);
    }
}

/// <summary>Http.Download and Http.Upload: the file root, limits, atomic writes and multipart forms (ADR-0043).</summary>
public sealed class HttpFileTransferTests(HttpHost host) : IClassFixture<HttpHost>
{
    private string Root => host.FileRoot!;

    private string Url(string path) => host.Server.Url(path);

    private static string Download(string properties) => Node("Http.Download", properties);

    private static string Upload(string properties) => Node("Http.Upload", properties);

    private string[] Partials() => [.. Directory.EnumerateFiles(Root, "*.download", SearchOption.AllDirectories)];

    private static IReadOnlyDictionary<string, object?> Map(object? value) => (IReadOnlyDictionary<string, object?>)value!;

    private static string Unquoted(string text) => text.Replace("\"", string.Empty, StringComparison.Ordinal);

    [Theory]
    [InlineData("download", "with-length/a.bin")]
    [InlineData("download-chunked", "chunked/a.bin")]
    public async Task Download_WritesTheWholeFile_InsideTheRoot(string endpoint, string path)
    {
        var result = await host.RunAsync(
            Download($$""" "url": "'{{Url($"{endpoint}?size=20000")}}'", "path": "'{{path}}'", "status": "status", "bytes": "bytes", "file": "file" """),
            ["status", "bytes", "file"]);

        AssertSucceeded(result);
        Assert.Equal(200L, result.Outputs["status"]);
        Assert.Equal(20000L, result.Outputs["bytes"]);
        Assert.Equal(path, result.Outputs["file"]);
        Assert.Equal(TestServer.Pattern(20000), File.ReadAllText(Path.Combine(Root, path), Encoding.Latin1));
        Assert.Empty(Partials());
    }

    [Theory]
    [InlineData("download")]
    [InlineData("download-chunked")]
    public async Task Download_LargerThanTheLimit_FailsAndLeavesNoFile(string endpoint)
    {
        var path = $"big-{endpoint}.bin";
        var result = await host.RunAsync(Download($$""" "url": "'{{Url($"{endpoint}?size={MaxDownloadBytes + 1}")}}'", "path": "'{{path}}'" """));

        AssertFailed(result, ErrorTypes.ResponseTooLarge);
        Assert.Contains("maxDownloadBytes", result.Error!.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Root, path)));
        Assert.Empty(Partials());
    }

    [Fact]
    public async Task Download_AnExistingFile_IsReplacedOnlyWithOverwrite()
    {
        File.WriteAllText(Path.Combine(Root, "keep.bin"), "mine");
        var refused = await host.RunAsync(Download($$""" "url": "'{{Url("download?size=10")}}'", "path": "'keep.bin'" """));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Root, "keep.bin")));

        var replaced = await host.RunAsync(Download($$""" "url": "'{{Url("download?size=10")}}'", "path": "'keep.bin'", "overwrite": true """));

        AssertFailed(refused, ErrorTypes.FileAlreadyExists);
        AssertSucceeded(replaced);
        Assert.Equal(TestServer.Pattern(10), File.ReadAllText(Path.Combine(Root, "keep.bin")));
    }

    [Fact]
    public async Task Download_OutsideTheRoot_IsRefusedBeforeAnyRequest()
    {
        var before = host.Server.For("/download").Count;
        var result = await host.RunAsync(Download($$""" "url": "'{{Url("download?size=10")}}'", "path": "'../escape.bin'" """));

        AssertFailed(result, ErrorTypes.FileAccessDenied);
        Assert.Equal(before, host.Server.For("/download").Count);
    }

    [Fact]
    public async Task Download_ErrorStatus_FailsAndLeavesNoFile()
    {
        var result = await host.RunAsync(Download($$""" "url": "'{{Url("status/404")}}'", "path": "'missing.bin'" """));

        AssertFailed(result, ErrorTypes.HttpStatus);
        Assert.False(File.Exists(Path.Combine(Root, "missing.bin")));
    }

    [Fact]
    public async Task Upload_SendsFilesAndFields_AsAMultipartForm()
    {
        Directory.CreateDirectory(Path.Combine(Root, "in"));
        File.WriteAllText(Path.Combine(Root, "in", "invoice.txt"), "invoice 42 content");
        var files = WorkflowValues.Dictionary([new("doc", "in/invoice.txt")]);
        var fields = WorkflowValues.Dictionary([new("kind", "invoice"), new("count", 1L)]);

        var result = await host.RunAsync(
            Upload($$""" "url": "'{{Url("echo")}}'", "files": "files", "fields": "fields", "status": "status", "responseBody": "body" """),
            ["status", "body"],
            new Dictionary<string, object?> { ["files"] = files, ["fields"] = fields });

        AssertSucceeded(result);
        var echo = Map(result.Outputs["body"]);
        Assert.Equal("POST", echo["method"]);
        Assert.StartsWith("multipart/form-data", (string)Map(echo["headers"])["content-type"]!, StringComparison.Ordinal);
        var body = (string)echo["body"]!;
        Assert.Contains("name=doc; filename=invoice.txt", Unquoted(body), StringComparison.Ordinal);
        Assert.Contains("invoice 42 content", body, StringComparison.Ordinal);
        Assert.Contains("name=kind", Unquoted(body), StringComparison.Ordinal);
        Assert.Contains("\r\n1\r\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_FollowsA307_AndSendsTheFilesAgain()
    {
        File.WriteAllText(Path.Combine(Root, "again.txt"), "sent twice");
        var files = WorkflowValues.Dictionary([new("f", "again.txt")]);
        var target = Uri.EscapeDataString(Url("echo"));

        var result = await host.RunAsync(
            Upload($$""" "url": "'{{Url($"redirect?code=307&to={target}")}}'", "files": "files", "responseBody": "body" """),
            ["body"],
            new Dictionary<string, object?> { ["files"] = files });

        AssertSucceeded(result);
        Assert.Contains("sent twice", (string)Map(result.Outputs["body"])["body"]!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nope.txt", ErrorTypes.FileNotFound)]
    [InlineData("../outside.txt", ErrorTypes.FileAccessDenied)]
    public async Task Upload_AFileThatCannotBeRead_IsRefusedBeforeAnyRequest(string path, string errorType)
    {
        var before = host.Server.For("/echo").Count;
        var result = await host.RunAsync(
            Upload($$""" "url": "'{{Url("echo")}}'", "files": "files" """),
            secrets: new Dictionary<string, object?> { ["files"] = WorkflowValues.Dictionary([new("f", path)]) });

        AssertFailed(result, errorType);
        Assert.Equal(before, host.Server.For("/echo").Count);
    }

    [Fact]
    public async Task Upload_LargerThanTheLimit_IsRefused()
    {
        File.WriteAllText(Path.Combine(Root, "large.txt"), new string('x', MaxUploadBytes + 1));
        var result = await host.RunAsync(
            Upload($$""" "url": "'{{Url("echo")}}'", "files": "files" """),
            secrets: new Dictionary<string, object?> { ["files"] = WorkflowValues.Dictionary([new("f", "large.txt")]) });

        AssertFailed(result, ErrorTypes.FileTooLarge);
    }
}

/// <summary>Without the fileRoot setting, downloads and uploads touch no file.</summary>
public sealed class HttpWithoutFileRootTests(HttpHostWithoutFiles host) : IClassFixture<HttpHostWithoutFiles>
{
    [Fact]
    public async Task Download_WithoutAFileRoot_IsRefused()
    {
        var result = await host.RunAsync(Node("Http.Download", $$""" "url": "'{{host.Server.Url("download?size=1")}}'", "path": "'a.bin'" """));

        AssertFailed(result, ErrorTypes.FileAccessDenied);
        Assert.Contains("fileRoot", result.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(host.Server.For("/download"));
    }
}

/// <summary>Chat.Post: each platform's payload, the secret webhook URL, no redirects, only 429 repeated (ADR-0043).</summary>
public sealed class ChatPostTests(HttpHost host) : IClassFixture<HttpHost>
{
    private static int _cases;

    private static string Post(string properties) => Node("Chat.Post", properties);

    /// <summary>Posts to the echo endpoint (a unique query per case) and returns the JSON body the server received.</summary>
    private async Task<JsonElement> PostedAsync(string platform, string extra)
    {
        var url = host.Server.Url($"echo?case={Interlocked.Increment(ref _cases)}");
        var result = await host.RunAsync(
            Post($$""" "platform": "{{platform}}", "webhookUrl": "hook", "message": "'Invoice 42 is in'"{{extra}}, "status": "status" """),
            ["status"],
            new Dictionary<string, object?> { ["hook"] = url });

        AssertSucceeded(result);
        Assert.Equal(200L, result.Outputs["status"]);
        var received = host.Server.Received.Single(r => url.EndsWith(r.PathAndQuery, StringComparison.Ordinal));
        Assert.Equal("POST", received.Method);
        Assert.StartsWith("application/json", received.Headers["content-type"], StringComparison.Ordinal);
        using var document = JsonDocument.Parse(received.Body);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Slack_PlainMessage_IsText()
    {
        var body = await PostedAsync("Slack", string.Empty);

        Assert.Equal("Invoice 42 is in", body.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Slack_WithColour_IsAnAttachment()
    {
        var body = await PostedAsync("Slack", """, "title": "'Intake'", "color": "'#65a30d'" """);

        var attachment = body.GetProperty("attachments")[0];
        Assert.Equal("#65A30D", attachment.GetProperty("color").GetString());
        Assert.Equal("Intake", attachment.GetProperty("title").GetString());
        Assert.Equal("Invoice 42 is in", attachment.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Discord_WithTitleAndColour_IsAnEmbed()
    {
        var body = await PostedAsync("Discord", """, "title": "'Intake'", "color": "'65A30D'" """);

        var embed = body.GetProperty("embeds")[0];
        Assert.Equal("Intake", embed.GetProperty("title").GetString());
        Assert.Equal("Invoice 42 is in", embed.GetProperty("description").GetString());
        Assert.Equal(0x65A30D, embed.GetProperty("color").GetInt32());
    }

    [Fact]
    public async Task Discord_PlainMessage_IsContent()
    {
        var body = await PostedAsync("Discord", string.Empty);

        Assert.Equal("Invoice 42 is in", body.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Teams_IsAnAdaptiveCard()
    {
        var body = await PostedAsync("Teams", """, "title": "'Intake'" """);

        var card = body.GetProperty("attachments")[0];
        Assert.Equal("application/vnd.microsoft.card.adaptive", card.GetProperty("contentType").GetString());
        var blocks = card.GetProperty("content").GetProperty("body");
        Assert.Equal("Intake", blocks[0].GetProperty("text").GetString());
        Assert.Equal("Invoice 42 is in", blocks[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Generic_IsTitleMessageAndColour()
    {
        var body = await PostedAsync("Generic", """, "title": "'Intake'", "color": "'#00ff00'" """);

        Assert.Equal("Intake", body.GetProperty("title").GetString());
        Assert.Equal("Invoice 42 is in", body.GetProperty("message").GetString());
        Assert.Equal("#00FF00", body.GetProperty("color").GetString());
    }

    [Fact]
    public async Task AFailure_NamesOnlyTheHost_NeverTheWebhookPath()
    {
        var result = await host.RunAsync(
            Post(""" "platform": "Slack", "webhookUrl": "hook", "message": "'x'" """),
            secrets: new Dictionary<string, object?> { ["hook"] = host.Server.Url("status/500?T0KEN=hidden-part") });

        AssertFailed(result, ErrorTypes.HttpStatus);
        Assert.Contains("localhost", result.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("status/500", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-part", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Logs.Messages, m => m.Contains("hidden-part", StringComparison.Ordinal) || m.Contains("status/500", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARedirect_IsNotFollowed()
    {
        var result = await host.RunAsync(
            Post(""" "platform": "Generic", "webhookUrl": "hook", "message": "'x'" """),
            secrets: new Dictionary<string, object?> { ["hook"] = host.Server.Url($"redirect?code=307&to={Uri.EscapeDataString(host.Server.Url("echo"))}") });

        AssertFailed(result, ErrorTypes.RedirectNotAllowed);
    }

    [Fact]
    public async Task OnlyARateLimit_IsRepeated()
    {
        var repeated = await host.RunAsync(
            Post(""" "platform": "Slack", "webhookUrl": "hook", "message": "'x'", "retries": 2, "retryDelayMs": 1 """),
            secrets: new Dictionary<string, object?> { ["hook"] = host.Server.Url("flaky?key=chat-429&fail=1&code=429&retryAfter=0") });
        var notRepeated = await host.RunAsync(
            Post(""" "platform": "Slack", "webhookUrl": "hook", "message": "'x'", "retries": 2, "retryDelayMs": 1 """),
            secrets: new Dictionary<string, object?> { ["hook"] = host.Server.Url("flaky?key=chat-503&fail=1&code=503") });

        AssertSucceeded(repeated);
        Assert.Equal(2, host.Server.Received.Count(r => r.PathAndQuery.Contains("key=chat-429", StringComparison.Ordinal)));
        AssertFailed(notRepeated, ErrorTypes.HttpStatus);
        Assert.Equal(1, host.Server.Received.Count(r => r.PathAndQuery.Contains("key=chat-503", StringComparison.Ordinal)));
    }

    [Fact]
    public void AWebhookUrlWrittenInTheWorkflow_IsRefusedByTheLoader()
    {
        var load = host.Load(Post($$""" "platform": "Slack", "webhookUrl": "'{{host.Server.Url("echo")}}'", "message": "'x'" """), [], []);

        Assert.False(load.IsValid);
        Assert.Contains(load.Diagnostics, d => d.Code == "MYRPA1066");
    }

    [Fact]
    public async Task AColourThatIsNotRgbHex_IsRefused()
    {
        var result = await host.RunAsync(
            Post(""" "platform": "Slack", "webhookUrl": "hook", "message": "'x'", "color": "'green'" """),
            secrets: new Dictionary<string, object?> { ["hook"] = host.Server.Url("echo") });

        AssertFailed(result, ErrorTypes.InvalidInput);
    }
}
