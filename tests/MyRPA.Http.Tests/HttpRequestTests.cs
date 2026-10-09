using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Core.Activities;
using MyRPA.Core.Execution;
using MyRPA.Workflow.Validation;
using MyRPA.Workflow.Values;
using static MyRPA.Http.Tests.HttpHost;

namespace MyRPA.Http.Tests;

public sealed class HttpRequestTests(HttpHost host) : IClassFixture<HttpHost>
{
    private const string Token = "s3cr3t-token-value";

    private static IReadOnlyDictionary<string, object?> Map(object? value) => (IReadOnlyDictionary<string, object?>)value!;

    private string Url(string path) => host.Server.Url(path);

    [Fact]
    public async Task Get_JsonResponse_GivesStatusHeadersAndParsedBody()
    {
        var result = await host.RunAsync(
            Request($$""" "url": "'{{Url("echo?x=1")}}'", "status": "status", "responseHeaders": "headers", "responseBody": "body" """),
            ["status", "headers", "body"]);

        AssertSucceeded(result);
        Assert.Equal(200L, result.Outputs["status"]);
        Assert.StartsWith("application/json", (string)Map(result.Outputs["headers"])["content-type"]!, StringComparison.Ordinal);
        var body = Map(result.Outputs["body"]);
        Assert.Equal("GET", body["method"]);
        Assert.Equal("?x=1", body["query"]);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Methods_AreSent(string method)
    {
        var result = await host.RunAsync(Request($$""" "method": "{{method}}", "url": "'{{Url("echo")}}'", "responseBody": "body" """), ["body"]);

        AssertSucceeded(result);
        Assert.Equal(method, Map(result.Outputs["body"])["method"]);
    }

    [Fact]
    public async Task Head_HasStatusAndNoBody()
    {
        var result = await host.RunAsync(Request($$""" "method": "HEAD", "url": "'{{Url("text")}}'", "status": "status", "responseBody": "body" """), ["status", "body"]);

        AssertSucceeded(result);
        Assert.Equal(200L, result.Outputs["status"]);
        Assert.Null(result.Outputs["body"]);
    }

    [Fact]
    public async Task Post_NonStringBody_IsSentAsJson_AndStringBodyAsText()
    {
        var result = await host.RunAsync(
            Request($$""" "method": "POST", "url": "'{{Url("echo")}}'", "body": "42", "responseBody": "a" """) + "," +
            Request($$""" "method": "POST", "url": "'{{Url("echo")}}'", "body": "'plain text'", "responseBody": "b" """),
            ["a", "b"]);

        AssertSucceeded(result);
        var json = Map(result.Outputs["a"]);
        Assert.Equal("42", json["body"]);
        Assert.StartsWith("application/json", (string)Map(json["headers"])["content-type"]!, StringComparison.Ordinal);
        var text = Map(result.Outputs["b"]);
        Assert.Equal("plain text", text["body"]);
        Assert.StartsWith("text/plain", (string)Map(text["headers"])["content-type"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Headers_AreSent_AndContentTypeCanBeChosen()
    {
        var sent = WorkflowValues.Dictionary([new("X-Custom", "one"), new("Content-Type", "application/xml")]);
        var result = await host.RunAsync(
            Request($$""" "method": "POST", "url": "'{{Url("echo")}}'", "body": "'<a/>'", "headers": "hdrs", "responseBody": "body" """),
            ["body"],
            new Dictionary<string, object?> { ["hdrs"] = sent });

        AssertSucceeded(result);
        var headers = Map(Map(result.Outputs["body"])["headers"]);
        Assert.Equal("one", headers["x-custom"]);
        Assert.Equal("application/xml", headers["content-type"]);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Host")]
    [InlineData("Content-Length")]
    public async Task Headers_ReservedNames_AreRefused(string name)
    {
        var result = await host.RunAsync(
            Request($$""" "url": "'{{Url("echo")}}'", "headers": "hdrs" """),
            null,
            new Dictionary<string, object?> { ["hdrs"] = WorkflowValues.Dictionary([new(name, "x")]) });

        AssertFailed(result, ErrorTypes.InvalidInput);
    }

    [Fact]
    public async Task Bearer_SendsTheTokenAndItNeverReachesLogsOrErrors()
    {
        var secrets = new Dictionary<string, object?> { ["apiToken"] = Token };
        var ok = await host.RunAsync(Request($$""" "url": "'{{Url("echo")}}'", "auth": "Bearer", "token": "apiToken", "responseBody": "body" """), ["body"], secrets);
        var failed = await host.RunAsync(Request($$""" "url": "'{{Url("status/401")}}?key=' + apiToken", "auth": "Bearer", "token": "apiToken" """), null, secrets);

        AssertSucceeded(ok);
        Assert.Equal("Bearer " + Token, Map(Map(ok.Outputs["body"])["headers"])["authorization"]);
        AssertFailed(failed, ErrorTypes.HttpStatus);
        Assert.Contains("401", failed.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, failed.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("key=", failed.Error.Message, StringComparison.Ordinal);
        Assert.NotEmpty(host.Logs.Messages);
        Assert.DoesNotContain(host.Logs.Messages, m => m.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Basic_AndApiKey_SendTheirHeaders()
    {
        var secrets = new Dictionary<string, object?> { ["pw"] = "p@ss", ["key"] = "k-123" };
        var result = await host.RunAsync(
            Request($$""" "url": "'{{Url("echo")}}'", "auth": "Basic", "username": "'ada'", "password": "pw", "responseBody": "basic" """) + "," +
            Request($$""" "url": "'{{Url("echo")}}'", "auth": "ApiKey", "apiKeyHeader": "X-Service-Key", "token": "key", "responseBody": "apiKey" """),
            ["basic", "apiKey"],
            secrets);

        AssertSucceeded(result);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ada:p@ss")), Map(Map(result.Outputs["basic"])["headers"])["authorization"]);
        Assert.Equal("k-123", Map(Map(result.Outputs["apiKey"])["headers"])["x-service-key"]);
    }

    [Fact]
    public void Token_WrittenInTheWorkflow_IsRefusedByValidation()
    {
        var load = host.Load(Request($$""" "url": "'{{Url("echo")}}'", "auth": "Bearer", "token": "'abc'" """), [], []);

        Assert.False(load.IsValid);
        Assert.Contains(load.Diagnostics, d => d.Code == DiagnosticCodes.SecretLiteral);
    }

    [Fact]
    public async Task Bearer_WithoutAToken_FailsWithInvalidInput()
    {
        var result = await host.RunAsync(Request($$""" "url": "'{{Url("echo")}}'", "auth": "Bearer" """));

        AssertFailed(result, ErrorTypes.InvalidInput);
    }

    [Fact]
    public async Task ErrorStatus_FailsUnlessFailOnErrorStatusIsFalse()
    {
        var failed = await host.RunAsync(Request($$""" "url": "'{{Url("status/404")}}'" """));
        var kept = await host.RunAsync(Request($$""" "url": "'{{Url("status/503")}}'", "failOnErrorStatus": "false", "status": "status", "responseBody": "body" """), ["status", "body"]);

        AssertFailed(failed, ErrorTypes.HttpStatus);
        AssertSucceeded(kept);
        Assert.Equal(503L, kept.Outputs["status"]);
        Assert.Equal("nope", Map(kept.Outputs["body"])["error"]);
    }

    [Fact]
    public async Task Redirect_SameOrigin_KeepsCredentials_OtherOrigin_DropsThem()
    {
        var secrets = new Dictionary<string, object?> { ["apiToken"] = Token };
        var same = Uri.EscapeDataString(Url("echo?hop=same"));
        var other = Uri.EscapeDataString(host.Other.Url("echo?hop=other"));

        var result = await host.RunAsync(
            Request($$""" "url": "'{{Url($"redirect?to={same}")}}'", "auth": "Bearer", "token": "apiToken", "responseBody": "same" """) + "," +
            Request($$""" "url": "'{{Url($"redirect?to={other}")}}'", "auth": "ApiKey", "token": "apiToken", "responseBody": "other" """),
            ["same", "other"],
            secrets);

        AssertSucceeded(result);
        Assert.Equal("Bearer " + Token, Map(Map(result.Outputs["same"])["headers"])["authorization"]);
        var otherHeaders = Map(Map(result.Outputs["other"])["headers"]);
        Assert.False(otherHeaders.ContainsKey("authorization"));
        Assert.False(otherHeaders.ContainsKey("x-api-key"));
        Assert.DoesNotContain(host.Other.Received, r => r.Headers.Values.Any(v => v.Contains(Token, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Redirect303AfterPost_BecomesGetWithoutBody()
    {
        var to = Uri.EscapeDataString(Url("echo"));
        var result = await host.RunAsync(
            Request($$""" "method": "POST", "url": "'{{Url($"redirect?code=303&to={to}")}}'", "body": "'payload'", "responseBody": "body" """),
            ["body"]);

        AssertSucceeded(result);
        Assert.Equal("GET", Map(result.Outputs["body"])["method"]);
        Assert.Equal(string.Empty, Map(result.Outputs["body"])["body"]);
    }

    [Fact]
    public async Task Redirect_Loop_FailsWithTooManyRedirects()
    {
        AssertFailed(await host.RunAsync(Request($$""" "url": "'{{Url("loop")}}'" """)), ErrorTypes.TooManyRedirects);
        Assert.Equal(MaxRedirects + 1, host.Server.For("/loop").Count);
    }

    [Fact]
    public async Task HostNotInAllowedHosts_IsRefusedBeforeAnyRequest_AlsoOnARedirect()
    {
        var direct = await host.RunAsync(Request($$""" "url": "'http://127.0.0.1:{{host.Server.Port}}/echo?direct'" """));
        var to = Uri.EscapeDataString($"http://127.0.0.1:{host.Other.Port}/echo?redirected");
        var redirected = await host.RunAsync(Request($$""" "url": "'{{Url($"redirect?to={to}")}}'" """));

        AssertFailed(direct, ErrorTypes.HostNotAllowed);
        AssertFailed(redirected, ErrorTypes.HostNotAllowed);
        Assert.DoesNotContain(host.Server.Received, r => r.PathAndQuery.EndsWith("?direct", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Other.Received, r => r.PathAndQuery.EndsWith("?redirected", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ftp://localhost/x")]
    [InlineData("file:///c:/windows/win.ini")]
    [InlineData("http://user:pw@localhost/echo")]
    [InlineData("not a url")]
    [InlineData("/relative")]
    public async Task InvalidUrls_FailWithInvalidUrl(string url)
    {
        AssertFailed(await host.RunAsync(Request($$""" "url": "'{{url}}'" """)), ErrorTypes.InvalidUrl);
    }

    [Theory]
    [InlineData("big")]
    [InlineData("big-chunked")]
    public async Task ResponseLargerThanTheLimit_FailsWithResponseTooLarge(string path)
    {
        AssertFailed(await host.RunAsync(Request($$""" "url": "'{{Url(path)}}'" """)), ErrorTypes.ResponseTooLarge);
    }

    [Fact]
    public async Task SlowServer_FailsWithTimeout()
    {
        var result = await host.RunAsync(Request($$""" "url": "'{{Url("slow")}}'", "timeoutMs": "300" """));

        AssertFailed(result, ErrorTypes.Timeout);
    }

    [Fact]
    public async Task CancellingTheRun_CancelsTheRequest()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(300));

        var result = await host.RunAsync(Request($$""" "url": "'{{Url("slow")}}'" """), cancellationToken: cancel.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task ClosedPort_FailsWithHttpConnection()
    {
        var port = TestServer.FreePort();

        AssertFailed(await host.RunAsync(Request($$""" "url": "'http://localhost:{{port}}/'", "timeoutMs": "10000" """)), ErrorTypes.HttpConnection);
    }

    [Fact]
    public async Task Json_InvalidOrTooDeep_FailsWithInvalidJson_UnlessParseJsonIsFalse()
    {
        AssertFailed(await host.RunAsync(Request($$""" "url": "'{{Url("bad-json")}}'" """)), ErrorTypes.InvalidJson);
        AssertFailed(await host.RunAsync(Request($$""" "url": "'{{Url("deep-json")}}'" """)), ErrorTypes.InvalidJson);

        var raw = await host.RunAsync(Request($$""" "url": "'{{Url("bad-json")}}'", "parseJson": "false", "responseBody": "body" """), ["body"]);
        var text = await host.RunAsync(Request($$""" "url": "'{{Url("text")}}'", "responseBody": "body" """), ["body"]);

        AssertSucceeded(raw);
        Assert.Equal("{not json", raw.Outputs["body"]);
        Assert.Equal("héllo", text.Outputs["body"]);
    }

    [Fact]
    public async Task ConcurrentRuns_KeepTheirOwnCredentials()
    {
        var runs = Enumerable.Range(0, 10).Select(i => host.RunAsync(
            Request($$""" "url": "'{{Url($"echo?run={i}")}}'", "auth": "Bearer", "token": "apiToken", "responseBody": "body" """),
            ["body"],
            new Dictionary<string, object?> { ["apiToken"] = $"token-{i}" }));

        var results = await Task.WhenAll(runs);

        for (var i = 0; i < results.Length; i++)
        {
            AssertSucceeded(results[i]);
            var body = Map(results[i].Outputs["body"]);
            Assert.Equal($"?run={i}", body["query"]);
            Assert.Equal($"Bearer token-{i}", Map(body["headers"])["authorization"]);
        }
    }

    [Fact]
    public void Catalog_DescribesTheNetworkSideEffectAndSecretProperties()
    {
        Assert.True(host.Services.GetRequiredService<IActivityCatalog>().TryGet(new ActivityTypeName("Http.Request"), out var descriptor));

        Assert.Equal(ActivitySideEffects.Network, descriptor.SideEffects);
        Assert.Equal(["password", "token"], descriptor.Properties.Where(p => p.IsSecret).Select(p => p.Name).Order(StringComparer.Ordinal));
    }
}
