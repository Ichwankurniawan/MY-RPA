using System.Text;
using System.Text.Json.Nodes;

namespace MyRPA.Orchestration.Tests;

public sealed class JobTransitionsTests
{
    [Theory]
    [InlineData(JobState.Queued, JobState.Assigned)]
    [InlineData(JobState.Queued, JobState.Cancelled)]
    [InlineData(JobState.Assigned, JobState.Running)]
    [InlineData(JobState.Assigned, JobState.Queued)]
    [InlineData(JobState.Assigned, JobState.Failed)]
    [InlineData(JobState.Running, JobState.Succeeded)]
    [InlineData(JobState.Running, JobState.Interrupted)]
    [InlineData(JobState.Interrupted, JobState.Succeeded)]
    public void IsAllowed_LegalTransition_IsTrue(JobState from, JobState to) => Assert.True(JobTransitions.IsAllowed(from, to));

    [Theory]
    [InlineData(JobState.Queued, JobState.Running)]
    [InlineData(JobState.Queued, JobState.Succeeded)]
    [InlineData(JobState.Running, JobState.Queued)]
    [InlineData(JobState.Running, JobState.Assigned)]
    [InlineData(JobState.Interrupted, JobState.Queued)]
    [InlineData(JobState.Interrupted, JobState.Running)]
    [InlineData(JobState.Succeeded, JobState.Running)]
    [InlineData(JobState.Failed, JobState.Queued)]
    [InlineData(JobState.Cancelled, JobState.Succeeded)]
    [InlineData(JobState.TimedOut, JobState.Failed)]
    public void IsAllowed_IllegalTransition_IsFalse(JobState from, JobState to) => Assert.False(JobTransitions.IsAllowed(from, to));

    [Fact]
    public void FinalStates_HaveNoWayOut()
    {
        foreach (var state in Enum.GetValues<JobState>().Where(JobTransitions.IsFinal))
        {
            Assert.Empty(JobTransitions.Next(state));
        }

        Assert.Equal([JobState.Succeeded, JobState.Failed, JobState.Cancelled, JobState.TimedOut], Enum.GetValues<JobState>().Where(JobTransitions.IsFinal));
    }

    [Theory]
    [InlineData("Succeeded", true)]
    [InlineData("TimedOut", true)]
    [InlineData("Running", false)]
    [InlineData("Queued", false)]
    [InlineData(null, false)]
    public void TryParseOutcome_AcceptsOnlyOutcomes(string? status, bool expected) => Assert.Equal(expected, JobTransitions.TryParseOutcome(status, out _));
}

