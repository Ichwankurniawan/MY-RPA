using System.Net;
using System.Text.Json;

namespace MyRPA.Server.Tests;

/// <summary>Without <c>--data</c> the Server runs no orchestrator (ADR-0049); everything else works as before.</summary>
public sealed class OrchestratorDisabledTests
{
    [Fact]
    public async Task WithoutData_InfoSaysSo_AndTheOrchestratorApiIsNotFound()
    {
        await using var server = await ServerHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;

        var info = JsonDocument.Parse(await server.Client.GetStringAsync("/api/info", ct)).RootElement;
        using var me = await server.Client.GetAsync("/api/orchestrator/me", ct);
        using var runner = await server.Client.PostAsync("/runner/v1/heartbeat", new StringContent("{}"), ct);

        Assert.False(info.GetProperty("orchestrator").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, me.StatusCode);
        Assert.Contains("--data", await me.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, runner.StatusCode);
    }
}
