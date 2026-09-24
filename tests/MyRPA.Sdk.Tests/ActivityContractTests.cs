using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MyRPA.Activities;
using MyRPA.Core.Activities;
using MyRPA.Core.Diagnostics;
using MyRPA.Core.Execution;
using MyRPA.Sdk.Automation;
using MyRPA.Workflow.Execution;

namespace MyRPA.Sdk.Tests;

/// <summary>ADR-0013: identity, failure classification, declared outputs, deadline, logging correlation.</summary>
public sealed partial class ActivityContractTests
{
    private static readonly string _resultArgument = """[ { "name": "result", "direction": "Out", "type": "Object" } ]""";

    [Theory]
    [InlineData("Core.Log", true)]
    [InlineData("Browser.Click", true)]
    [InlineData("Contoso.Excel.ReadRange", true)]
    [InlineData("Click", false)]
    [InlineData("Browser.Click-Button", false)]
    [InlineData("System.Diagnostics.Process, System", false)]
    public void ActivityIdentity_IsANamespacedName(string name, bool valid) => Assert.Equal(valid, ActivityTypeName.IsValid(name));

    [Theory]
    [InlineData("Custom.Kind")]
    [InlineData(AutomationErrorTypes.ElementNotFound)]
    public async Task ClassifiedFailure_BecomesTheNodeErrorType(string errorType)
    {
        using var h = new SdkHarness(s => s.AddActivity<ClassifiedFailureActivity>(
            SdkHarness.Descriptor("Test.Classified", new ActivityPropertyDefinition("kind", ActivityPropertyKind.Text, isRequired: true))));

        var result = await h.RunAsync(SdkHarness.Workflow($$"""{ "id": "c", "type": "Test.Classified", "properties": { "kind": "{{errorType}}" } }"""));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ExecutionErrorCodes.ActivityFailed, result.Error!.Code);
        Assert.Equal(errorType, result.Error.ErrorType);
        Assert.Equal("c", result.Error.NodeId);
        Assert.Equal("Test.Classified", result.Error.ActivityType);
    }

    [Fact]
    public async Task ClassifiedFailure_IsVisibleToTryCatch()
    {
        using var h = new SdkHarness(s => s.AddActivity<ClassifiedFailureActivity>(
            SdkHarness.Descriptor("Test.Classified", new ActivityPropertyDefinition("kind", ActivityPropertyKind.Text, isRequired: true))));

        var result = await h.RunAsync(SdkHarness.Workflow(
            """
            { "id": "t", "type": "Core.TryCatch", "properties": { "exceptionVariable": "err" }, "slots": {
                "try": { "id": "c", "type": "Test.Classified", "properties": { "kind": "ElementNotFound" } },
                "catch": { "id": "a", "type": "Core.Assign", "properties": { "to": "result", "value": "err.errorType + '/' + err.code" } } } }
            """,
            _resultArgument));

        Assert.Equal("ElementNotFound/MYRPA2001", result.Outputs["result"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("1Leading")]
    [InlineData("Trailing.")]
    public void ErrorType_MustBeANameLikeValue(string errorType) =>
        Assert.Throws<ArgumentException>(() => new ActivityFailedException(errorType, "m"));

    [Fact]
    public async Task UnclassifiedException_UsesTheTypeName()
    {
        using var h = new SdkHarness(s => s.AddActivity<ClassifiedFailureActivity>(
            SdkHarness.Descriptor("Test.Classified", new ActivityPropertyDefinition("kind", ActivityPropertyKind.Text, isRequired: true))));

        var result = await h.RunAsync(SdkHarness.Workflow("""{ "id": "c", "type": "Test.Classified", "properties": { "kind": "plain" } }"""));

        Assert.Equal(ExecutionErrorCodes.ActivityFailed, result.Error!.Code);
        Assert.Equal(nameof(TimeoutException), result.Error.ErrorType);
    }

    [Fact]
    public async Task SetValue_AcceptsOnlyDeclaredTargets()
    {
        using var h = new SdkHarness(s => s.AddActivity<SneakyWriterActivity>(
            SdkHarness.Descriptor("Test.Sneaky", new ActivityPropertyDefinition("to", ActivityPropertyKind.AssignmentTarget, isRequired: true))));
        var workflow = SdkHarness.Workflow(
            """{ "id": "w", "type": "Test.Sneaky", "properties": { "to": "result" } }""",
            """[ { "name": "result", "direction": "Out", "type": "String" }, { "name": "other", "direction": "Out", "type": "String" } ]""");

        var result = await h.RunAsync(workflow);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("cannot assign 'other'", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Context_ExposesIdentityAndDeadline()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero));
        using var h = new SdkHarness(s => s.AddActivity<ContextProbeActivity>(SdkHarness.Descriptor("Test.Context")), time);

        await h.RunAsync(SdkHarness.Workflow("""{ "id": "p", "type": "Test.Context" }"""), timeout: TimeSpan.FromSeconds(30));
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(30), h.Probe.Deadline);
        Assert.Equal("p", h.Probe.NodeId);

        await h.RunAsync(SdkHarness.Workflow("""{ "id": "p", "type": "Test.Context" }"""));
        Assert.Null(h.Probe.Deadline);
    }

    [Fact]
    public async Task ActivityLogs_AreCorrelatedWithTheNode()
    {
        using var logs = new CapturingLoggerProvider();
        using var h = new SdkHarness(s => s.AddActivity<LoggingActivity>(SdkHarness.Descriptor("Test.Logging")), logs: logs);

        var result = await h.RunAsync(SdkHarness.Workflow("""{ "id": "logger-node", "type": "Test.Logging" }"""));

        var entry = Assert.Single(logs.Entries, e => e.Message == "hello from a plugin activity");
        Assert.Equal("logger-node", entry.Scope[DiagnosticNames.NodeIdKey]);
        Assert.Equal(result.ExecutionId.ToString(), entry.Scope[DiagnosticNames.ExecutionIdKey]);
    }

    public sealed class ClassifiedFailureActivity : IActivity
    {
        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            var kind = context.GetText("kind");
            throw kind == "plain"
                ? new TimeoutException("plain failure")
                : kind.StartsWith("Element", StringComparison.Ordinal)
                    ? new AutomationException(kind, "automation failure")
                    : new ActivityFailedException(kind, "classified failure");
        }
    }

    public sealed class SneakyWriterActivity : IActivity
    {
        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            context.SetValue(context.GetName("to"), "declared");
            context.SetValue("other", "undeclared");
            return ActivityResult.CompletedTask;
        }
    }

    public sealed class ContextProbeActivity(Probe probe) : IActivity
    {
        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            probe.Deadline = context.Deadline;
            probe.NodeId = context.Identity.NodeId?.Value;
            return ActivityResult.CompletedTask;
        }
    }

    public sealed partial class LoggingActivity(ILogger<LoggingActivity> logger) : IActivity
    {
        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            Log(logger);
            return ActivityResult.CompletedTask;
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "hello from a plugin activity")]
        private static partial void Log(ILogger logger);
    }
}
