namespace MyRPA.Architecture.Tests;

/// <summary>
/// The browser plugin's page-scripting rule (ADR-0017, ADR-0039): no code is evaluated in pages; only the recorder adds
/// its fixed script and binding to the pages of recording sessions. A source scan, because the plugin is never compiled
/// against by tests.
/// </summary>
public sealed class BrowserPluginRulesTests
{
    private static string PluginFolder => Path.Combine(Repository.Root, "plugins", "MyRPA.Browser.Playwright");

    [Fact]
    public void BrowserPlugin_UsesPageScriptingOnlyInTheRecorder()
    {
        var files = Directory.EnumerateFiles(PluginFolder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);

        var (recorderFile, allowed) = ArchitectureRules.RecorderScriptException;
        var violations = new List<string>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(PluginFolder, file).Replace('\\', '/');
            var used = ArchitectureRules.FindPageScripting(File.ReadAllText(file));
            var permitted = relative == recorderFile ? allowed : [];
            violations.AddRange(used.Except(permitted).Select(api => $"{relative}: {api}"));
        }

        Assert.Empty(violations);
        Assert.Equal(allowed, ArchitectureRules.FindPageScripting(File.ReadAllText(Path.Combine(PluginFolder, recorderFile))));
    }

    [Fact]
    public void PageScriptingDetector_FindsKnownBadCalls()
    {
        Assert.Equal(["EvaluateAsync"], ArchitectureRules.FindPageScripting("var x = await page.EvaluateAsync(\"1 + 1\");"));
        Assert.Equal(["EvaluateAsync"], ArchitectureRules.FindPageScripting("await locator.EvaluateAsync<int>(\"e => 1\");"));
        Assert.Equal(["AddInitScriptAsync", "ExposeBindingAsync"], ArchitectureRules.FindPageScripting("await c.AddInitScriptAsync(s); await c.ExposeBindingAsync(n, f);"));
        Assert.Empty(ArchitectureRules.FindPageScripting("await locator.ClickAsync(); // EvaluateAsync is mentioned in a comment without a call"));
    }
}
