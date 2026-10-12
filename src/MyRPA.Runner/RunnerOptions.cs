using System.Text.Json;
using MyRPA.Orchestration;

namespace MyRPA.Runner;

/// <summary>A Runner's configuration (ADR-0049). The token is a secret: it is never logged, printed or serialized.</summary>
public sealed record RunnerOptions
{
    /// <summary>The Server's Runner listener (https, or http on loopback only).</summary>
    public required Uri ServerUrl { get; init; }

    /// <summary>The Runner token (from a token file or an environment variable).</summary>
    public required string Token { get; init; }

    /// <summary>
    /// Optional SHA-256 (hex) of the Server certificate: when set, exactly that certificate is trusted (for a self-signed
    /// Server certificate); when not set, normal certificate validation applies. Validation is never switched off.
    /// </summary>
    public string? ServerCertificateSha256 { get; init; }

    /// <summary>Plugin directories (required plugins).</summary>
    public IReadOnlyList<string> PluginDirectories { get; init; } = [];

    /// <summary>Plugin configuration file (ADR-0019): plugins, pins, settings (AI provider, MCP servers, file roots).</summary>
    public string? PluginConfiguration { get; init; }

    /// <summary>Where job packages are unpacked while they run (each job in its own folder, removed afterwards).</summary>
    public required string WorkDirectory { get; init; }

    /// <summary>Jobs run at the same time (the Server may cap it lower).</summary>
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>How long one claim request waits for a job.</summary>
    public TimeSpan ClaimWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a shutdown waits for running jobs to stop and report.</summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public override string ToString() => $"Runner for {ServerUrl} (work: {WorkDirectory}, concurrency {MaxConcurrency})";
}

/// <summary>Reads <c>runner.json</c> (ADR-0049).</summary>
public static class RunnerConfiguration
{
    /// <summary>The default environment variable holding the Runner token.</summary>
    public const string DefaultTokenVariable = "MYRPA_RUNNER_TOKEN";

    /// <summary>
    /// Loads a configuration file. Relative paths resolve from the file's folder. The token comes from <c>tokenFile</c>
    /// or from the environment variable <c>tokenEnvironmentVariable</c> (default <see cref="DefaultTokenVariable"/>).
    /// </summary>
    public static RunnerOptions? Load(string path, Func<string, string?> environment, out string? error)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            error = $"The Runner configuration '{full}' does not exist.";
            return null;
        }

        var folder = Path.GetDirectoryName(full)!;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(full), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The Runner configuration must be a JSON object.";
                return null;
            }

            var known = new HashSet<string>(StringComparer.Ordinal)
            {
                "serverUrl", "tokenFile", "tokenEnvironmentVariable", "serverCertificateSha256", "pluginConfig", "plugins", "workDirectory", "maxConcurrency", "claimWaitSeconds", "shutdownGraceSeconds",
            };
            if (root.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !known.Contains(n)) is { } unknown)
            {
                error = $"Unknown setting '{unknown}' in the Runner configuration.";
                return null;
            }

            if (!Uri.TryCreate(Text(root, "serverUrl"), UriKind.Absolute, out var server) || server.Scheme is not ("http" or "https"))
            {
                error = "'serverUrl' must be the Server's http(s) address.";
                return null;
            }

            if (server.Scheme == "http" && !server.IsLoopback)
            {
                error = "'serverUrl' must use https unless the Server is on this machine (loopback): the token must not cross a network unencrypted.";
                return null;
            }

            string? token;
            if (Text(root, "tokenFile") is { } tokenFile)
            {
                var tokenPath = Path.GetFullPath(Path.Combine(folder, tokenFile));
                token = File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : null;
                if (token is null)
                {
                    error = $"The token file '{tokenPath}' does not exist.";
                    return null;
                }
            }
            else
            {
                var variable = Text(root, "tokenEnvironmentVariable") ?? DefaultTokenVariable;
                token = environment(variable)?.Trim();
                if (string.IsNullOrEmpty(token))
                {
                    error = $"No Runner token: set the environment variable {variable} or 'tokenFile'.";
                    return null;
                }
            }

            if (!TokenSecrets.TryParse(token, TokenSecrets.RunnerPrefix, out _, out _))
            {
                error = "The Runner token is not a Runner token (it starts with myrpa_runner_).";
                return null;
            }

            var pin = Text(root, "serverCertificateSha256")?.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            if (pin is not null && (pin.Length != 64 || !pin.All(Uri.IsHexDigit)))
            {
                error = "'serverCertificateSha256' must be a SHA-256 hash in hex.";
                return null;
            }

            var maxConcurrency = Number(root, "maxConcurrency") ?? 1;
            var claimWait = Number(root, "claimWaitSeconds") ?? 20;
            var grace = Number(root, "shutdownGraceSeconds") ?? 30;
            if (maxConcurrency is < 1 or > 64 || claimWait is < 1 or > 25 || grace is < 1 or > 3600)
            {
                error = "'maxConcurrency' must be 1–64, 'claimWaitSeconds' 1–25 and 'shutdownGraceSeconds' 1–3600.";
                return null;
            }

            var plugins = root.TryGetProperty("plugins", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(p => Path.GetFullPath(Path.Combine(folder, p.GetString() ?? string.Empty))).ToList()
                : [];
            error = null;
            return new RunnerOptions
            {
                ServerUrl = server,
                Token = token,
                ServerCertificateSha256 = pin,
                PluginDirectories = plugins,
                PluginConfiguration = Text(root, "pluginConfig") is { } config ? Path.GetFullPath(Path.Combine(folder, config)) : null,
                WorkDirectory = Path.GetFullPath(Path.Combine(folder, Text(root, "workDirectory") ?? "work")),
                MaxConcurrency = maxConcurrency,
                ClaimWait = TimeSpan.FromSeconds(claimWait),
                ShutdownGrace = TimeSpan.FromSeconds(grace),
            };
        }
        catch (JsonException ex)
        {
            error = $"The Runner configuration is not valid JSON: {ex.Message}";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"The Runner configuration could not be read: {ex.Message}";
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    private static int? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
