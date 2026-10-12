namespace MyRPA.Orchestration;

/// <summary>Access tokens, the execution policy and the audit history (ADR-0051).</summary>
public sealed partial class Orchestrator
{
    /// <summary>Creates an access token with a role; returns it once (only its hash is stored).</summary>
    public async Task<(AccessTokenRecord Record, string Token)> CreateAccessTokenAsync(Actor actor, string name, Role role, CancellationToken cancellationToken)
    {
        Require(actor, Role.Admin);
        name = (name ?? string.Empty).Trim();
        if (name.Length is 0 or > 80 || !Enum.IsDefined(role))
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, "A token needs a name (up to 80 characters) and a role.");
        }

        var id = _ids.NewId("tok");
        var (token, hash) = TokenSecrets.Create(TokenSecrets.AccessPrefix, id);
        var record = new AccessTokenRecord { TokenId = id, Name = name, Role = role, Hash = hash, CreatedAt = _time.GetUtcNow(), CreatedBy = actor.Name };
        await _store.InsertAccessTokenAsync(record, cancellationToken).ConfigureAwait(false);
        await AuditAsync(actor, "token.create", id, $"{name} ({role})", cancellationToken).ConfigureAwait(false);
        return (record, token);
    }

    /// <summary>Revokes an access token.</summary>
    public async Task<AccessTokenRecord> RevokeAccessTokenAsync(Actor actor, string tokenId, CancellationToken cancellationToken)
    {
        Require(actor, Role.Admin);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await _store.GetAccessTokenAsync(tokenId, cancellationToken).ConfigureAwait(false)
                ?? throw new OrchestrationException(OrchestrationFailure.NotFound, $"Unknown token '{tokenId}'.");
            var revoked = record with { RevokedAt = record.RevokedAt ?? _time.GetUtcNow() };
            await UpdateOrThrowAsync(_store.UpdateAccessTokenAsync(revoked, cancellationToken)).ConfigureAwait(false);
            await AuditAsync(actor, "token.revoke", tokenId, record.Name, cancellationToken).ConfigureAwait(false);
            return revoked;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The actor an access token stands for, if it is valid and not revoked.</summary>
    public async Task<Actor?> AuthenticateAccessTokenAsync(string? token, CancellationToken cancellationToken)
    {
        if (!TokenSecrets.TryParse(token, TokenSecrets.AccessPrefix, out var id, out var secret))
        {
            return null;
        }

        var record = await _store.GetAccessTokenAsync(id, cancellationToken).ConfigureAwait(false);
        return record is { RevokedAt: null } && TokenSecrets.Verify(secret, record.Hash) ? new Actor($"token:{record.Name}", record.Role) : null;
    }

    /// <summary>Changes the execution policy.</summary>
    public async Task<OrchestrationPolicy> SetPolicyAsync(Actor actor, OrchestrationPolicy policy, CancellationToken cancellationToken)
    {
        Require(actor, Role.Admin);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaxAttempts is < 1 or > 10 || policy.MaxEventsPerJob is < 100 or > 1_000_000 || policy.OfflineAfterSeconds is < 15 or > 3600
            || policy.DefaultTimeoutMinutes is < 1 or > 10_080)
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid,
                "Attempts must be 1–10, events per job 100–1,000,000, the offline threshold 15–3,600 seconds and the default time limit 1–10,080 minutes.");
        }

        await _store.SavePolicyAsync(policy, cancellationToken).ConfigureAwait(false);
        await AuditAsync(actor, "policy.update", null, $"attempts={policy.MaxAttempts}, events={policy.MaxEventsPerJob}, offline={policy.OfflineAfterSeconds}s, timeout={policy.DefaultTimeoutMinutes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "engine default"}", cancellationToken).ConfigureAwait(false);
        return policy;
    }

    /// <summary>The audit history, newest first (administrators only).</summary>
    public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(Actor actor, int offset, int limit, CancellationToken cancellationToken)
    {
        Require(actor, Role.Admin);
        return _store.ListAuditAsync(Math.Max(0, offset), Math.Clamp(limit, 1, 500), cancellationToken);
    }
}
