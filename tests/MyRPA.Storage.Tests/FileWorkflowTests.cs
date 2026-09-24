using System.Diagnostics.CodeAnalysis;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Validation;

namespace MyRPA.Storage.Tests;

/// <summary>File loading and the confinement rules of <see cref="FileWorkflowResolver"/> (ADR-0012).</summary>
public sealed class FileWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "myrpa-storage-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WorkflowFileLoader _files = new(new WorkflowLoader(new SequenceOnlyCatalog()));

    public FileWorkflowTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "app", "sub"));
        Write("app/main.json", Workflow("main"));
        Write("app/sibling.json", Workflow("sibling"));
        Write("app/sub/child.json", Workflow("child"));
        Write("outside.json", Workflow("outside"));
        Write("app/notes.txt", Workflow("notes"));
        Write("app/broken.json", "{ \"schemaVersion\": \"1.0\" }");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Workflow(string id) =>
        $$"""{ "schemaVersion": "1.0", "id": "{{id}}", "name": "W", "version": "1", "root": { "id": "r", "type": "Core.Sequence" } }""";

    private string PathOf(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relative, string content) => File.WriteAllText(PathOf(relative), content);

    private Task<Workflow.Execution.WorkflowResolution> Resolve(FileWorkflowResolver resolver, string reference) =>
        resolver.ResolveAsync(reference, PathOf("app/main.json"), PathOf("app/main.json"), TestContext.Current.CancellationToken).AsTask();

    [Fact]
    public async Task Load_ValidFile_ReturnsWorkflowAndFullPath()
    {
        var result = await _files.LoadAsync(PathOf("app/main.json"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("main", result.Workflow.Id.Value);
        Assert.Equal(PathOf("app/main.json"), result.Location);
    }

    [Fact]
    public async Task Load_InvalidFile_ReturnsDiagnostics()
    {
        var result = await _files.LoadAsync(PathOf("app/broken.json"), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Equal("The workflow is invalid.", result.Error);
    }

    [Theory]
    [InlineData("app/missing.json", "does not exist")]
    [InlineData("app/notes.txt", "not a .json file")]
    public async Task Load_FileProblems_AreReportedWithoutDiagnostics(string relative, string expected)
    {
        var result = await _files.LoadAsync(PathOf(relative), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_OversizedFile_IsRejected()
    {
        await using (var stream = File.Create(PathOf("app/huge.json")))
        {
            stream.SetLength(WorkflowFileLoader.MaxFileSizeBytes + 1);
        }

        var result = await _files.LoadAsync(PathOf("app/huge.json"), TestContext.Current.CancellationToken);

        Assert.Contains("larger than 5 MB", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sibling.json", "sibling")]
    [InlineData("sub/child.json", "child")]
    [InlineData("sub/../sibling.json", "sibling")]
    public async Task Resolve_RelativePathsInsideRoot_Succeed(string reference, string expectedId)
    {
        var result = await Resolve(new FileWorkflowResolver(_files), reference);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(expectedId, result.Workflow.Id.Value);
    }

    [Fact]
    public async Task Resolve_FromNestedWorkflow_IsRelativeToThatWorkflow()
    {
        var resolver = new FileWorkflowResolver(_files);

        var result = await resolver.ResolveAsync("../sibling.json", PathOf("app/sub/child.json"), PathOf("app/main.json"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("sibling", result.Workflow.Id.Value);
    }

    [Theory]
    [InlineData("../outside.json", "outside the workflow root")]
    [InlineData("sub/../../outside.json", "outside the workflow root")]
    [InlineData("C:/Windows/win.ini", "must be a relative path")]
    [InlineData("/etc/passwd", "must be a relative path")]
    [InlineData("notes.txt", "not a .json file")]
    public async Task Resolve_EscapesAndAbsolutePaths_AreRejected(string reference, string expected)
    {
        var result = await Resolve(new FileWorkflowResolver(_files), reference);

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_CachesPerResolverInstance_NotGlobally()
    {
        var first = new FileWorkflowResolver(_files);
        var cached = await Resolve(first, "sibling.json");

        Write("app/sibling.json", Workflow("changed"));

        Assert.Same(cached, await Resolve(first, "sibling.json"));
        Assert.Equal("changed", (await Resolve(new FileWorkflowResolver(_files), "sibling.json")).Workflow!.Id.Value);
    }

    private sealed class SequenceOnlyCatalog : IActivityCatalog
    {
        private static readonly ActivityDescriptor _sequence = new(new ActivityTypeName("Core.Sequence"), "Sequence", "Test", allowsChildren: true);

        public IReadOnlyList<ActivityDescriptor> Descriptors { get; } = [_sequence];

        public bool TryGet(ActivityTypeName typeName, [NotNullWhen(true)] out ActivityDescriptor? descriptor)
        {
            descriptor = typeName == _sequence.TypeName ? _sequence : null;
            return descriptor is not null;
        }
    }
}
