using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MyRPA.Orchestration;

/// <summary>Creates orchestration ids (jobs, leases, processes, schedules, Runners, tokens).</summary>
public interface IOrchestrationIds
{
    /// <summary>A new unique id starting with <paramref name="prefix"/> (lowercase letters and digits only).</summary>
    string NewId(string prefix);
}

/// <summary>Random ids: a prefix and 20 random base-32 characters (100 bits).</summary>
public sealed class RandomOrchestrationIds : IOrchestrationIds
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <inheritdoc />
    public string NewId(string prefix)
    {
        Span<byte> bytes = stackalloc byte[20];
        RandomNumberGenerator.Fill(bytes);
        var text = new StringBuilder(prefix.Length + 21).Append(prefix).Append('_');
        foreach (var b in bytes)
        {
            text.Append(Alphabet[b & 31]);
        }

        return text.ToString();
    }
}

/// <summary>
/// Bearer tokens (ADR-0051): <c>myrpa_runner_&lt;id&gt;.&lt;secret&gt;</c> for Runners and
/// <c>myrpa_token_&lt;id&gt;.&lt;secret&gt;</c> for access tokens. The secret is 256 random bits; only its SHA-256 is
/// stored, compared in constant time. Tokens are shown once, when created, and never logged.
/// </summary>
public static class TokenSecrets
{
    /// <summary>Prefix of Runner tokens.</summary>
    public const string RunnerPrefix = "myrpa_runner_";

    /// <summary>Prefix of access tokens.</summary>
    public const string AccessPrefix = "myrpa_token_";

    /// <summary>A new token for <paramref name="id"/>, and the hash to store.</summary>
    public static (string Token, string Hash) Create(string prefix, string id)
    {
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return ($"{prefix}{id}.{secret}", Hash(secret));
    }

    /// <summary>Splits a token into its id and secret; false for anything malformed.</summary>
    public static bool TryParse(string? token, string prefix, out string id, out string secret)
    {
        id = secret = string.Empty;
        if (token is null || token.Length > 200 || !token.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = token.AsSpan(prefix.Length);
        var dot = rest.IndexOf('.');
        if (dot <= 0 || dot == rest.Length - 1)
        {
            return false;
        }

        foreach (var c in rest[..dot])
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            {
                return false;
            }
        }

        id = rest[..dot].ToString();
        secret = rest[(dot + 1)..].ToString();
        return true;
    }

    /// <summary>SHA-256 (hex) of a secret.</summary>
    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Whether <paramref name="secret"/> matches <paramref name="storedHash"/> (constant time).</summary>
    public static bool Verify(string secret, string storedHash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(secret)), Encoding.ASCII.GetBytes(storedHash));
}
