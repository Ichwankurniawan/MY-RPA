using System.Text;
using MyRPA.Workflow.Values;
using static MyRPA.Email.Tests.EmailHost;

namespace MyRPA.Email.Tests;

/// <summary>Email.Send against the in-process fake SMTP server (ADR-0043): delivery, TLS, rejection, limits, secrets.</summary>
public sealed class EmailSendTests
{
    private const string Password = "p@ss-w0rd-xyz";

    private static Dictionary<string, string> Plain(FakeSmtpServer server, params (string Name, string Value)[] extra)
    {
        var settings = new Dictionary<string, string>
        {
            ["smtpHost"] = "127.0.0.1",
            ["smtpPort"] = $"{server.Port}",
            ["smtpSecurity"] = "None",
            ["fileRoot"] = "$root",
            ["maxMessageBytes"] = "8192",
            ["timeoutMs"] = "15000",
        };
        foreach (var (name, value) in extra)
        {
            settings[name] = value;
        }

        return settings;
    }

    private static string Send(string properties) => Node("Email.Send", properties);

    private static Dictionary<string, object?> Secret(string value = Password) => new() { ["pwd"] = value };

    [Fact]
    public async Task Send_ThroughAPlainRelay_DeliversToEveryRecipient_WithBodyAndAttachment()
    {
        using var server = new FakeSmtpServer();
        await using var host = await StartAsync(Plain(server));
        Directory.CreateDirectory(host.InRoot("out"));
        File.WriteAllText(host.InRoot("out/report.csv"), "invoice,amount\nINV-1,10\n");

        var result = await host.RunAsync(
            Send(""" "from": "'Robot <robot@acme.test>'", "to": "'ada@acme.test, bob@acme.test'", "cc": "['cy@acme.test']", "bcc": "'dee@acme.test'", "subject": "'Invoices ready'", "body": "'2 invoices processed.'", "attachments": "'out/report.csv'", "username": "'robot'", "password": "pwd", "messageId": "id" """),
            ["id"],
            Secret());

        AssertSucceeded(result);
        var mail = Assert.Single(server.Received);
        Assert.Equal("robot@acme.test", mail.From);
        Assert.Equal(["ada@acme.test", "bob@acme.test", "cy@acme.test", "dee@acme.test"], mail.Recipients);
        Assert.Contains("Subject: Invoices ready", mail.Data, StringComparison.Ordinal);
        Assert.Contains("2 invoices processed.", mail.Data, StringComparison.Ordinal);
        Assert.Contains("report.csv", mail.Data, StringComparison.Ordinal);
        Assert.Contains("INV-1,10", mail.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("dee@acme.test", mail.Data, StringComparison.Ordinal);
        Assert.Equal(("robot", Password), (mail.User, mail.Password));
        Assert.Contains((string)result.Outputs["id"]!, mail.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Logs.Messages, m => m.Contains(Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Send_WithoutFrom_UsesTheDefaultSender_AndNeedsNoLogin()
    {
        using var server = new FakeSmtpServer();
        await using var host = await StartAsync(Plain(server, ("defaultFrom", "noreply@acme.test")));

        var result = await host.RunAsync(Send(""" "to": "['ada@acme.test']", "subject": "'Hello'", "body": "'<b>Hi</b>'", "html": true """));

        AssertSucceeded(result);
        var mail = Assert.Single(server.Received);
        Assert.Equal("noreply@acme.test", mail.From);
        Assert.Null(mail.User);
        Assert.Contains("text/html", mail.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_ByDefault_RequiresTls_AndRefusesAServerWithoutStartTls()
    {
        using var server = new FakeSmtpServer();
        var settings = Plain(server);
        settings.Remove("smtpSecurity");
        await using var host = await StartAsync(settings);

        var result = await host.RunAsync(Send(""" "from": "'robot@acme.test'", "to": "'ada@acme.test'", "subject": "'x'", "username": "'robot'", "password": "pwd" """), inputs: Secret());

        host.AssertFailed(result, ErrorTypes.EmailConnection);
        Assert.Contains("TLS", result.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Send_StartTls_WithAnUntrustedCertificate_IsRefused_BeforeThePasswordIsSent()
    {
        using var server = new FakeSmtpServer(offerStartTls: true);
        var settings = Plain(server);
        settings["smtpSecurity"] = "StartTls";
        await using var host = await StartAsync(settings);

        var result = await host.RunAsync(Send(""" "from": "'robot@acme.test'", "to": "'ada@acme.test'", "subject": "'x'", "username": "'robot'", "password": "pwd" """), inputs: Secret());

        host.AssertFailed(result, ErrorTypes.EmailConnection);
        Assert.Contains("certificate", result.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Send_ARefusedRecipient_FailsEmailRejected()
    {
        using var server = new FakeSmtpServer();
        await using var host = await StartAsync(Plain(server));

        var result = await host.RunAsync(Send(""" "from": "'robot@acme.test'", "to": "'ghost@reject.test'", "subject": "'x'" """));

        host.AssertFailed(result, ErrorTypes.EmailRejected);
        Assert.Contains("550", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_AWrongPassword_FailsEmailAuthentication_WithoutNamingIt()
    {
        using var server = new FakeSmtpServer();
        await using var host = await StartAsync(Plain(server));

        var result = await host.RunAsync(Send(""" "from": "'robot@acme.test'", "to": "'ada@acme.test'", "subject": "'x'", "username": "'robot'", "password": "pwd" """), inputs: Secret("wrong"));

        host.AssertFailed(result, ErrorTypes.EmailAuthentication);
        Assert.DoesNotContain("wrong", result.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Send_AServerThatNeverAnswers_FailsWithTimeout()
    {
        using var server = new FakeSmtpServer(silent: true);
        await using var host = await StartAsync(Plain(server, ("timeoutMs", "1500")));

        var result = await host.RunAsync(Send(""" "from": "'robot@acme.test'", "to": "'ada@acme.test'", "subject": "'x'" """));

        host.AssertFailed(result, ErrorTypes.Timeout);
    }

    [Fact]
    public async Task Send_WithoutAnSmtpHost_FailsHostNotConfigured()
    {
        await using var host = await StartAsync(new Dictionary<string, string>());

        var result = await host.RunAsync(Send(""" "from": "'robot@acme.test'", "to": "'ada@acme.test'", "subject": "'x'" """));

        host.AssertFailed(result, ErrorTypes.HostNotConfigured);
    }

    [Theory]
    [InlineData(""" "from": "'robot@acme.test'", "to": "'not an address'", "subject": "'x'" """)]
    [InlineData(""" "from": "'robot@acme.test'", "to": "'ada@acme.test'", "subject": "'two\nlines'" """)]
    [InlineData(""" "to": "'ada@acme.test'", "subject": "'no sender and no default'" """)]
    [InlineData(""" "from": "'robot@acme.test'", "to": "[]", "subject": "'nobody'" """)]
    public async Task Send_BadInput_FailsInvalidInput_AndSendsNothing(string properties)
    {
        using var server = new FakeSmtpServer();
        await using var host = await StartAsync(Plain(server));

        var result = await host.RunAsync(Send(properties));

        host.AssertFailed(result, ErrorTypes.InvalidInput);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Send_Attachments_StayInsideTheRoot_AndUnderTheSizeLimit()
    {
        using var server = new FakeSmtpServer();
        await using var host = await StartAsync(Plain(server));
        File.WriteAllText(Path.Combine(host.Outside, "secret.txt"), "outside");
        File.WriteAllText(host.InRoot("big.bin"), new string('x', 9000));

        var outside = await host.RunAsync(Send(""" "from": "'robot@acme.test'", "to": "'ada@acme.test'", "subject": "'x'", "attachments": "'../outside/secret.txt'" """));
        var large = await host.RunAsync(Send(""" "from": "'robot@acme.test'", "to": "'ada@acme.test'", "subject": "'x'", "attachments": "['big.bin']" """));

        host.AssertFailed(outside, ErrorTypes.FileAccessDenied);
        host.AssertFailed(large, ErrorTypes.MessageTooLarge);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Send_Attachments_WithoutAFileRoot_AreRefused()
    {
        using var server = new FakeSmtpServer();
        var settings = Plain(server);
        settings.Remove("fileRoot");
        await using var host = await StartAsync(settings);

        var result = await host.RunAsync(Send(""" "from": "'robot@acme.test'", "to": "'ada@acme.test'", "subject": "'x'", "attachments": "'a.txt'" """));

        host.AssertFailed(result, ErrorTypes.FileAccessDenied);
        Assert.Contains("fileRoot", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APasswordWrittenInTheWorkflow_IsRefusedByTheLoader()
    {
        using var server = new FakeSmtpServer();
        await using var host = await StartAsync(Plain(server));

        var load = host.Load(Send(""" "from": "'robot@acme.test'", "to": "'ada@acme.test'", "subject": "'x'", "username": "'robot'", "password": "'hunter2'" """), [], []);

        Assert.False(load.IsValid);
        Assert.Contains(load.Diagnostics, d => d.Code == "MYRPA1066");
    }

    [Theory]
    [InlineData("Email.MarkRead", """ "ids": "'not-an-id'" """)]
    [InlineData("Email.Move", """ "ids": "['INBOX/1']", "destination": "'Done'" """)]
    [InlineData("Email.SaveAttachments", """ "id": "'INBOX/x/1'", "folder": "'in'" """)]
    public async Task AMalformedMessageId_FailsInvalidInput_BeforeConnecting(string type, string properties)
    {
        await using var host = await StartAsync(new Dictionary<string, string> { ["imapHost"] = "127.0.0.1", ["imapPort"] = "1", ["fileRoot"] = "$root" });

        var result = await host.RunAsync(Node(type, properties));

        host.AssertFailed(result, ErrorTypes.InvalidInput);
    }

    [Fact]
    public async Task Read_WithoutAnImapHost_FailsHostNotConfigured()
    {
        await using var host = await StartAsync(new Dictionary<string, string>());

        var result = await host.RunAsync(Node("Email.Read", """ "result": "messages" """), ["messages"]);

        host.AssertFailed(result, ErrorTypes.HostNotConfigured);
    }
}

/// <summary>
/// The IMAP activities against a real mail server (MYRPA_TEST_MAIL; GreenMail in CI): read with filters, save
/// attachments, mark read, ids that went stale, a missing folder. Skipped (never passed) without a server.
/// </summary>
public sealed class EmailImapTests
{
    private const string Credentials = """ "username": "user", "password": "pwd" """;

    private static MailServerSettings Server()
    {
        var settings = MailServerSettings.FromEnvironment();
        if (settings is null)
        {
            var reason = $"No mail server: set {MailServerSettings.Variable} (see plugins/MyRPA.Email/README.md).";
            if (Environment.GetEnvironmentVariable("MYRPA_TEST_REQUIRE_SERVERS") == "1")
            {
                Assert.Fail(reason);
            }

            Assert.Skip(reason);
        }

        return settings!;
    }

    private static Dictionary<string, object?> Login(MailServerSettings server, params (string Name, object? Value)[] extra)
    {
        var inputs = new Dictionary<string, object?> { ["user"] = server["user"], ["pwd"] = server["password"] };
        foreach (var (name, value) in extra)
        {
            inputs[name] = value;
        }

        return inputs;
    }

    [Fact]
    public async Task ReadSaveMarkRead_TheInvoiceMailFlow()
    {
        var server = Server();
        await using var host = await StartAsync(server.PluginSettings());
        var tag = $"T{Guid.NewGuid():N}"[..12];
        Directory.CreateDirectory(host.InRoot("out"));
        File.WriteAllBytes(host.InRoot("out/invoice-1.pdf"), Encoding.ASCII.GetBytes("%PDF-1.4 fake invoice"));
        File.WriteAllText(host.InRoot("out/notes.txt"), "notes");
        var to = server["address"];

        var sent = await host.RunAsync(
            Node("Email.Send", $$""" "to": "'{{to}}'", "subject": "'Invoice {{tag}} one'", "body": "'Please find the invoice.'", "attachments": "['out/invoice-1.pdf', 'out/notes.txt']" """) + "," +
            Node("Email.Send", $$""" "to": "'{{to}}'", "subject": "'Invoice {{tag}} two'", "body": "'No attachment.'" """));
        AssertSucceeded(sent);

        var read = await host.RunAsync(Node("Email.Read", $$""" "subjectContains": "'{{tag}}'", {{Credentials}}, "result": "messages" """), ["messages"], Login(server));
        AssertSucceeded(read);
        var messages = ((IReadOnlyList<object?>)read.Outputs["messages"]!).Cast<IReadOnlyDictionary<string, object?>>().ToList();
        Assert.Equal(2, messages.Count);
        var first = messages[0];
        Assert.Equal($"Invoice {tag} one", first["subject"]);
        Assert.Contains("Please find the invoice.", (string)first["text"]!, StringComparison.Ordinal);
        Assert.Equal(["invoice-1.pdf", "notes.txt"], ((IReadOnlyList<object?>)first["attachments"]!).Cast<string>());
        Assert.Equal(false, first["tooLarge"]);

        var save = await host.RunAsync(
            Node("Email.SaveAttachments", $$""" "id": "id", "folder": "'in/{{tag}}'", "pattern": "'*.pdf'", {{Credentials}}, "result": "saved" """),
            ["saved"],
            Login(server, ("id", first["id"])));
        AssertSucceeded(save);
        Assert.Equal([$"in/{tag}/invoice-1.pdf"], ((IReadOnlyList<object?>)save.Outputs["saved"]!).Cast<string>());
        Assert.Equal("%PDF-1.4 fake invoice", File.ReadAllText(host.InRoot($"in/{tag}/invoice-1.pdf")));

        var again = await host.RunAsync(Node("Email.SaveAttachments", $$""" "id": "id", "folder": "'in/{{tag}}'", "pattern": "'*.pdf'", {{Credentials}} """), inputs: Login(server, ("id", first["id"])));
        host.AssertFailed(again, ErrorTypes.FileAlreadyExists);

        var ids = WorkflowValues.List(messages.Select(m => m["id"]));
        var marked = await host.RunAsync(Node("Email.MarkRead", $$""" "ids": "ids", {{Credentials}} """), inputs: Login(server, ("ids", ids)));
        AssertSucceeded(marked);
        var unread = await host.RunAsync(Node("Email.Read", $$""" "subjectContains": "'{{tag}}'", {{Credentials}}, "result": "messages" """), ["messages"], Login(server));
        var all = await host.RunAsync(Node("Email.Read", $$""" "subjectContains": "'{{tag}}'", "unreadOnly": false, {{Credentials}}, "result": "messages" """), ["messages"], Login(server));
        Assert.Empty((IReadOnlyList<object?>)unread.Outputs["messages"]!);
        Assert.Equal(2, ((IReadOnlyList<object?>)all.Outputs["messages"]!).Count);

        var missingFolder = await host.RunAsync(Node("Email.Move", $$""" "ids": "ids", "destination": "'NoSuchFolder{{tag}}'", {{Credentials}} """), inputs: Login(server, ("ids", ids)));
        host.AssertFailed(missingFolder, ErrorTypes.FolderNotFound);
    }

    [Fact]
    public async Task AStaleMessageId_FailsMessageNotFound()
    {
        var server = Server();
        await using var host = await StartAsync(server.PluginSettings());

        var result = await host.RunAsync(Node("Email.MarkRead", $$""" "ids": "'INBOX/1/999999'", {{Credentials}} """), inputs: Login(server));

        host.AssertFailed(result, ErrorTypes.MessageNotFound);
    }

    [Fact]
    public async Task AWrongPassword_FailsEmailAuthentication()
    {
        var server = Server();
        await using var host = await StartAsync(server.PluginSettings());

        var result = await host.RunAsync(Node("Email.Read", $$""" {{Credentials}}, "result": "messages" """), ["messages"], new Dictionary<string, object?> { ["user"] = server["user"], ["pwd"] = "definitely-wrong" });

        host.AssertFailed(result, ErrorTypes.EmailAuthentication);
    }
}
