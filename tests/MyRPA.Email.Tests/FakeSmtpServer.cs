using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MyRPA.Email.Tests;

/// <summary>One message the fake server accepted.</summary>
public sealed record ReceivedMail(string From, IReadOnlyList<string> Recipients, string Data, string? User, string? Password);

/// <summary>
/// A small in-process SMTP server on loopback for the send tests (MailKit belongs to the plugin only). It speaks enough
/// SMTP for MailKit: EHLO, AUTH PLAIN and LOGIN, MAIL, RCPT, DATA, RSET, NOOP, QUIT, and optionally STARTTLS with a
/// self-signed certificate (which a correct client must refuse). Recipients ending in @reject.test are refused (550);
/// the password "wrong" is refused (535).
/// </summary>
public sealed class FakeSmtpServer : IDisposable
{
    /// <summary>A self-signed certificate no client trusts.</summary>
    private static readonly Lazy<X509Certificate2> _selfSigned = new(() =>
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fake.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    });

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<ReceivedMail> _received = new();
    private readonly bool _offerStartTls;
    private readonly bool _silent;
    private readonly Task _loop;

    /// <param name="offerStartTls">Advertise STARTTLS (with a self-signed certificate).</param>
    /// <param name="silent">Accept connections but never answer (for timeouts).</param>
    public FakeSmtpServer(bool offerStartTls = false, bool silent = false)
    {
        _offerStartTls = offerStartTls;
        _silent = silent;
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IReadOnlyCollection<ReceivedMail> Received => _received;

    /// <summary>Whether a client completed a TLS handshake (a correct client never does with this certificate).</summary>
    public bool TlsEstablished { get; private set; }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                if (_silent)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    return;
                }

                Stream stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.Latin1);
                var writer = new StreamWriter(stream, Encoding.Latin1) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 fake.test ESMTP ready");
                string? from = null, user = null, password = null;
                var recipients = new List<string>();
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var command = line.Split(' ', 2)[0].ToUpperInvariant();
                    var argument = line.Length > command.Length ? line[(command.Length + 1)..] : string.Empty;
                    switch (command)
                    {
                        case "EHLO":
                            await writer.WriteLineAsync("250-fake.test");
                            if (_offerStartTls && stream is not SslStream)
                            {
                                await writer.WriteLineAsync("250-STARTTLS");
                            }

                            await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                            await writer.WriteLineAsync("250 8BITMIME");
                            break;
                        case "HELO":
                            await writer.WriteLineAsync("250 fake.test");
                            break;
                        case "STARTTLS":
                            await writer.WriteLineAsync("220 go ahead");
                            var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                            await tls.AuthenticateAsServerAsync(_selfSigned.Value);
                            TlsEstablished = true;
                            stream = tls;
                            reader = new StreamReader(stream, Encoding.Latin1);
                            writer = new StreamWriter(stream, Encoding.Latin1) { NewLine = "\r\n", AutoFlush = true };
                            break;
                        case "AUTH":
                            (user, password) = await AuthenticateAsync(argument, reader, writer);
                            if (password == "wrong")
                            {
                                await writer.WriteLineAsync("535 5.7.8 authentication failed");
                                user = password = null;
                            }
                            else
                            {
                                await writer.WriteLineAsync("235 2.7.0 accepted");
                            }

                            break;
                        case "MAIL":
                            from = Address(argument);
                            recipients.Clear();
                            await writer.WriteLineAsync("250 ok");
                            break;
                        case "RCPT":
                            var to = Address(argument);
                            if (to.EndsWith("@reject.test", StringComparison.OrdinalIgnoreCase))
                            {
                                await writer.WriteLineAsync("550 5.1.1 no such user here");
                            }
                            else
                            {
                                recipients.Add(to);
                                await writer.WriteLineAsync("250 ok");
                            }

                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 end with <CRLF>.<CRLF>");
                            var data = new StringBuilder();
                            while (await reader.ReadLineAsync(_stop.Token) is { } dataLine && dataLine != ".")
                            {
                                data.Append(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine[1..] : dataLine).Append("\r\n");
                            }

                            _received.Enqueue(new ReceivedMail(from ?? string.Empty, [.. recipients], data.ToString(), user, password));
                            await writer.WriteLineAsync("250 2.0.0 queued");
                            break;
                        case "RSET":
                        case "NOOP":
                            await writer.WriteLineAsync("250 ok");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 bye");
                            return;
                        default:
                            await writer.WriteLineAsync("502 not implemented");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException or SocketException)
            {
                // The client went away (or refused the certificate): the test asserts on what it observed.
            }
        }
    }

    private static async Task<(string? User, string? Password)> AuthenticateAsync(string argument, StreamReader reader, StreamWriter writer)
    {
        var parts = argument.Split(' ');
        if (parts[0].Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = parts.Length > 1 ? parts[1] : await Ask(reader, writer, string.Empty);
            var fields = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\0');
            return (fields[1], fields[2]);
        }

        var user = Encoding.UTF8.GetString(Convert.FromBase64String(parts.Length > 1 ? parts[1] : await Ask(reader, writer, "Username:")));
        var password = Encoding.UTF8.GetString(Convert.FromBase64String(await Ask(reader, writer, "Password:")));
        return (user, password);
    }

    private static async Task<string> Ask(StreamReader reader, StreamWriter writer, string prompt)
    {
        await writer.WriteLineAsync("334 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(prompt)));
        return await reader.ReadLineAsync() ?? string.Empty;
    }

    private static string Address(string argument)
    {
        var start = argument.IndexOf('<', StringComparison.Ordinal);
        var end = argument.IndexOf('>', StringComparison.Ordinal);
        return start >= 0 && end > start ? argument[(start + 1)..end] : argument;
    }
}
