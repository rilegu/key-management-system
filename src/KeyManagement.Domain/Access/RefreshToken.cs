namespace KeyManagement.Domain.Access;

/// <summary>
/// A revocable, long-lived token that buys short-lived access tokens.
/// </summary>
/// <remarks>
/// Only the hash is stored. A stolen database therefore yields no usable token, which is the
/// whole reason for keeping these server-side rather than making the access token long-lived.
/// </remarks>
public sealed class RefreshToken
{
    private RefreshToken()
    {
        TokenHash = string.Empty;
    }

    /// <summary>Issues a token.</summary>
    /// <param name="userId">The holder it authenticates.</param>
    /// <param name="tokenHash">Hash of the token; the token itself is returned to the caller and never stored.</param>
    /// <param name="issuedAt">When it was issued, UTC.</param>
    /// <param name="expiresAt">When it stops being accepted, UTC.</param>
    public RefreshToken(UserId userId, string tokenHash, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        Id = RefreshTokenId.New();
        UserId = userId;
        TokenHash = tokenHash;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    /// <summary>Identifies this token.</summary>
    public RefreshTokenId Id { get; private set; }

    /// <summary>The holder it authenticates.</summary>
    public UserId UserId { get; private set; }

    /// <summary>Hash of the token. The token itself is never stored.</summary>
    public string TokenHash { get; private set; }

    /// <summary>When it was issued, UTC.</summary>
    public DateTimeOffset IssuedAt { get; private set; }

    /// <summary>When it stops being accepted, UTC.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When it was revoked, UTC, or <see langword="null"/> if it still stands.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Whether the token may still be exchanged.</summary>
    /// <param name="asOf">The moment to judge it at, UTC.</param>
    /// <returns><see langword="true"/> when it is neither revoked nor expired.</returns>
    public bool IsUsableAt(DateTimeOffset asOf) => RevokedAt is null && asOf < ExpiresAt;

    /// <summary>Revokes the token. Revoking an already-revoked token keeps the first time.</summary>
    /// <param name="at">When it was revoked, UTC.</param>
    public void Revoke(DateTimeOffset at) => RevokedAt ??= at;
}
