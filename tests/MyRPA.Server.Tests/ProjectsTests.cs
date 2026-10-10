using System.Net;
using System.Text.Json;

namespace MyRPA.Server.Tests;

/// <summary>The projects folder: listing, creating and deleting projects (ADR-0046).</summary>
public sealed class ProjectsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A server with its usual --project and a projects folder holding <paramref name="folders"/>.</summary>
    private static async Task<(ServerHarness Harness, DirectoryInfo Root)> StartWithRootAsync(params string[] folders)
    {
        var root = Directory.CreateTempSubdirectory("myrpa-projects-");
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, folder));
        }

        return (await ServerHarness.StartAsync(o => o with { ProjectsRoot = root.FullName }), root);
    }

    private static async Task<JsonElement> ProjectsAsync(ServerHarness h)
    {
        using var response = await h.Client.GetAsync("/api/projects", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ServerHarness.JsonAsync(response);
    }

    private static List<(string? Name, bool Removable)> Listed(JsonElement projects) =>
        [.. projects.GetProperty("projects").EnumerateArray().Select(p => (p.GetProperty("name").GetString(), p.GetProperty("removable").GetBoolean()))];

    [Fact]
    public async Task Projects_AreTheCommandLineFolders_ThenTheProjectsFolderByName_WithoutHiddenOrTrash()
    {
        var (h, root) = await StartWithRootAsync("Zeta", "alpha", ".trash", ".hidden", "bin");
        await using var _ = h;
        try
        {
            // A folder of the projects folder named like a --project is left out: the --project wins.
            Directory.CreateDirectory(Path.Combine(root.FullName, h.ProjectName));
            var projects = await ProjectsAsync(h);
            using var info = await h.Client.GetAsync("/api/info", Token);

            Assert.Equal([(h.ProjectName, false), ("alpha", true), ("Zeta", true)], Listed(projects));
            Assert.Equal(root.FullName, projects.GetProperty("projectsRoot").GetString());
            Assert.Equal([h.ProjectName, "alpha", "Zeta"], (await ServerHarness.JsonAsync(info)).GetProperty("projects").EnumerateArray().Select(p => p.GetString()));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CreateProject_MakesAnEmptyFolder_ThatIsAProjectAtOnce_AndNeverOverAnExistingName()
    {
        var (h, root) = await StartWithRootAsync("taken");
        await using var _ = h;
        try
        {
            Task<HttpResponseMessage> Create(string name) => h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/projects", new { name }));

            using var created = await Create("Invoices 2026");
            using var save = h.Unsafe(HttpMethod.Put, "/api/projects/Invoices%202026/workflows/main.json", rawJson: ServerHarness.Logs(1));
            save.Headers.Add("If-None-Match", "*");
            using var saved = await h.SendAsync(save);
            using var taken = await Create("TAKEN");
            using var commandLine = await Create(h.ProjectName);

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("Invoices 2026", (await ServerHarness.JsonAsync(created)).GetProperty("name").GetString());
            Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
            Assert.True(File.Exists(Path.Combine(root.FullName, "Invoices 2026", "main.json")));
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, commandLine.StatusCode);
            Assert.Contains(("Invoices 2026", true), Listed(await ProjectsAsync(h)));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" padded")]
    [InlineData(".hidden")]
    [InlineData("ends.")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("what?")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    public async Task CreateProject_RefusesNamesThatAreNotOneSafeFolder(string name)
    {
        var (h, root) = await StartWithRootAsync();
        await using var _ = h;
        try
        {
            using var response = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/projects", new { name }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.StartsWith("'name': ", (await ServerHarness.JsonAsync(response)).GetProperty("error").GetString(), StringComparison.Ordinal);
            Assert.Empty(root.GetFileSystemInfos());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CreateProject_WithoutAProjectsFolder_IsRefused()
    {
        await using var h = await ServerHarness.StartAsync();

        using var response = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/projects", new { name = "new" }));
        var projects = await ProjectsAsync(h);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("--projects-root", (await ServerHarness.JsonAsync(response)).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, projects.GetProperty("projectsRoot").ValueKind);
        Assert.Equal([(h.ProjectName, false)], Listed(projects));
    }

    [Fact]
    public async Task DeleteProject_MovesItsFolderToTheTrash_WithEveryFile()
    {
        var (h, root) = await StartWithRootAsync("old");
        await using var _ = h;
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "old", "main.json"), ServerHarness.Logs(1));

            using var deleted = await h.SendAsync(h.Unsafe(HttpMethod.Delete, "/api/projects/old"));
            using var again = await h.SendAsync(h.Unsafe(HttpMethod.Delete, "/api/projects/old"));

            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            var trash = (await ServerHarness.JsonAsync(deleted)).GetProperty("trash").GetString()!;
            Assert.Matches(@"^\.trash/old-\d{8}-\d{6}$", trash);
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "old")));
            Assert.True(File.Exists(Path.Combine(root.FullName, trash, "main.json")));
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
            Assert.DoesNotContain(Listed(await ProjectsAsync(h)), p => p.Name == "old");

            // The name is free again, and a second delete in the same second gets its own trash folder.
            using var recreated = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/projects", new { name = "old" }));
            using var deletedAgain = await h.SendAsync(h.Unsafe(HttpMethod.Delete, "/api/projects/old"));
            Assert.Equal(HttpStatusCode.Created, recreated.StatusCode);
            Assert.Equal(HttpStatusCode.OK, deletedAgain.StatusCode);
            Assert.Equal(2, Directory.GetDirectories(Path.Combine(root.FullName, ".trash")).Length);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DeleteProject_NeverDeletesACommandLineProject()
    {
        var (h, root) = await StartWithRootAsync();
        await using var _ = h;
        try
        {
            h.WriteWorkflow("a.json", ServerHarness.Logs(1));

            using var response = await h.SendAsync(h.Unsafe(HttpMethod.Delete, $"/api/projects/{h.ProjectName}"));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("--project", (await ServerHarness.JsonAsync(response)).GetProperty("error").GetString(), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(h.ProjectRoot, "a.json")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CreateAndDelete_NeedTheAntiForgeryHeader()
    {
        var (h, root) = await StartWithRootAsync("keep");
        await using var _ = h;
        try
        {
            using var create = h.Unsafe(HttpMethod.Post, "/api/projects", new { name = "x" });
            create.Headers.Remove("X-MyRPA-Request");
            using var delete = h.Unsafe(HttpMethod.Delete, "/api/projects/keep");
            delete.Headers.Remove("X-MyRPA-Request");

            using var created = await h.SendAsync(create);
            using var deleted = await h.SendAsync(delete);

            Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "x")));
            Assert.True(Directory.Exists(Path.Combine(root.FullName, "keep")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void CommandLine_ProjectsRoot_IsCreated_IsEnoughAlone_AndTheDefaultAppliesOnlyWhenNothingIsNamed()
    {
        var folder = Directory.CreateTempSubdirectory("myrpa-cli-root-");
        try
        {
            var root = Path.Combine(folder.FullName, "Laconi Projects");
            var fallback = Path.Combine(folder.FullName, "default");

            var alone = ServerCommandLine.Parse(["--projects-root", root], out var error);
            var named = ServerCommandLine.Parse(["--project", folder.FullName], out _, defaultProjectsRoot: fallback);
            Assert.False(Directory.Exists(fallback));
            var none = ServerCommandLine.Parse([], out _, defaultProjectsRoot: fallback);

            Assert.Null(error);
            Assert.Equal(root, alone!.ProjectsRoot);
            Assert.Empty(alone.Projects);
            Assert.True(Directory.Exists(root));
            Assert.Null(named!.ProjectsRoot);
            Assert.Equal(fallback, none!.ProjectsRoot);
            Assert.True(Directory.Exists(fallback));
            Assert.Null(ServerCommandLine.Parse(["--projects-root", root, "--projects-root", root], out var twice));
            Assert.Contains("once", twice, StringComparison.Ordinal);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