public sealed class CronExpressionTests
{
    private static readonly TimeZoneInfo _utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo _amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    [Theory]
    [InlineData("* * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("5-1 * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("a * * * *")]
    public void TryParse_InvalidExpression_IsRefusedWithAReason(string text)
    {
        Assert.False(CronExpression.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Next_EveryFifteenMinutes_IsTheNextQuarter()
    {
        Assert.True(CronExpression.TryParse("*/15 * * * *", out var cron, out _));

        Assert.Equal(At(2026, 10, 12, 8, 15), cron.Next(At(2026, 10, 12, 8, 0), _utc));
        Assert.Equal(At(2026, 10, 12, 8, 15), cron.Next(At(2026, 10, 12, 8, 7), _utc));
        Assert.Equal(At(2026, 10, 12, 9, 0), cron.Next(At(2026, 10, 12, 8, 45), _utc));
    }

    [Fact]
    public void Next_WeekdaysInAZone_UsesLocalTimeAndNames()
    {
        Assert.True(CronExpression.TryParse("0 9 * * MON-FRI", out var cron, out _));

        // Friday 16 October 2026 09:00 CEST is 07:00 UTC; after it comes Monday 19 October.
        Assert.Equal(At(2026, 10, 16, 7, 0), cron.Next(At(2026, 10, 16, 6, 0), _amsterdam));
        Assert.Equal(At(2026, 10, 19, 7, 0), cron.Next(At(2026, 10, 16, 7, 0), _amsterdam));
    }

    [Fact]
    public void Next_BothDayFieldsRestricted_MatchesEither()
    {
        // The 1st of the month or any Sunday (classic cron).
        Assert.True(CronExpression.TryParse("0 0 1 * SUN", out var cron, out _));

        Assert.Equal(At(2026, 10, 18, 0, 0), cron.Next(At(2026, 10, 12, 0, 0), _utc));
        Assert.Equal(At(2026, 11, 1, 0, 0), cron.Next(At(2026, 10, 25, 0, 0), _utc));
    }

    [Fact]
    public void Next_SkippedLocalTime_FiresOnceAtTheEndOfTheGap()
    {
        // 29 March 2026: Amsterdam skips 02:00–03:00. 02:30 does not exist; it fires at 03:00 CEST (01:00 UTC).
        Assert.True(CronExpression.TryParse("30 2 * * *", out var cron, out _));

        var fire = cron.Next(At(2026, 3, 28, 23, 0), _amsterdam);
        Assert.Equal(At(2026, 3, 29, 1, 0), fire);
        Assert.Equal(At(2026, 3, 30, 0, 30), cron.Next(fire!.Value, _amsterdam));
    }

    [Fact]
    public void Next_RepeatedLocalTime_FiresOnceAtTheFirstOccurrence()
    {
        // 25 October 2026: Amsterdam repeats 02:00–03:00. 02:30 CEST (00:30 UTC) fires; 02:30 CET (01:30 UTC) does not.
        Assert.True(CronExpression.TryParse("30 2 * * *", out var cron, out _));

        var fire = cron.Next(At(2026, 10, 24, 23, 0), _amsterdam);
        Assert.Equal(At(2026, 10, 25, 0, 30), fire);
        Assert.Equal(At(2026, 10, 26, 1, 30), cron.Next(fire!.Value, _amsterdam));
    }

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}

public sealed class ScheduleTimesTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Once_FiresAtItsTimeOnly()
    {
        var schedule = Schedule(ScheduleKind.Once) with { StartAt = _now.AddHours(1) };

        Assert.Equal(_now.AddHours(1), ScheduleTimes.Next(schedule, _now));
        Assert.Null(ScheduleTimes.Next(schedule, _now.AddHours(1)));
    }

    [Fact]
    public void Interval_FiresOnItsGridFromTheStart()
    {
        var schedule = Schedule(ScheduleKind.Interval) with { IntervalMinutes = 30, StartAt = _now };

        Assert.Equal(_now, ScheduleTimes.Next(schedule, _now.AddMinutes(-5)));
        Assert.Equal(_now.AddMinutes(30), ScheduleTimes.Next(schedule, _now));
        Assert.Equal(_now.AddMinutes(90), ScheduleTimes.Next(schedule, _now.AddMinutes(61)));
    }

    [Fact]
    public void Cron_WithAStartTime_FiresFromThenOn()
    {
        var schedule = Schedule(ScheduleKind.Cron) with { Cron = "0 * * * *", StartAt = _now.AddDays(1) };

        Assert.Equal(_now.AddDays(1), ScheduleTimes.Next(schedule, _now));
    }

    [Theory]
    [InlineData(ScheduleKind.Cron, "not cron", null, "Europe/Amsterdam")]
    [InlineData(ScheduleKind.Cron, "0 * * * *", null, "Mars/Olympus")]
    [InlineData(ScheduleKind.Interval, null, 0, "UTC")]
    [InlineData(ScheduleKind.Once, null, null, "UTC")]
    public void Validate_BadDefinition_GivesAReason(ScheduleKind kind, string? cron, int? interval, string zone) =>
        Assert.NotNull(ScheduleTimes.Validate(Schedule(kind) with { Cron = cron, IntervalMinutes = interval, TimeZone = zone }));

    private static ScheduleRecord Schedule(ScheduleKind kind) => new()
    {
        ScheduleId = "s",
        ProcessId = "p",
        EntryPath = "main.json",
        Name = "test",
        Kind = kind,
        TimeZone = "UTC",
        CreatedAt = _now,
        CreatedBy = "test",
    };
}

public sealed class WorkflowPackageTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_IncludesValidWorkflows_LeavesOthersOut_AndRecordsDependencies()
    {
        var result = Build(
            ("main.json", Workflows.WithPlugin()),
            ("sub/hello.json", Workflows.Hello()),
            ("data.json", "{\"not\":\"a workflow\"}"));

        Assert.True(result.Succeeded, string.Join(" ", result.Errors));
        var manifest = result.Manifest!;
        Assert.Equal(["main.json", "sub/hello.json"], manifest.Files.Select(f => f.Path));
        Assert.Equal(["data.json: not a valid workflow"], manifest.Excluded);
        Assert.Contains(TestCatalog.PluginActivity, manifest.Activities);
        Assert.Equal([new PackagePlugin("Demo.Plugin", "1.2.0")], manifest.Plugins);
        Assert.Equal(WorkflowPackage.Sha256Hex(result.Content), result.Sha256);
    }

    [Fact]
    public void Build_InvalidEntry_IsRefused()
    {
        var result = Build(("main.json", "{\"schemaVersion\":\"1.0\"}"));

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Build_RecordsSecretArguments()
    {
        var result = Build(("main.json", Workflows.WithSecret()));

        Assert.True(Assert.Single(Assert.Single(result.Manifest!.EntryPoints).Arguments).Secret);
    }

    [Fact]
    public void TryVerify_TheBuiltPackage_Succeeds()
    {
        var result = Build(("main.json", Workflows.Hello()));

        Assert.True(WorkflowPackage.TryVerify(result.Content, result.Sha256, out var document, out var error), error);
        Assert.Equal("main.json", Assert.Single(document!.Manifest.EntryPoints).Path);
    }

    [Fact]
    public void TryVerify_APackageOfAnotherFormat_SaysWhichFormats()
    {
        var result = Build(("main.json", Workflows.Hello()));
        var node = JsonNode.Parse(result.Content)!;
        node["format"] = "myrpa-package/9";
        var other = Encoding.UTF8.GetBytes(node.ToJsonString());

        Assert.Equal("myrpa-package/9", WorkflowPackage.ReadFormat(other));
        Assert.False(WorkflowPackage.TryVerify(other, WorkflowPackage.Sha256Hex(other), out _, out var error));
        Assert.Equal("The package uses format myrpa-package/9; this Runner reads myrpa-package/2. Update the Runner and the Server to the same version.", error);
        Assert.Null(WorkflowPackage.ReadFormat("not json"u8));
    }

    [Fact]
    public void TryVerify_AnotherHash_IsRefused()
    {
        var result = Build(("main.json", Workflows.Hello()));

        Assert.False(WorkflowPackage.TryVerify(result.Content, new string('0', 64), out _, out var error));
        Assert.Contains("hash", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryVerify_ATamperedFile_IsRefusedEvenWithAMatchingPackageHash()
    {
        var result = Build(("main.json", Workflows.Hello()));
        var node = JsonNode.Parse(result.Content)!;
        node["files"]![0]!["content"] = Workflows.Hello(name: "Tampered");
        var tampered = Encoding.UTF8.GetBytes(node.ToJsonString());

        Assert.False(WorkflowPackage.TryVerify(tampered, WorkflowPackage.Sha256Hex(tampered), out _, out var error));
        Assert.Contains("does not match", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryVerify_APathLeavingThePackage_IsRefused()
    {
        var result = Build(("main.json", Workflows.Hello()));
        var node = JsonNode.Parse(result.Content)!;
        node["files"]![0]!["path"] = "../main.json";
        node["manifest"]!["files"]![0]!["path"] = "../main.json";
        var bad = Encoding.UTF8.GetBytes(node.ToJsonString());

        Assert.False(WorkflowPackage.TryVerify(bad, WorkflowPackage.Sha256Hex(bad), out _, out _));
    }

    [Theory]
    [InlineData("main.json", true)]
    [InlineData("sub/a.json", true)]
    [InlineData("../a.json", false)]
    [InlineData("/a.json", false)]
    [InlineData("C:/a.json", false)]
    [InlineData("a\\b.json", false)]
    [InlineData("a//b.json", false)]
    [InlineData("./a.json", false)]
    public void IsSafePath_OnlyRelativePathsInsideThePackage(string path, bool safe) => Assert.Equal(safe, WorkflowPackage.IsSafePath(path));

    [Theory]
    [InlineData("1.2.0", "1.1.0", true)]
    [InlineData("1.2.0", "1.2.0", true)]
    [InlineData("1.0.0", "1.1.0", false)]
    [InlineData("1.2.0-beta", "1.2.0", true)]
    [InlineData("2.0", "10.0", false)]
    public void Satisfies_SameOrNewerVersion(string installed, string required, bool expected) => Assert.Equal(expected, WorkflowPackage.Satisfies(installed, required));

    [Fact]
    public void Build_SeveralEntryPoints_DescribesEachWithItsArguments()
    {
        var result = BuildWith(["main.json", "other.json"], ("main.json", Workflows.Hello()), ("other.json", Workflows.WithSecret()), ("helper.json", Workflows.Hello(name: "Helper")));

        Assert.True(result.Succeeded, string.Join(" ", result.Errors));
        Assert.Equal(["main.json", "other.json"], result.Manifest!.EntryPoints.Select(e => e.Path));
        Assert.Equal(["who", "count", "greeting"], result.Manifest.EntryPoints[0].Arguments.Select(a => a.Name));
        Assert.Equal(["\"World\"", null, null], result.Manifest.EntryPoints[0].Arguments.Select(a => a.Default));
        Assert.True(Assert.Single(result.Manifest.EntryPoints[1].Arguments).Secret);
        Assert.Equal(3, result.Manifest.Files.Count);
    }

    [Theory]
    [InlineData(new string[0], "at least one")]
    [InlineData(new[] { "missing.json" }, "not in the project")]
    [InlineData(new[] { "main.json", "main.json" }, "appears twice")]
    public void Build_BadEntryPoints_AreRefused(string[] entryPoints, string reason)
    {
        var result = BuildWith(entryPoints, ("main.json", Workflows.Hello()));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void TryVerify_AnEntryPointThatIsNotAFile_IsRefused()
    {
        var result = Build(("main.json", Workflows.Hello()));
        var node = JsonNode.Parse(result.Content)!;
        node["manifest"]!["entryPoints"]![0]!["path"] = "elsewhere.json";
        var bad = Encoding.UTF8.GetBytes(node.ToJsonString());

        Assert.False(WorkflowPackage.TryVerify(bad, WorkflowPackage.Sha256Hex(bad), out _, out var error));
        Assert.Contains("entry points", error, StringComparison.Ordinal);
    }

    [Fact]
    public void FindEntryPoint_MatchesThePathExactly()
    {
        var manifest = Build(("main.json", Workflows.Hello())).Manifest!;

        Assert.NotNull(WorkflowPackage.FindEntryPoint(manifest, "main.json"));
        Assert.Null(WorkflowPackage.FindEntryPoint(manifest, "MAIN.json"));
    }

    private static PackageBuildResult Build(params (string Path, string Json)[] files) => BuildWith([files[0].Path], files);

    private static PackageBuildResult BuildWith(string[] entryPoints, params (string Path, string Json)[] files) => WorkflowPackage.Build(
        new PackageBuildRequest
        {
            ProcessId = "prc_1",
            Name = "Test",
            Version = 1,
            Project = "project",
            EntryPaths = entryPoints,
            Files = [.. files.Select(f => new KeyValuePair<string, string>(f.Path, f.Json))],
            PublishedAt = _now,
            PublishedBy = "tester",
        },
        TestCatalog.Loader.Load,
        TestCatalog.PluginOf);
}

public sealed class TokenSecretsTests
{
    [Fact]
    public void Create_ThenParseAndVerify_RoundTrips()
    {
        var (token, hash) = TokenSecrets.Create(TokenSecrets.RunnerPrefix, "rnr_abc");

        Assert.True(TokenSecrets.TryParse(token, TokenSecrets.RunnerPrefix, out var id, out var secret));
        Assert.Equal("rnr_abc", id);
        Assert.True(TokenSecrets.Verify(secret, hash));
        Assert.DoesNotContain(secret, hash, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("myrpa_token_x.y")]
    [InlineData("myrpa_runner_.secret")]
    [InlineData("myrpa_runner_id.")]
    [InlineData("myrpa_runner_BAD!.secret")]
    public void TryParse_Malformed_IsRefused(string? token) => Assert.False(TokenSecrets.TryParse(token, TokenSecrets.RunnerPrefix, out _, out _));

    [Fact]
    public void Verify_AnotherSecret_IsFalse()
    {
        var (_, hash) = TokenSecrets.Create(TokenSecrets.AccessPrefix, "tok_1");

        Assert.False(TokenSecrets.Verify("not-the-secret", hash));
    }

    [Fact]
    public void Ids_AreUniqueAndLowercase()
    {
        var ids = new RandomOrchestrationIds();
        var many = Enumerable.Range(0, 1000).Select(_ => ids.NewId("job")).ToList();

        Assert.Equal(many.Count, many.Distinct(StringComparer.Ordinal).Count());
        Assert.All(many, id => Assert.Matches("^job_[a-z2-7]{20}$", id));
    }
}
