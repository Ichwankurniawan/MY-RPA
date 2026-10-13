using System.Net;
using System.Net.Sockets;

namespace MyRPA.Server.Tests;

/// <summary>Start-up problems are one clear line, not a stack trace.</summary>
public sealed class StartupErrorTests
{
    [Fact]
    public void CommandLine_RunnerUrlOnTheStudioPort_IsRefusedWithAHint()
    {
        var folder = Directory.CreateTempSubdirectory("myrpa-start-");
        try
        {
            var options = ServerCommandLine.Parse(
                ["--project", folder.FullName, "--data", Path.Combine(folder.FullName, "data"), "--runner-url", "http://127.0.0.1:5310/"],
                out var error);

            Assert.Null(options);
            Assert.Contains("--runner-url uses port 5310, which is the Studio's port", error, StringComparison.Ordinal);
            Assert.Contains("without --runner-url", error, StringComparison.Ordinal);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Run_APortInUse_SaysSoInOneLine()
    {
        var folder = Directory.CreateTempSubdirectory("myrpa-start-");
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            var port = ((IPEndPoint)taken.LocalEndpoint).Port;
            using var output = new StringWriter();
            using var error = new StringWriter();

            var code = await ServerApplication.RunAsync(
                ["--project", folder.FullName, "--data", Path.Combine(folder.FullName, "data"), "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                output,
                error,
                TestContext.Current.CancellationToken);

            var text = error.ToString().Trim();
            Assert.Equal(2, code);
            Assert.DoesNotContain('\n', text);
            Assert.Contains("address already in use", text, StringComparison.Ordinal);
            Assert.Contains("start this one with another --port", text, StringComparison.Ordinal);
        }
        finally
        {
            taken.Stop();
            folder.Delete(recursive: true);
        }
    }
}
