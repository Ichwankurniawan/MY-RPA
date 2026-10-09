using System.Diagnostics;
using MyRPA.Sdk.Files;
using MyRPA.Workflow.Execution;

namespace MyRPA.Sdk.Tests;

public sealed class FileRootPolicyTests : IDisposable
{
    private readonly string _parent = Directory.CreateTempSubdirectory("myrpa-policy-").FullName;
    private readonly string _root;
    private readonly FileRootPolicy _policy;

    public FileRootPolicyTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_parent, "root")).FullName;
        Directory.CreateDirectory(Path.Combine(_parent, "root2"));
        Directory.CreateDirectory(Path.Combine(_parent, "outside"));
        _policy = new FileRootPolicy(_root + Path.DirectorySeparatorChar);
    }

    public void Dispose()
    {
        try
        {
            // Links themselves first (never what they point to), then the rest.
            foreach (var link in Directory.GetDirectories(_root).Select(d => new DirectoryInfo(d)).Where(d => d.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                link.Delete();
            }

            Directory.Delete(_parent, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a temporary folder left behind does not change any result.
        }
    }

    [Fact]
    public void Constructor_MissingRoot_Throws() =>
        Assert.Throws<ArgumentException>(() => new FileRootPolicy(Path.Combine(_parent, "missing")));

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("sub/b.txt", "sub/b.txt")]
    [InlineData("sub/../c.txt", "c.txt")]
    [InlineData("./d.txt", "d.txt")]
    [InlineData(".", ".")]
    public void Resolve_PathInsideTheRoot_GivesTheFullPath(string path, string relative)
    {
        var full = _policy.Resolve(path);

        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(_root, relative))), full);
        Assert.Equal(relative, _policy.Relative(full));
    }

    [Theory]
    [InlineData("../outside/x.txt")]
    [InlineData("sub/../../outside/x.txt")]
    [InlineData("../root2/x.txt")]
    [InlineData("..")]
    public void Resolve_PathOutsideTheRoot_IsDeniedWithoutNamingTheRoot(string path)
    {
        var ex = Assert.Throws<ActivityFailedException>(() => _policy.Resolve(path));

        Assert.Equal(FileErrorTypes.FileAccessDenied, ex.ErrorType);
        Assert.DoesNotContain(_root, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_AbsolutePathOutside_IsDenied()
    {
        var ex = Assert.Throws<ActivityFailedException>(() => _policy.Resolve(Path.Combine(_parent, "outside", "x.txt")));

        Assert.Equal(FileErrorTypes.FileAccessDenied, ex.ErrorType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a\0b")]
    public void Resolve_EmptyOrInvalidPath_FailsWithInvalidPath(string path) =>
        Assert.Equal(FileErrorTypes.InvalidPath, Assert.Throws<ActivityFailedException>(() => _policy.Resolve(path)).ErrorType);

    [Fact]
    public void Resolve_ThroughASymbolicLink_IsDenied()
    {
        var link = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(_parent, "outside"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account may not create symbolic links (Windows without Developer Mode).");
        }

        Assert.Equal(FileErrorTypes.FileAccessDenied, Assert.Throws<ActivityFailedException>(() => _policy.Resolve("link/x.txt")).ErrorType);
        Assert.Equal(FileErrorTypes.FileAccessDenied, Assert.Throws<ActivityFailedException>(() => _policy.Resolve("link")).ErrorType);
    }

    [Fact]
    public void Resolve_ThroughAJunction_IsDenied()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions exist only on Windows.");
        var link = Path.Combine(_root, "junction");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, Path.Combine(_parent, "outside")])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!)
        {
            process.WaitForExit();
        }

        Assert.True(Directory.Exists(link));
        Assert.Equal(FileErrorTypes.FileAccessDenied, Assert.Throws<ActivityFailedException>(() => _policy.Resolve("junction/x.txt")).ErrorType);
    }

    [Fact]
    public void ResolveFileToWrite_RefusesExistingFilesUnlessOverwriteAndRefusesFolders()
    {
        File.WriteAllText(Path.Combine(_root, "exists.txt"), "x");

        Assert.Equal(FileErrorTypes.FileAlreadyExists, Assert.Throws<ActivityFailedException>(() => _policy.ResolveFileToWrite("exists.txt", overwrite: false)).ErrorType);
        Assert.Equal(Path.Combine(_root, "exists.txt"), _policy.ResolveFileToWrite("exists.txt", overwrite: true));
        Assert.Equal(FileErrorTypes.InvalidPath, Assert.Throws<ActivityFailedException>(() => _policy.ResolveFileToWrite(".", overwrite: true)).ErrorType);

        var created = _policy.ResolveFileToWrite("new/folder/file.txt", overwrite: false);
        Assert.True(Directory.Exists(Path.GetDirectoryName(created)));
        Assert.False(File.Exists(created));
    }

    [Fact]
    public void ResolveExisting_MissingTargets_FailWithFileNotFound()
    {
        Assert.Equal(FileErrorTypes.FileNotFound, Assert.Throws<ActivityFailedException>(() => _policy.ResolveExistingFile("none.txt")).ErrorType);
        Assert.Equal(FileErrorTypes.FileNotFound, Assert.Throws<ActivityFailedException>(() => _policy.ResolveExistingFolder("none")).ErrorType);
        Assert.Equal(_policy.Root, _policy.ResolveExistingFolder("."));
    }
}
