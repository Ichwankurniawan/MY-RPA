using System.Text.Json;
using MyRPA.Ai.Contracts;
using static MyRPA.Ai.Assistant.Tests.Samples;

namespace MyRPA.Ai.Assistant.Tests;

/// <summary>Explaining workflows and failed runs (brief §7, §10).</summary>
public sealed class ExplainAndDebugTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Explain_KeepsOnlyNotesOfNodesThatAreInTheWorkflow()
    {
        var answer = JsonSerializer.Serialize(new { summary = "Logs a greeting.", steps = new[] { new { nodeId = "say", text = "Writes Hello." }, new { nodeId = "ghost", text = "Invented." } } });
        var provider = new FakeProvider().Answer(answer);

        var result = await TestCatalog.Assistant(provider).ExplainAsync(new ExplainRequest(Valid), Token);

        Assert.Equal("Logs a greeting.", result.Summary);
        Assert.Equal([new NodeNote("say", "Writes Hello.")], result.Steps);
        Assert.Contains("\"Core.Log\"", provider.Requests[0].Messages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Core.If\"", provider.Requests[0].Messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_AnAnswerInTheWrongShape_IsAProviderFailure()
    {
        var error = await Assert.ThrowsAsync<AiProviderException>(() => TestCatalog.Assistant(new FakeProvider().Answer("I think it greets.")).ExplainAsync(new ExplainRequest(Valid), Token));

        Assert.Equal(AiFailureKind.InvalidResponse, error.Kind);
    }

    [Theory]
    [InlineData("likely", DebugCertainty.Likely)]
    [InlineData("possible", DebugCertainty.Possible)]
    [InlineData("certain beyond doubt", DebugCertainty.Unknown)]
    public async Task Debug_LabelsHypotheses_AndPointsOnlyToNodesOfTheWorkflow(string certainty, DebugCertainty expected)
    {
        var answer = JsonSerializer.Serialize(new { explanation = "The name was empty.", certainty, nodeId = "say", suggestions = new[] { "Give who a default." } });
        var provider = new FakeProvider().Answer(answer);
        var failure = new RunFailure("Failed") { Code = "MYRPA2002", Message = "boom", NodeId = "say", ActivityType = "Core.Log", ErrorType = "Workflow" };

        var result = await TestCatalog.Assistant(provider).DebugAsync(new DebugRequest(Valid, failure), Token);

        Assert.Equal(expected, result.Certainty);
        Assert.Equal("say", result.NodeId);
        Assert.Equal(["Give who a default."], result.Suggestions);
        var sent = provider.Requests[0].Messages[0].Text;
        Assert.Contains("FAILED RUN", sent, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"MYRPA2002\"", sent, StringComparison.Ordinal);
        Assert.Contains("hypotheses as hypotheses", provider.Requests[0].Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Debug_ANodeThatIsNotInTheWorkflow_IsDropped()
    {
        var answer = JsonSerializer.Serialize(new { explanation = "Unclear.", certainty = "unknown", nodeId = "elsewhere", suggestions = Array.Empty<string>() });

        var result = await TestCatalog.Assistant(new FakeProvider().Answer(answer)).DebugAsync(new DebugRequest(Valid, new RunFailure("Failed")), Token);

        Assert.Null(result.NodeId);
        Assert.Equal(DebugCertainty.Unknown, result.Certainty);
    }

    [Fact]
    public async Task FixFailure_IsAValidatedProposal_LikeAnyOther()
    {
        var provider = new FakeProvider().Answer(Proposal(Valid));
        var request = new GenerationRequest(string.Empty, AssistantPurpose.FixFailure) { Document = Valid, Failure = new RunFailure("Failed") { Code = "MYRPA2002", NodeId = "say" } };

        var result = await TestCatalog.Assistant(provider).GenerateAsync(request, Token);

        Assert.Equal(GenerationOutcome.Proposal, result.Outcome);
        Assert.Contains("FAILED RUN", provider.Requests[0].Messages[0].Text, StringComparison.Ordinal);
        Assert.Contains("Fix the cause of the failed run.", provider.Requests[0].Messages[^1].Text, StringComparison.Ordinal);
    }
}
