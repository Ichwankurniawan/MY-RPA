using Microsoft.Extensions.DependencyInjection;
using MyRPA.Core.Activities;
using static MyRPA.Files.Tests.FilesHost;

namespace MyRPA.Files.Tests;

public sealed class FileActivityTests(FilesHost host) : IClassFixture<FilesHost>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WriteText_ThenReadText_RoundTripsAndExists()
    {
        var result = await host.RunAsync(
            Node("File.WriteText", """ "path": "'rt/hello.txt'", "text": "'Grüße, world'" """) + "," +
            Node("File.ReadText", """ "path": "'rt/hello.txt'", "result": "text" """) + "," +
            Node("File.Exists", """ "path": "'rt/hello.txt'", "result": "exists" """) + "," +
            Node("File.Exists", """ "path": "'rt/missing.txt'", "result": "missing" """),
            "text", "exists", "missing");

        AssertSucceeded(result);
        Assert.Equal("Grüße, world", result.Outputs["text"]);
        Assert.Equal(true, result.Outputs["exists"]);
        Assert.Equal(false, result.Outputs["missing"]);
        Assert.Equal("Grüße, world", await File.ReadAllTextAsync(host.InRoot("rt/hello.txt"), Token));
    }

    [Fact]
    public async Task WriteText_ExistingFileWithoutOverwrite_FailsAndKeepsTheFile()
    {
        Directory.CreateDirectory(host.InRoot("keep"));
        await File.WriteAllTextAsync(host.InRoot("keep/a.txt"), "original", Token);

        var refused = await host.RunAsync(Node("File.WriteText", """ "path": "'keep/a.txt'", "text": "'new'" """));
        host.AssertFailed(refused, ErrorTypes.FileAlreadyExists);
        Assert.Equal("original", await File.ReadAllTextAsync(host.InRoot("keep/a.txt"), Token));

        AssertSucceeded(await host.RunAsync(Node("File.WriteText", """ "path": "'keep/a.txt'", "text": "'new'", "overwrite": "true" """)));
        AssertSucceeded(await host.RunAsync(Node("File.WriteText", """ "path": "'keep/a.txt'", "text": "'+more'", "append": "true" """)));
        Assert.Equal("new+more", await File.ReadAllTextAsync(host.InRoot("keep/a.txt"), Token));
    }

    [Theory]
    [InlineData("File.ReadText", "'../outside/secret.txt'", ", \"result\": \"x\"")]
    [InlineData("File.WriteText", "'../outside/new.txt'", ", \"text\": \"'x'\", \"overwrite\": \"true\"")]
    [InlineData("File.Delete", "'../outside/secret.txt'", ", \"missingOk\": \"true\"")]
    [InlineData("File.Exists", "'sub/../../outside/secret.txt'", ", \"result\": \"x\"")]
    [InlineData("Folder.Create", "'../outside/made'", "")]
    public async Task PathOutsideTheRoot_IsDeniedAndNothingOutsideChanges(string type, string path, string extra)
    {
        var result = await host.RunAsync(Node(type, $$""" "path": "{{path}}"{{extra}} """), "x");

        host.AssertFailed(result, ErrorTypes.FileAccessDenied);
        Assert.Equal("secret.txt", Path.GetFileName(Assert.Single(Directory.GetFileSystemEntries(host.Outside))));
        Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(host.Outside, "secret.txt"), Token));
    }

    [Fact]
    public async Task AbsolutePathOutsideTheRoot_IsDenied()
    {
        // One backslash is four in the JSON text: JSON unescapes to two, the expression string to one.
        var outside = Path.Combine(host.Outside, "secret.txt").Replace("\\", "\\\\\\\\", StringComparison.Ordinal);
        var result = await host.RunAsync(Node("File.ReadText", $$""" "path": "'{{outside}}'", "result": "x" """), "x");

        host.AssertFailed(result, ErrorTypes.FileAccessDenied);
    }

    [Fact]
    public async Task AbsolutePathInsideTheRoot_IsAllowed()
    {
        await File.WriteAllTextAsync(host.InRoot("abs.txt"), "inside", Token);
        var inside = host.InRoot("abs.txt").Replace("\\", "\\\\\\\\", StringComparison.Ordinal);

        var result = await host.RunAsync(Node("File.ReadText", $$""" "path": "'{{inside}}'", "result": "x" """), "x");

        AssertSucceeded(result);
        Assert.Equal("inside", result.Outputs["x"]);
    }

    [Fact]
    public async Task SymbolicLinkToAFolderOutside_IsDenied()
    {
        var link = Links.TrySymbolicLink(host.InRoot("symlink"), host.Outside);
        Assert.SkipWhen(link is null, "This account may not create symbolic links (Windows without Developer Mode).");

        var read = await host.RunAsync(Node("File.ReadText", """ "path": "'symlink/secret.txt'", "result": "x" """), "x");
        var write = await host.RunAsync(Node("File.WriteText", """ "path": "'symlink/new.txt'", "text": "'x'" """));
        var list = await host.RunAsync(Node("File.List", """ "folder": "'symlink'", "result": "x" """), "x");

        host.AssertFailed(read, ErrorTypes.FileAccessDenied);
        host.AssertFailed(write, ErrorTypes.FileAccessDenied);
        host.AssertFailed(list, ErrorTypes.FileAccessDenied);
        Assert.False(File.Exists(Path.Combine(host.Outside, "new.txt")));
    }

    [Fact]
    public async Task JunctionToAFolderOutside_IsDenied()
    {
        var junction = Links.TryJunction(host.InRoot("junction"), host.Outside);
        Assert.SkipWhen(junction is null, "Junctions exist only on Windows.");

        var read = await host.RunAsync(Node("File.ReadText", """ "path": "'junction/secret.txt'", "result": "x" """), "x");
        var delete = await host.RunAsync(Node("File.Delete", """ "path": "'junction/secret.txt'" """));

        host.AssertFailed(read, ErrorTypes.FileAccessDenied);
        host.AssertFailed(delete, ErrorTypes.FileAccessDenied);
        Assert.True(File.Exists(Path.Combine(host.Outside, "secret.txt")));
    }

    [Fact]
    public async Task List_SkipsLinksAndReturnsSortedRelativePaths()
    {
        Directory.CreateDirectory(host.InRoot("list/sub"));
        foreach (var name in new[] { "list/b.csv", "list/a.csv", "list/c.txt", "list/sub/d.csv" })
        {
            await File.WriteAllTextAsync(host.InRoot(name), "x", Token);
        }

        Links.TryJunction(host.InRoot("list/link"), host.Outside);

        var result = await host.RunAsync(
            Node("File.List", """ "folder": "'list'", "pattern": "'*.csv'", "result": "flat" """) + "," +
            Node("File.List", """ "folder": "'list'", "pattern": "'*.csv'", "recursive": "true", "result": "deep" """) + "," +
            Node("File.List", """ "folder": "'list'", "kind": "Folders", "result": "folders" """),
            "flat", "deep", "folders");

        AssertSucceeded(result);
        Assert.Equal(["list/a.csv", "list/b.csv"], (IEnumerable<object?>)result.Outputs["flat"]!);
        Assert.Equal(["list/a.csv", "list/b.csv", "list/sub/d.csv"], (IEnumerable<object?>)result.Outputs["deep"]!);
        Assert.Equal(["list/sub"], (IEnumerable<object?>)result.Outputs["folders"]!);
    }

    [Fact]
    public async Task List_MoreThanMaxItems_FailsWithTooManyItems()
    {
        Directory.CreateDirectory(host.InRoot("many"));
        for (var i = 0; i <= MaxItems; i++)
        {
            await File.WriteAllTextAsync(host.InRoot($"many/{i}.txt"), "x", Token);
        }

        host.AssertFailed(await host.RunAsync(Node("File.List", """ "folder": "'many'", "result": "x" """), "x"), ErrorTypes.TooManyItems);
    }

    [Fact]
    public async Task List_PatternWithAFolder_IsRefused()
    {
        var result = await host.RunAsync(Node("File.List", """ "pattern": "'../*'", "result": "x" """), "x");

        host.AssertFailed(result, ErrorTypes.InvalidPath);
    }

    [Fact]
    public async Task ReadText_LargerThanMaxFileBytes_FailsWithFileTooLarge()
    {
        await File.WriteAllTextAsync(host.InRoot("big.txt"), new string('x', MaxFileBytes + 1), Token);

        host.AssertFailed(await host.RunAsync(Node("File.ReadText", """ "path": "'big.txt'", "result": "x" """), "x"), ErrorTypes.FileTooLarge);
    }

    [Fact]
    public async Task CopyMoveDelete_WorkOnFilesAndRefuseSilentReplacement()
    {
        Directory.CreateDirectory(host.InRoot("cmd"));
        await File.WriteAllTextAsync(host.InRoot("cmd/a.txt"), "a", Token);
        await File.WriteAllTextAsync(host.InRoot("cmd/b.txt"), "b", Token);

        AssertSucceeded(await host.RunAsync(Node("File.Copy", """ "source": "'cmd/a.txt'", "destination": "'cmd/copy/a.txt'" """)));
        host.AssertFailed(await host.RunAsync(Node("File.Copy", """ "source": "'cmd/a.txt'", "destination": "'cmd/b.txt'" """)), ErrorTypes.FileAlreadyExists);
        host.AssertFailed(await host.RunAsync(Node("File.Move", """ "source": "'cmd/a.txt'", "destination": "'cmd/b.txt'" """)), ErrorTypes.FileAlreadyExists);
        Assert.Equal("b", await File.ReadAllTextAsync(host.InRoot("cmd/b.txt"), Token));
        Assert.Equal("a", await File.ReadAllTextAsync(host.InRoot("cmd/copy/a.txt"), Token));

        AssertSucceeded(await host.RunAsync(Node("File.Move", """ "source": "'cmd/a.txt'", "destination": "'cmd/moved.txt'" """)));
        Assert.False(File.Exists(host.InRoot("cmd/a.txt")));
        Assert.Equal("a", await File.ReadAllTextAsync(host.InRoot("cmd/moved.txt"), Token));

        AssertSucceeded(await host.RunAsync(Node("File.Delete", """ "path": "'cmd/moved.txt'" """)));
        Assert.False(File.Exists(host.InRoot("cmd/moved.txt")));
        host.AssertFailed(await host.RunAsync(Node("File.Delete", """ "path": "'cmd/moved.txt'" """)), ErrorTypes.FileNotFound);
        AssertSucceeded(await host.RunAsync(Node("File.Delete", """ "path": "'cmd/moved.txt'", "missingOk": "true" """)));
    }

    [Fact]
    public async Task Delete_AFolder_IsRefusedAndTheFolderStays()
    {
        Directory.CreateDirectory(host.InRoot("folder-stays/inner"));

        host.AssertFailed(await host.RunAsync(Node("File.Delete", """ "path": "'folder-stays'" """)), ErrorTypes.InvalidPath);
        host.AssertFailed(await host.RunAsync(Node("File.Copy", """ "source": "'folder-stays'", "destination": "'x'" """)), ErrorTypes.FileNotFound);
        Assert.True(Directory.Exists(host.InRoot("folder-stays/inner")));
    }

    [Fact]
    public async Task FolderCreate_IsIdempotent()
    {
        AssertSucceeded(await host.RunAsync(
            Node("Folder.Create", """ "path": "'made/deep/er'" """) + "," + Node("Folder.Create", """ "path": "'made/deep/er'" """)));

        Assert.True(Directory.Exists(host.InRoot("made/deep/er")));
    }

    [Theory]
    [InlineData("''")]
    [InlineData("'   '")]
    public async Task EmptyPath_FailsWithInvalidPath(string path)
    {
        var result = await host.RunAsync(Node("File.ReadText", $$""" "path": "{{path}}", "result": "x" """), "x");

        host.AssertFailed(result, ErrorTypes.InvalidPath);
    }

    [Fact]
    public async Task ConcurrentRuns_DoNotShareState()
    {
        var runs = Enumerable.Range(0, 8).Select(i => host.RunAsync(
            Node("File.WriteText", $$""" "path": "'parallel/{{i}}.txt'", "text": "'run {{i}}'" """) + "," +
            Node("File.ReadText", $$""" "path": "'parallel/{{i}}.txt'", "result": "text" """),
            "text"));

        var results = await Task.WhenAll(runs);

        for (var i = 0; i < results.Length; i++)
        {
            AssertSucceeded(results[i]);
            Assert.Equal($"run {i}", results[i].Outputs["text"]);
        }
    }

    [Fact]
    public void Catalog_DescribesEveryActivityWithItsSideEffectAndTypedPaths()
    {
        var catalog = host.Services.GetRequiredService<IActivityCatalog>();
        string[] types =
        [
            "File.Exists", "File.List", "File.ReadText", "File.WriteText", "File.Copy", "File.Move", "File.Delete",
            "Folder.Create", "Csv.Read", "Csv.Write", "Json.ReadFile", "Json.WriteFile", "Xml.ReadFile",
        ];

        foreach (var type in types)
        {
            Assert.True(catalog.TryGet(new ActivityTypeName(type), out var descriptor), type);
            Assert.Equal(ActivitySideEffects.FileSystem, descriptor.SideEffects);
            var path = descriptor.Properties.First(p => p.Name is "path" or "source" or "folder");
            Assert.Equal(ActivityValueType.String, path.ValueType);
        }
    }
}
