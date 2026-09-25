namespace MyRPA.Plugins.Tests;

public sealed class PluginConfigurationFileTests
{
    private static readonly string _base = Path.Combine(Path.GetTempPath(), "myrpa-config-base");

    [Fact]
    public void Parse_ReadsEveryOption_AndResolvesDirectoriesAgainstTheFile()
    {
        var options = PluginConfigurationFile.Parse(
            """
            {
              // comments are allowed
              "pluginConfigVersion": "1.0",
              "requireIntegrity": true,
              "deniedCapabilities": [ "filesystem.write" ],
              "plugins": [
                { "directory": "plugins/browser", "sha256": "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789",
                  "required": false, "settings": { "headless": "true" } },
                { "directory": "other" },
              ]
            }
            """,
            _base);

        Assert.True(options.RequireIntegrity);
        Assert.Equal(["filesystem.write"], options.DeniedCapabilities);
        Assert.Equal(2, options.Sources.Count);
        var browser = options.Sources[0];
        Assert.Equal(Path.GetFullPath(Path.Combine(_base, "plugins", "browser")), browser.Directory);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", browser.Sha256);
        Assert.False(browser.Required);
        Assert.Equal("true", browser.Settings["headless"]);
        Assert.True(options.Sources[1].Required); // required by default
        Assert.Null(options.Sources[1].Sha256);
    }

    [Fact]
    public void Parse_AbsoluteDirectory_IsKept()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "somewhere", "plugin");
        var options = PluginConfigurationFile.Parse(
            $$"""{ "pluginConfigVersion": "1.0", "plugins": [ { "directory": {{System.Text.Json.JsonSerializer.Serialize(absolute)}} } ] }""",
            _base);

        Assert.Equal(absolute, options.Sources.Single().Directory);
    }

    [Theory]
    [InlineData("""{ "plugins": [] }""", "pluginConfigVersion")]
    [InlineData("""{ "pluginConfigVersion": "2.0" }""", "pluginConfigVersion")]
    [InlineData("""{ "pluginConfigVersion": "1.0", "requireIntegrty": true }""", "requireIntegrty is not a known property")]
    [InlineData("""{ "pluginConfigVersion": "1.0", "requireIntegrity": "yes" }""", "true or false")]
    [InlineData("""{ "pluginConfigVersion": "1.0", "plugins": [ { } ] }""", "$.plugins[0].directory is required")]
    [InlineData("""{ "pluginConfigVersion": "1.0", "plugins": [ { "directory": " " } ] }""", "must not be empty")]
    [InlineData("""{ "pluginConfigVersion": "1.0", "plugins": [ { "directory": "a", "sha256": "abc" } ] }""", "64 hexadecimal")]
    [InlineData("""{ "pluginConfigVersion": "1.0", "plugins": [ { "directory": "a", "settings": { "n": 5 } } ] }""", "settings.n must be a JSON string")]
    [InlineData("""{ "pluginConfigVersion": "1.0", "plugins": [ { "directory": "a", "pin": "x" } ] }""", "pin is not a known property")]
    [InlineData("""{ "pluginConfigVersion": "1.0", "plugins": [ { "directory": "a" }, { "directory": "./a" } ] }""", "more than once")]
    [InlineData("""{ "pluginConfigVersion": "1.0", "deniedCapabilities": "all" }""", "must be a JSON array")]
    [InlineData("""[]""", "$ must be a JSON object")]
    [InlineData("""{ "pluginConfigVersion": """, "invalid JSON")]
    public void Parse_InvalidConfiguration_IsRejectedWithAClearMessage(string json, string expected)
    {
        var ex = Assert.Throws<PluginConfigurationException>(() => PluginConfigurationFile.Parse(json, _base, "cfg.json"));

        Assert.StartsWith("cfg.json: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_IsAConfigurationError()
    {
        var path = Path.Combine(Path.GetTempPath(), "myrpa-missing-" + Guid.NewGuid().ToString("N") + ".json");

        var ex = await Assert.ThrowsAsync<PluginConfigurationException>(() => PluginConfigurationFile.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("cannot be read", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_ConfiguredPlugin_LoadsWithItsPin()
    {
        using var staged = StagedPlugin.Sample();
        string digest;
        await using (var probe = await PluginTestHost.LoadAsync(new PluginSource { Directory = staged.Directory }))
        {
            digest = probe.Plugins.Single().Digest;
        }

        var configPath = Path.Combine(staged.Directory, "..", Path.GetFileName(staged.Directory) + ".config.json");
        await File.WriteAllTextAsync(
            configPath,
            $$"""
            { "pluginConfigVersion": "1.0", "requireIntegrity": true,
              "plugins": [ { "directory": "{{Path.GetFileName(staged.Directory)}}", "sha256": "{{digest}}", "settings": { "echoPrefix": ">> " } } ] }
            """,
            TestContext.Current.CancellationToken);
        try
        {
            var options = await PluginConfigurationFile.LoadAsync(configPath, TestContext.Current.CancellationToken);
            await using var plugins = await PluginLoader.LoadAsync(options, TestContext.Current.CancellationToken);

            Assert.False(plugins.HasRequiredFailures, string.Join(Environment.NewLine, plugins.Diagnostics));
            Assert.Equal(staged.Directory, Assert.Single(plugins.Plugins).Directory);
            Assert.Equal(">> ", options.Sources.Single().Settings["echoPrefix"]);
        }
        finally
        {
            File.Delete(configPath);
        }
    }
}
