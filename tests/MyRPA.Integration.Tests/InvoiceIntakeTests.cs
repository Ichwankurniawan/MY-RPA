using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MyRPA.Cli;
using MyRPA.Documents.Tests;

namespace MyRPA.Integration.Tests;

/// <summary>
/// Phase 7.1 definition of done (ADR-0043): the invoice-intake sample runs through the CLI with the email, documents,
/// database, Excel, files, SFTP and HTTP plugins: IMAP → PDF text → database and Excel → ZIP → SFTP → chat webhook. The
/// run needs a mail server (MYRPA_TEST_MAIL) and an SFTP server (MYRPA_TEST_SFTP), as in the CI Integration services
/// job; without them it is reported as skipped, never as passed. Validation always runs.
/// </summary>
public sealed class InvoiceIntakeTests
{
    private static string Sample => Path.Combine(RepositoryPaths.Samples, "plugins", "invoice-intake.json");

    private static string PluginDirectory(string key) => Path.GetFullPath(
        typeof(InvoiceIntakeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!);

    private static Dictionary<string, string> Parse(string text) =>
        text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : string.Empty, StringComparer.OrdinalIgnoreCase);

    /// <summary>A plugin configuration for every plugin the sample needs, all over one data folder.</summary>
    private static string Config(TempWorkspace workspace, string data, Dictionary<string, string> mail, Dictionary<string, string> sftp)
    {
        static string Entry(string key, IReadOnlyDictionary<string, string> settings) =>
            $$"""{ "directory": {{JsonSerializer.Serialize(PluginDirectory(key))}}, "settings": {{JsonSerializer.Serialize(settings)}} }""";

        var root = new Dictionary<string, string> { ["fileRoot"] = data };
        var plugins = new[]
        {
            Entry("EmailPluginDirectory", new Dictionary<string, string>
            {
                ["smtpHost"] = mail["smtpHost"], ["smtpPort"] = mail["smtpPort"], ["smtpSecurity"] = "None",
                ["imapHost"] = mail["imapHost"], ["imapPort"] = mail["imapPort"], ["imapSecurity"] = "None",
                ["fileRoot"] = data, ["defaultFrom"] = mail["address"],
            }),
            Entry("DocumentsPluginDirectory", root),
            Entry("DatabasePluginDirectory", new Dictionary<string, string>
            {
                ["connection.invoices.provider"] = "Sqlite",
                ["connection.invoices.connectionString"] = $"Data Source={Path.Combine(data, "invoices.db")};Pooling=False",
            }),
            Entry("SpreadsheetPluginDirectory", root),
            Entry("FilesPluginDirectory", root),
            Entry("SftpPluginDirectory", new Dictionary<string, string>
            {
                ["server.bank.host"] = sftp["host"], ["server.bank.port"] = sftp["port"], ["server.bank.hostKey"] = sftp["hostKey"],
                ["server.bank.username"] = sftp["user"], ["server.bank.remoteRoot"] = sftp["root"], ["fileRoot"] = data,
            }),
            Entry("HttpPluginDirectory", new Dictionary<string, string> { ["allowedHosts"] = "localhost" }),
        };
        return workspace.Write("plugins.json", $$"""{ "pluginConfigVersion": "1.0", "plugins": [ {{string.Join(", ", plugins)}} ] }""");
    }

