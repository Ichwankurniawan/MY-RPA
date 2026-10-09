using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyRPA.Server.Tests;

/// <summary>Expression assist through the server (ADR-0041): functions, names in scope, references of a declaration.</summary>
public sealed class ExpressionAssistTests
{
    private const string MessagePath = "$.root.children[0].slots.body.properties.message";

    private static readonly JsonElement _document = JsonDocument.Parse("""
        { "schemaVersion": "1.0", "id": "w", "name": "W", "version": "1",
          "arguments": [ { "name": "who", "direction": "In", "type": "String" } ],
          "variables": [ { "name": "items", "type": "List" } ],
          "root": { "id": "main", "type": "Core.Sequence", "children": [
            { "id": "loop", "type": "Core.ForEach", "properties": { "items": "items", "itemVariable": "item" },
              "slots": { "body": { "id": "say", "type": "Core.Log", "properties": { "message": "who + ': ' + item" } } } } ] } }
        """).RootElement;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Functions_ListEveryExpressionFunction_WithSignatureAndDescription()
    {
        await using var h = await ServerHarness.StartAsync();

        using var response = await h.Client.GetAsync("/api/expressions/functions", Token);
        var functions = (await ServerHarness.JsonAsync(response)).EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var substring = Assert.Single(functions, f => f.GetProperty("name").GetString() == "substring");
        Assert.Equal(("substring(text, start, length?)", 2, 3), (substring.GetProperty("signature").GetString(), substring.GetProperty("minArguments").GetInt32(), substring.GetProperty("maxArguments").GetInt32()));
        Assert.All(functions, f => Assert.False(string.IsNullOrEmpty(f.GetProperty("description").GetString())));
    }

    [Fact]
    public async Task Scope_ListsTheNamesVisibleAtAPath_FromTheLoader()
    {
        await using var h = await ServerHarness.StartAsync();

        using var response = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/expressions/scope", new { document = _document, path = MessagePath }));
        var names = (await ServerHarness.JsonAsync(response)).GetProperty("names").EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["who", "items", "item"], names.Select(n => n.GetProperty("name").GetString()));
        Assert.Equal(("Argument", "String", "In"), (names[0].GetProperty("kind").GetString(), names[0].GetProperty("type").GetString(), names[0].GetProperty("direction").GetString()));
        Assert.Equal(("Local", "$.root.children[0].properties.itemVariable"), (names[2].GetProperty("kind").GetString(), names[2].GetProperty("path").GetString()));
    }

    [Fact]
    public async Task References_GiveTheDeclarationAndEveryUse_WithPositions()
    {
        await using var h = await ServerHarness.StartAsync();

        using var response = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/expressions/references", new { document = _document, path = MessagePath, name = "item" }));
        var json = await ServerHarness.JsonAsync(response);
        using var unknown = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/expressions/references", new { document = _document, path = "$.root", name = "item" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("$.root.children[0].properties.itemVariable", json.GetProperty("declaration").GetProperty("path").GetString());
        Assert.Equal(
            ["$.root.children[0].properties.itemVariable@0+4 declaration", $"{MessagePath}@13+4 use"],
            json.GetProperty("references").EnumerateArray().Select(r =>
                $"{r.GetProperty("path").GetString()}@{r.GetProperty("start").GetInt32()}+{r.GetProperty("length").GetInt32()} {(r.GetProperty("declaration").GetBoolean() ? "declaration" : "use")}"));
        var outside = await ServerHarness.JsonAsync(unknown);
        Assert.Equal(JsonValueKind.Null, outside.GetProperty("declaration").ValueKind);
        Assert.Empty(outside.GetProperty("references").EnumerateArray());
    }

    [Theory]
    [InlineData("""{ "document": [1], "path": "$.root" }""", "scope", "'document'")]
    [InlineData("""{ "document": { "schemaVersion": "1.0" }, "path": "root" }""", "scope", "'path'")]
    [InlineData("""{ "document": { "schemaVersion": "1.0" }, "path": "$.root", "name": "1bad" }""", "references", "'name'")]
    public async Task BadRequests_AreRefused(string body, string endpoint, string expected)
    {
        await using var h = await ServerHarness.StartAsync();

        using var response = await h.SendAsync(h.Unsafe(HttpMethod.Post, $"/api/expressions/{endpoint}", rawJson: body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, (await ServerHarness.JsonAsync(response)).GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpressionAssist_NeedsTheSession_AndTheAntiForgeryHeader()
    {
        await using var h = await ServerHarness.StartAsync();
        using var stranger = ServerHarness.NewClient(h.BaseUri);

        using var noSession = await stranger.GetAsync("/api/expressions/functions", Token);
        using var withoutHeader = new HttpRequestMessage(HttpMethod.Post, "/api/expressions/scope") { Content = JsonContent.Create(new { document = _document, path = MessagePath }) };
        using var refused = await h.Client.SendAsync(withoutHeader, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
        Assert.False(refused.IsSuccessStatusCode);
    }
}
