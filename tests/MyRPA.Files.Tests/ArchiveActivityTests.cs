using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using MyRPA.Workflow.Execution;
using static MyRPA.Files.Tests.FilesHost;

namespace MyRPA.Files.Tests;

/// <summary>Zip.Create, Zip.Extract (zip slip, zip bombs, limits), File.Hash and File.WaitFor (ADR-0043).</summary>
public sealed class ArchiveActivityTests(FilesHost host) : IClassFixture<FilesHost>
{
    private static int _folders;

    /// <summary>A new folder under the root for one test (relative path).</summary>
    private string Folder()
    {
        var name = $"t{Interlocked.Increment(ref _folders)}";
        Directory.CreateDirectory(host.InRoot(name));
        return name;
    }

    private void Write(string relative, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(host.InRoot(relative))!);
        File.WriteAllText(host.InRoot(relative), text);
    }

    /// <summary>Writes a ZIP whose entries are given by name and content, exactly as named (unsafe names included).</summary>
    private void Archive(string relative, params (string Name, string Content)[] entries)
    {
        using var stream = File.Create(host.InRoot(relative));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }

    private static string[] Names(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        return [.. archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Create_FromAFolder_KeepsSubFolders_AndExtractGivesTheSameFiles()
    {
        var folder = Folder();
        Write($"{folder}/in/a.txt", "alpha");
        Write($"{folder}/in/sub/b.txt", "beta");
        Write($"{folder}/in/skip.log", "not taken");

        var created = await host.RunAsync(
            Node("Zip.Create", $$""" "source": "'{{folder}}/in'", "destination": "'{{folder}}/out.zip'", "pattern": "'*.txt'", "result": "count" """) + "," +
            Node("Zip.Extract", $$""" "source": "'{{folder}}/out.zip'", "destination": "'{{folder}}/back'", "result": "files" """),
            "count", "files");

        AssertSucceeded(created);
        Assert.Equal(2L, created.Outputs["count"]);
        Assert.Equal(["a.txt", "sub/b.txt"], Names(host.InRoot($"{folder}/out.zip")));
        Assert.Equal([$"{folder}/back/a.txt", $"{folder}/back/sub/b.txt"], ((IReadOnlyList<object?>)created.Outputs["files"]!).Cast<string>().Order(StringComparer.Ordinal));
        Assert.Equal("beta", File.ReadAllText(host.InRoot($"{folder}/back/sub/b.txt")));
        Assert.Empty(Directory.GetFiles(host.InRoot(folder), "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Create_FromAList_StoresFilesByName_AndRefusesTwoWithTheSameName()
    {
        var folder = Folder();
        Write($"{folder}/x/one.pdf", "1");
        Write($"{folder}/y/two.pdf", "2");
        Write($"{folder}/z/one.pdf", "1 again");

        var made = await host.RunAsync(Node("Zip.Create", $$""" "source": "['{{folder}}/x/one.pdf', '{{folder}}/y/two.pdf']", "destination": "'{{folder}}/list.zip'", "result": "count" """), "count");
        var clash = await host.RunAsync(Node("Zip.Create", $$""" "source": "['{{folder}}/x/one.pdf', '{{folder}}/z/one.pdf']", "destination": "'{{folder}}/clash.zip'", "result": "count" """), "count");

        AssertSucceeded(made);
        Assert.Equal(["one.pdf", "two.pdf"], Names(host.InRoot($"{folder}/list.zip")));
        host.AssertFailed(clash, "InvalidInput");
        Assert.False(File.Exists(host.InRoot($"{folder}/clash.zip")));
    }

    [Fact]
    public async Task Create_AnExistingArchive_IsReplacedOnlyWithOverwrite()
    {
        var folder = Folder();
        Write($"{folder}/a.txt", "a");
        Write($"{folder}/a.zip", "not a zip yet");

        var refused = await host.RunAsync(Node("Zip.Create", $$""" "source": "'{{folder}}/a.txt'", "destination": "'{{folder}}/a.zip'", "result": "n" """), "n");
        var replaced = await host.RunAsync(Node("Zip.Create", $$""" "source": "'{{folder}}/a.txt'", "destination": "'{{folder}}/a.zip'", "overwrite": true, "result": "n" """), "n");

        host.AssertFailed(refused, ErrorTypes.FileAlreadyExists);
        AssertSucceeded(replaced);
        Assert.Equal(["a.txt"], Names(host.InRoot($"{folder}/a.zip")));
    }

    [Fact]
    public async Task Create_MoreFilesThanMaxItems_IsRefused()
    {
        var folder = Folder();
        for (var i = 0; i <= MaxItems; i++)
        {
            Write($"{folder}/many/f{i}.txt", "x");
        }

        var result = await host.RunAsync(Node("Zip.Create", $$""" "source": "'{{folder}}/many'", "destination": "'{{folder}}/many.zip'", "result": "n" """), "n");

        host.AssertFailed(result, ErrorTypes.TooManyItems);
        Assert.False(File.Exists(host.InRoot($"{folder}/many.zip")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("deep/../../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/drive.txt")]
    [InlineData("..\\backslash.txt")]
    public async Task Extract_AnEntryLeavingTheDestination_IsRefused_AndNothingIsWritten(string evil)
    {
        var folder = Folder();
        Archive($"{folder}/slip.zip", ("fine.txt", "ok"), (evil, "owned"));

        var result = await host.RunAsync(Node("Zip.Extract", $$""" "source": "'{{folder}}/slip.zip'", "destination": "'{{folder}}/out'", "result": "files" """), "files");

        host.AssertFailed(result, ErrorTypes.FileAccessDenied);
        Assert.False(File.Exists(host.InRoot($"{folder}/out/fine.txt")));
        Assert.False(File.Exists(host.InRoot($"{folder}/escape.txt")));
        Assert.False(File.Exists(host.InRoot("escape.txt")));
        Assert.False(File.Exists(Path.Combine(host.FileRoot, "..", "escape.txt")));
    }

    [Fact]
    public async Task Extract_MoreThanMaxExtractBytes_IsRefusedBeforeWriting()
    {
        var folder = Folder();
        // Highly compressible: a small archive (under maxFileBytes) that expands past maxExtractBytes.
        Archive($"{folder}/bomb.zip", ("first.txt", "small"), ("zeros.txt", new string('0', MaxExtractBytes + 1)));
        Assert.True(new FileInfo(host.InRoot($"{folder}/bomb.zip")).Length < MaxFileBytes);

        var result = await host.RunAsync(Node("Zip.Extract", $$""" "source": "'{{folder}}/bomb.zip'", "destination": "'{{folder}}/out'", "result": "files" """), "files");

        host.AssertFailed(result, ErrorTypes.FileTooLarge);
        Assert.False(File.Exists(host.InRoot($"{folder}/out/first.txt")));
    }

    [Fact]
    public async Task Extract_MoreFilesThanMaxItems_IsRefused()
    {
        var folder = Folder();
        Archive($"{folder}/many.zip", [.. Enumerable.Range(0, MaxItems + 1).Select(i => ($"f{i}.txt", "x"))]);

        var result = await host.RunAsync(Node("Zip.Extract", $$""" "source": "'{{folder}}/many.zip'", "destination": "'{{folder}}/out'", "result": "files" """), "files");

        host.AssertFailed(result, ErrorTypes.TooManyItems);
        Assert.False(Directory.Exists(host.InRoot($"{folder}/out")) && Directory.EnumerateFiles(host.InRoot($"{folder}/out")).Any());
    }

    [Fact]
    public async Task Extract_OverAnExistingFile_NeedsOverwrite()
    {
        var folder = Folder();
        Archive($"{folder}/a.zip", ("new.txt", "fresh"), ("keep.txt", "from zip"));
        Write($"{folder}/out/keep.txt", "mine");

        var refused = await host.RunAsync(Node("Zip.Extract", $$""" "source": "'{{folder}}/a.zip'", "destination": "'{{folder}}/out'", "result": "files" """), "files");
        Assert.Equal("mine", File.ReadAllText(host.InRoot($"{folder}/out/keep.txt")));
        Assert.False(File.Exists(host.InRoot($"{folder}/out/new.txt")));

        var replaced = await host.RunAsync(Node("Zip.Extract", $$""" "source": "'{{folder}}/a.zip'", "destination": "'{{folder}}/out'", "overwrite": true, "result": "files" """), "files");

        host.AssertFailed(refused, ErrorTypes.FileAlreadyExists);
        AssertSucceeded(replaced);
        Assert.Equal("from zip", File.ReadAllText(host.InRoot($"{folder}/out/keep.txt")));
    }

    [Fact]
    public async Task Extract_AFileThatIsNotAZip_FailsAsInvalidArchive()
    {
        var folder = Folder();
        Write($"{folder}/fake.zip", "this is text");

        var result = await host.RunAsync(Node("Zip.Extract", $$""" "source": "'{{folder}}/fake.zip'", "destination": "'{{folder}}/out'", "result": "files" """), "files");

        host.AssertFailed(result, ErrorTypes.InvalidArchive);
    }

    [Fact]
    public async Task Extract_IntoAFolderOutsideTheRoot_IsRefused()
    {
        var folder = Folder();
        Archive($"{folder}/a.zip", ("a.txt", "a"));

        var result = await host.RunAsync(Node("Zip.Extract", $$""" "source": "'{{folder}}/a.zip'", "destination": "'../outside'", "result": "files" """), "files");

        host.AssertFailed(result, ErrorTypes.FileAccessDenied);
        Assert.False(File.Exists(Path.Combine(host.Outside, "a.txt")));
    }

    [Theory]
    [InlineData("SHA256")]
    [InlineData("SHA512")]
    public async Task Hash_IsTheLowerCaseHexOfTheFile(string algorithm)
    {
        var folder = Folder();
        Write($"{folder}/h.txt", "hash me");
        var bytes = Encoding.UTF8.GetBytes("hash me");
        var expected = Convert.ToHexStringLower(algorithm == "SHA512" ? SHA512.HashData(bytes) : SHA256.HashData(bytes));

        var result = await host.RunAsync(Node("File.Hash", $$""" "path": "'{{folder}}/h.txt'", "algorithm": "{{algorithm}}", "result": "hash" """), "hash");

        AssertSucceeded(result);
        Assert.Equal(expected, result.Outputs["hash"]);
    }

    [Fact]
    public async Task Hash_AMissingFile_FailsFileNotFound()
    {
        var result = await host.RunAsync(Node("File.Hash", """ "path": "'no-such-file.bin'", "result": "hash" """), "hash");

        host.AssertFailed(result, ErrorTypes.FileNotFound);
    }
}

/// <summary>File.WaitFor on the run's clock (a fake clock the test advances).</summary>
public sealed class WaitForActivityTests(ClockedFilesHost host) : IClassFixture<ClockedFilesHost>
{
    private static int _folders;

    private string Folder()
    {
        var name = $"w{Interlocked.Increment(ref _folders)}";
        Directory.CreateDirectory(host.InRoot(name));
        return name;
    }

    private void Write(string relative, string text) => File.WriteAllText(host.InRoot(relative), text);

    /// <summary>Advances <paramref name="clock"/> by <paramref name="step"/> until <paramref name="run"/> ends, calling <paramref name="each"/> before each step.</summary>
    private static async Task<WorkflowExecutionResult> DriveAsync(Task<WorkflowExecutionResult> run, FakeTimeProvider clock, TimeSpan step, Action<int>? each = null)
    {
        for (var i = 0; !run.IsCompleted && i < 1000; i++)
        {
            each?.Invoke(i);
            // Let the run reach its next wait before time moves (a real yield, not the clock under test).
            await Task.Delay(5, TestContext.Current.CancellationToken);
            clock.Advance(step);
        }

        return await run;
    }

    [Fact]
    public async Task WaitFor_AFileThatArrives_Succeeds_OnTheRunsClock()
    {
        var folder = Folder();
        var clock = host.Clock;
        var start = clock.GetUtcNow();
        var run = host.RunAsync(Node("File.WaitFor", $$""" "path": "'{{folder}}/later.csv'", "timeoutMs": 60000, "pollMs": 1000, "result": "there" """), "there");

        var result = await DriveAsync(run, clock, TimeSpan.FromSeconds(1), i =>
        {
            if (i == 3)
            {
                Write($"{folder}/later.csv", "a,b");
            }
        });

        AssertSucceeded(result);
        Assert.Equal(true, result.Outputs["there"]);
        Assert.InRange(clock.GetUtcNow() - start, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WaitFor_NoFile_FailsWithTimeout_OrGivesFalse()
    {
        var folder = Folder();
        var clock = host.Clock;
        var failing = await DriveAsync(
            host.RunAsync(Node("File.WaitFor", $$""" "path": "'{{folder}}/never.csv'", "timeoutMs": 3000, "pollMs": 1000 """)),
            clock,
            TimeSpan.FromSeconds(1));
        var quiet = await DriveAsync(
            host.RunAsync(Node("File.WaitFor", $$""" "path": "'{{folder}}/never.csv'", "timeoutMs": 3000, "pollMs": 1000, "failOnTimeout": false, "result": "there" """), "there"),
            clock,
            TimeSpan.FromSeconds(1));

        host.AssertFailed(failing, ErrorTypes.Timeout);
        AssertSucceeded(quiet);
        Assert.Equal(false, quiet.Outputs["there"]);
    }

    [Fact]
    public async Task WaitFor_WithStableMs_WaitsUntilTheFileStopsGrowing()
    {
        var folder = Folder();
        Write($"{folder}/growing.csv", "row");
        var clock = host.Clock;
        var start = clock.GetUtcNow();
        var run = host.RunAsync(Node("File.WaitFor", $$""" "path": "'{{folder}}/growing.csv'", "stableMs": 3000, "pollMs": 1000, "timeoutMs": 60000, "result": "there" """), "there");

        // The file grows for the first 6 steps, then stays the same.
        var result = await DriveAsync(run, clock, TimeSpan.FromSeconds(1), i =>
        {
            if (i < 6)
            {
                File.AppendAllText(host.InRoot($"{folder}/growing.csv"), "\nrow");
            }
        });

        AssertSucceeded(result);
        Assert.True(clock.GetUtcNow() - start >= TimeSpan.FromSeconds(8), $"stable only after the growth stopped: {clock.GetUtcNow() - start}");
    }

    [Fact]
    public async Task WaitFor_OutsideTheRoot_IsRefused()
    {
        var result = await host.RunAsync(Node("File.WaitFor", """ "path": "'../outside/secret.txt'", "timeoutMs": 10 """));

        host.AssertFailed(result, ErrorTypes.FileAccessDenied);
    }
}