    [Fact]
    public async Task LocalTour_HashesZipsStoresAndRegisters_WithoutNetwork_ThroughTheCli()
    {
        using var workspace = new TempWorkspace();
        var data = Directory.CreateDirectory(Path.Combine(workspace.Root, "data")).FullName;
        static string Entry(string key, IReadOnlyDictionary<string, string> settings) =>
            $$"""{ "directory": {{JsonSerializer.Serialize(PluginDirectory(key))}}, "settings": {{JsonSerializer.Serialize(settings)}} }""";
        var root = new Dictionary<string, string> { ["fileRoot"] = data };
        var config = workspace.Write("local.json", $$"""
            { "pluginConfigVersion": "1.0", "plugins": [
              {{Entry("FilesPluginDirectory", root)}},
              {{Entry("SpreadsheetPluginDirectory", root)}},
              {{Entry("DatabasePluginDirectory", new Dictionary<string, string> { ["connection.demo.provider"] = "Sqlite", ["connection.demo.connectionString"] = $"Data Source={Path.Combine(data, "demo.db")};Pooling=False" })}} ] }
            """);

        var result = await Cli.RunAsync("--plugin-config", config, "run", Path.Combine(RepositoryPaths.Samples, "plugins", "local-71-demo.json"));

        Assert.True(result.ExitCode == CliExitCodes.Success, result.Out + result.Error);
        var outputs = JsonDocument.Parse(result.Out).RootElement.GetProperty("outputs");
        Assert.Equal(64, outputs.GetProperty("hash").GetString()!.Length);
        Assert.Equal(1, outputs.GetProperty("zipped").GetInt64());
        Assert.Equal("demo71-unzipped/notes.txt", outputs.GetProperty("extracted")[0].GetString());
        Assert.Equal(2, outputs.GetProperty("invoices").GetArrayLength());
        Assert.Equal(1333.5m, outputs.GetProperty("total").GetDecimal());
        Assert.Equal(2, outputs.GetProperty("firstNewRow").GetInt64());
        Assert.Equal(1, outputs.GetProperty("cleared").GetInt64());
    }

    [Fact]
    public async Task InvoiceIntake_IsValid_WithItsPlugins()
    {
        using var workspace = new TempWorkspace();
        var data = Directory.CreateDirectory(Path.Combine(workspace.Root, "data")).FullName;
        var mail = Parse("smtpHost=127.0.0.1;smtpPort=25;imapHost=127.0.0.1;imapPort=143;address=robot@example.test");
        var sftp = Parse("host=127.0.0.1;port=22;hostKey=SHA256:nThbg6kXUpJWGl7E1IGOCspRomTxdCARLviKw6E5SY8;user=robot;root=/upload");

        var result = await Cli.RunAsync("--plugin-config", Config(workspace, data, mail, sftp), "validate", Sample);

        Assert.True(result.ExitCode == CliExitCodes.Success, result.Out + result.Error);
    }

