using System.Text.Json;
using MyRPA.Ai.Contracts;
using static MyRPA.Ai.Assistant.Tests.Samples;

namespace MyRPA.Ai.Assistant.Tests;

/// <summary>Generation, clarification, validation and bounded repair (brief §6, §9, §15).</summary>
public sealed class GenerationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<GenerationResult> Create(FakeProvider provider, string request = "Greet someone", AssistantOptions? options = null) =>
        TestCatalog.Assistant(provider, options).GenerateAsync(new GenerationRequest(request, AssistantPurpose.Create), Token);

    [Fact]
    public async Task Generate_ValidProposal_PassesTheChecks_WithFactsReadFromTheWorkflowItself()
    {
        var workflow = WorkflowWith("""{ "id": "say", "type": "Core.Log", "properties": { "message": "'hi'" } }, { "id": "save", "type": "Test.WriteFile", "properties": { "path": "'out.txt'" } }""");
        var provider = new FakeProvider().Answer(Proposal(workflow, "Log", "Save"));

        var result = await Create(provider);

        Assert.Equal(GenerationOutcome.Proposal, result.Outcome);
        Assert.Equal(0, result.RepairAttempts);
        Assert.Equal(["Core.Sequence", "Core.Log", "Test.WriteFile"], result.ActivityTypes);
        Assert.Equal(["FileSystem"], result.SideEffects);
        Assert.Equal(["Log", "Save"], result.Plan);
        Assert.Equal(["the name is an input argument"], result.Assumptions);
        Assert.Equal("greet", JsonDocument.Parse(result.Document!).RootElement.GetProperty("id").GetString());
        Assert.Equal(1, result.Usage!.Requests);
        Assert.Equal(("fake", 10, 5), (result.Usage.Provider, result.Usage.InputTokens, result.Usage.OutputTokens));
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Generate_AnAnswerInACodeFence_IsStillRead()
    {
        var result = await Create(new FakeProvider().Answer("```json\n" + Proposal(Valid) + "\n```"));

        Assert.Equal(GenerationOutcome.Proposal, result.Outcome);
    }

    [Fact]
    public async Task Generate_MissingInformation_AsksFocusedQuestions_AndKeepsFactsApartFromAssumptions()
    {
        var provider = new FakeProvider().Answer(Clarify("Should duplicate invoices be skipped or updated?"));

        var result = await Create(provider, "Download today's invoices");

        Assert.Equal(GenerationOutcome.Clarify, result.Outcome);
        var question = Assert.Single(result.Questions);
        Assert.Equal(["Skip", "Update"], question.Options);
        Assert.Equal(["invoices are downloaded"], result.Known);
        Assert.Equal(["today means the local date"], result.Assumptions);
        Assert.Null(result.Document);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Generate_AClarifyWithoutQuestions_IsNotAccepted()
    {
        var result = await Create(new FakeProvider().Answer(Clarify()), options: new AssistantOptions { MaxRepairAttempts = 0 });

        Assert.Equal(GenerationOutcome.Invalid, result.Outcome);
        Assert.Equal("AI0001", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task Generate_AnUnsupportedCapability_IsReportedHonestly()
    {
        var result = await Create(new FakeProvider().Answer(Unsupported("reading a document scanner")), "Scan the paper invoices");

        Assert.Equal(GenerationOutcome.Unsupported, result.Outcome);
        Assert.Equal(["reading a document scanner"], result.Missing);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task Generate_MalformedAnswer_IsSentBackOnce_AndTheCorrectionIsRevalidated()
    {
        var provider = new FakeProvider().Answer("Sure! Here is your workflow.").Answer(Proposal(Valid));

        var result = await Create(provider);

        Assert.Equal(GenerationOutcome.Proposal, result.Outcome);
        Assert.Equal(1, result.RepairAttempts);
        Assert.Equal(2, provider.Requests.Count);
        var repair = provider.Requests[1].Messages;
        Assert.Equal(AiRole.Assistant, repair[^2].Role);
        Assert.Contains("AI0001", repair[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_AnUnknownActivity_StaysInvalid_AfterTheBoundedRepairs_AndIsNeverSubstituted()
    {
        var provider = new FakeProvider().Answer(Proposal(WorkflowWith("""{ "id": "order", "type": "Shop.PlaceOrder", "properties": {} }""")));

        var result = await Create(provider, options: new AssistantOptions { MaxRepairAttempts = 2 });

        Assert.Equal(GenerationOutcome.Invalid, result.Outcome);
        Assert.Equal(2, result.RepairAttempts);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Contains("Shop.PlaceOrder", result.Document, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, d => d.Severity == "Error" && d.NodeId == "order");
    }

    [Theory]
    [InlineData("{ not json", "AI0001")]
    [InlineData("""{ "schemaVersion": "2.0", "id": "greet", "name": "G", "version": "1", "root": { "id": "main", "type": "Core.Sequence" } }""", "AI0003")]
    [InlineData("""{ "schemaVersion": "1.0", "id": "greet", "name": "G", "version": "1", "root": { "id": "main", "type": "Core.Sequence", "children": [ { "id": "js", "type": "Code.JavaScript", "properties": { "code": "return 1;" } } ] } }""", "AI0004")]
    public async Task Generate_DeterministicChecks_RefuseWhatTheValidatorAloneWouldNotCatch(string workflow, string code)
    {
        var result = await Create(new FakeProvider().Answer(Proposal(workflow)), options: new AssistantOptions { MaxRepairAttempts = 0 });

        Assert.Equal(GenerationOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
    }

    [Theory]
    [InlineData("""{ "id": "say", "type": "Core.Log", "properties": {} }""")]
    [InlineData("""{ "id": "say", "type": "Core.Log", "properties": { "message": "'hi'", "colour": "'red'" }, "children": [ { "id": "x", "type": "Core.Log", "properties": { "message": "'x'" } } ] }""")]
    [InlineData("""{ "id": "say", "type": "Core.Delay", "properties": { "milliseconds": "true +" } }""")]
    public async Task Generate_ValidatorProblems_MissingPropertiesNestingAndExpressions_KeepTheProposalInvalid(string child)
    {
        var result = await Create(new FakeProvider().Answer(Proposal(WorkflowWith(child))), options: new AssistantOptions { MaxRepairAttempts = 0 });

        Assert.Equal(GenerationOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Severity == "Error" && d.Code.StartsWith("MYRPA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Generate_AProposalWithoutAWorkflow_OrAnOversizedAnswer_IsRefused()
    {
        var empty = Proposal(string.Empty);
        var noWorkflow = await Create(new FakeProvider().Answer(empty), options: new AssistantOptions { MaxRepairAttempts = 0 });
        var oversized = await Create(new FakeProvider().Answer(Proposal(Valid)), options: new AssistantOptions { MaxRepairAttempts = 0, MaxAnswerChars = 100 });

        Assert.Equal("AI0006", Assert.Single(noWorkflow.Diagnostics).Code);
        Assert.Equal("AI0002", Assert.Single(oversized.Diagnostics).Code);
        Assert.Null(oversized.Document);
    }

    [Fact]
    public async Task Edit_MustKeepTheWorkflowIdAndSchemaVersion()
    {
        var provider = new FakeProvider().Answer(Proposal(WorkflowWith("""{ "id": "say", "type": "Core.Log", "properties": { "message": "'hi'" } }""", id: "renamed", schema: "1.1")));
        var assistant = TestCatalog.Assistant(provider, new AssistantOptions { MaxRepairAttempts = 0 });

        var result = await assistant.GenerateAsync(new GenerationRequest("Say hi instead", AssistantPurpose.Edit) { Document = Valid }, Token);

        Assert.Equal(GenerationOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Code == "AI0005");
        Assert.Contains(result.Diagnostics, d => d.Code == "AI0003");
    }

    [Fact]
    public async Task Edit_AValidChange_IsAProposal_AndTheModelSeesTheCurrentWorkflow()
    {
        var changed = Valid.Replace("'Hello ' + who", "'Hi ' + who", StringComparison.Ordinal);
        var provider = new FakeProvider().Answer(Proposal(changed));
        var assistant = TestCatalog.Assistant(provider);

        var result = await assistant.GenerateAsync(new GenerationRequest("Say Hi instead of Hello", AssistantPurpose.Edit) { Document = Valid }, Token);

        Assert.Equal(GenerationOutcome.Proposal, result.Outcome);
        Assert.Contains("CURRENT WORKFLOW (schema 1.0)", provider.Requests[0].Messages[0].Text, StringComparison.Ordinal);
        Assert.Contains("'Hello ' + who", provider.Requests[0].Messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FixValidation_SendsTheValidatorsOwnProblems()
    {
        var broken = WorkflowWith("""{ "id": "say", "type": "Core.Log", "properties": {} }""");

        var request = TestCatalog.Assistant(new FakeProvider()).PrepareGeneration(new GenerationRequest(string.Empty, AssistantPurpose.FixValidation) { Document = broken });

        var context = request.Messages[0].Text;
        Assert.Contains("VALIDATION PROBLEMS (from the MyRPA validator)", context, StringComparison.Ordinal);
        Assert.Contains("\"nodeId\":\"say\"", context, StringComparison.Ordinal);
        Assert.Contains("Fix the validation problems.", request.Messages[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Requests_CarryTheRealCatalog_WithoutCodeActivities_AndTheExpressionFunctions()
    {
        var request = TestCatalog.Assistant(new FakeProvider()).PrepareGeneration(new GenerationRequest("Greet", AssistantPurpose.Create));

        var context = request.Messages[0].Text;
        Assert.Contains("\"Core.Log\"", context, StringComparison.Ordinal);
        Assert.Contains("\"Test.WriteFile\"", context, StringComparison.Ordinal);
        Assert.DoesNotContain("Code.JavaScript", context, StringComparison.Ordinal);
        Assert.Contains("EXPRESSION FUNCTIONS", context, StringComparison.Ordinal);
        Assert.NotNull(request.JsonSchema);
        Assert.Contains("Code namespace", request.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_IsBoundedBeforeAnythingIsSent()
    {
        var provider = new FakeProvider().Answer(Proposal(Valid));

        var error = await Assert.ThrowsAsync<AiProviderException>(() => Create(provider, options: new AssistantOptions { MaxRequestChars = 1_000 }));

        Assert.Equal(AiFailureKind.TooLarge, error.Kind);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Requests_WithoutWords_OrWithoutADocument_AreRefused()
    {
        var assistant = TestCatalog.Assistant(new FakeProvider().Answer(Proposal(Valid)));

        await Assert.ThrowsAsync<ArgumentException>(() => assistant.GenerateAsync(new GenerationRequest(" ", AssistantPurpose.Create), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => assistant.GenerateAsync(new GenerationRequest("Change it", AssistantPurpose.Edit), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => assistant.GenerateAsync(new GenerationRequest("Change it", AssistantPurpose.Edit) { Document = "[1]" }, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => assistant.GenerateAsync(new GenerationRequest(string.Empty, AssistantPurpose.FixFailure) { Document = Valid }, Token));
    }
}