    [Fact]
    public async Task InvoiceIntake_ReadsMailPdfsStoresRegistersArchivesUploadsAndPosts_ThroughTheCli()
    {
        var mailText = Environment.GetEnvironmentVariable("MYRPA_TEST_MAIL");
        var sftpText = Environment.GetEnvironmentVariable("MYRPA_TEST_SFTP");
        if (string.IsNullOrWhiteSpace(mailText) || string.IsNullOrWhiteSpace(sftpText))
        {
            const string reason = "Needs a mail server (MYRPA_TEST_MAIL) and an SFTP server (MYRPA_TEST_SFTP), as in the CI Integration services job.";
            if (Environment.GetEnvironmentVariable("MYRPA_TEST_REQUIRE_SERVERS") == "1")
            {
                Assert.Fail(reason);
            }

            Assert.Skip(reason);
        }

        var mail = Parse(mailText!);
        var sftp = Parse(sftpText!);
        using var workspace = new TempWorkspace();
        using var chat = new ChatReceiver();
        var data = Directory.CreateDirectory(Path.Combine(workspace.Root, "data")).FullName;
        var config = Config(workspace, data, mail, sftp);
        var tag = $"Invoice {Guid.NewGuid():N}"[..20];

        // Two invoice mails with a PDF each, and one mail with only a text attachment (no PDF to read).
        Directory.CreateDirectory(Path.Combine(data, "seed"));
        await File.WriteAllBytesAsync(Path.Combine(data, "seed", "inv-41.pdf"), PdfFiles.Build(["ACME Supplies Ltd\nInvoice INV-2026-0041", "Total: 1,234.50 EUR"]), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(data, "seed", "inv-42.pdf"), PdfFiles.Build(["Bolt & Co\nInvoice INV-2026-0042\nTotal: 99.00 EUR"]), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(data, "seed", "notes.txt"), "not an invoice", TestContext.Current.CancellationToken);
        var seed = workspace.Write("seed.json", $$"""
            { "schemaVersion": "1.0", "id": "seed", "name": "Seed", "version": "1.0.0",
              "root": { "id": "main", "type": "Core.Sequence", "children": [
                { "id": "one", "type": "Email.Send", "properties": { "to": "'{{mail["address"]}}'", "subject": "'{{tag}} 41'", "body": "'Attached.'", "attachments": "'seed/inv-41.pdf'" } },
                { "id": "two", "type": "Email.Send", "properties": { "to": "'{{mail["address"]}}'", "subject": "'{{tag}} 42'", "body": "'Attached.'", "attachments": "['seed/inv-42.pdf', 'seed/notes.txt']" } },
                { "id": "three", "type": "Email.Send", "properties": { "to": "'{{mail["address"]}}'", "subject": "'{{tag}} no pdf'", "body": "'Notes only.'", "attachments": "'seed/notes.txt'" } } ] } }
            """);
        var seeded = await Cli.RunAsync("--plugin-config", config, "run", seed);
        Assert.True(seeded.ExitCode == CliExitCodes.Success, seeded.Out + seeded.Error);

        var result = await Cli.RunAsync(
            "--plugin-config", config, "run", Sample,
            "--arg", $"mailUser={mail["user"]}",
            "--arg", $"mailPassword={mail["password"]}",
            "--arg", $"sftpPassword={sftp["password"]}",
            "--arg", $"chatWebhook={chat.Url}",
            "--arg", $"subjectFilter={tag}");

        Assert.True(result.ExitCode == CliExitCodes.Success, result.Out + result.Error);
        var outputs = JsonDocument.Parse(result.Out).RootElement.GetProperty("outputs");
        Assert.Equal(2, outputs.GetProperty("processed").GetInt64());
        Assert.Equal(2, outputs.GetProperty("stored").GetInt64());
        Assert.Equal(2, outputs.GetProperty("registered").GetInt64());
        var archive = outputs.GetProperty("archive").GetString()!;
        Assert.Matches(@"^invoices-\d{8}-\d{6}\.zip$", archive);
        Assert.True(File.Exists(Path.Combine(data, "archive", archive)));
        Assert.True(File.Exists(Path.Combine(data, "register.xlsx")));

        var posted = Assert.Single(chat.Bodies);
        using (var body = JsonDocument.Parse(posted))
        {
            Assert.Equal("Invoice intake", body.RootElement.GetProperty("title").GetString());
            Assert.StartsWith("2 invoices processed (2 PDFs archived as invoices-", body.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(mail["password"], result.Out + result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(sftp["password"], result.Out + result.Error, StringComparison.Ordinal);

        // The mails were marked read: a second run finds nothing new.
        var again = await Cli.RunAsync(
            "--plugin-config", config, "run", Sample,
            "--arg", $"mailUser={mail["user"]}", "--arg", $"mailPassword={mail["password"]}",
            "--arg", $"sftpPassword={sftp["password"]}", "--arg", $"chatWebhook={chat.Url}", "--arg", $"subjectFilter={tag}");
        if (again.ExitCode == CliExitCodes.Success)
        {
            Assert.Equal(0, JsonDocument.Parse(again.Out).RootElement.GetProperty("outputs").GetProperty("processed").GetInt64());
        }
        else
        {
            // Within the same second the archive name repeats, and the upload refuses to replace it.
            Assert.Contains("RemoteFileExists", again.Out, StringComparison.Ordinal);
        }
    }

    /// <summary>A local chat webhook (HttpListener on localhost) that keeps every posted body.</summary>
    private sealed class ChatReceiver : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly ConcurrentQueue<string> _bodies = new();
        private readonly Task _loop;

        public ChatReceiver()
        {
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                Url = $"http://localhost:{((IPEndPoint)probe.LocalEndpoint).Port}/hook/secret-path/";
            }

            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }

                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        _bodies.Enqueue(await reader.ReadToEndAsync());
                    }

                    context.Response.StatusCode = 200;
                    context.Response.Close();
                }
            });
        }

        public string Url { get; }

        public IReadOnlyCollection<string> Bodies => _bodies;

        public void Dispose()
        {
            _listener.Close();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }
    }
}
