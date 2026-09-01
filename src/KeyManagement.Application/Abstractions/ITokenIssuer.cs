using KeyManagement.Domain.Access;

namespace KeyManagement.Application.Abstractions;

/// <summary>
/// Mints access tokens and the opaque refresh tokens that buy them.
/// </summary>
public interface ITokenIssuer
{
    /// <summary>How long an access token is accepted for.</summary>
    TimeSpan AccessTokenLifetime { get; }

    /// <summary>How long a refresh token is accepted for.</summary>
    TimeSpan RefreshTokenLifetime { get; }

    /// <summary>Mints a signed access token carrying the holder's identity and permissions.</summary>
    /// <param name="user">The holder.</param>
    /// <param name="issuedAt">When the token is issued, UTC.</param>
    /// <returns>The encoded token.</returns>
    string IssueAccessToken(User user, DateTimeOffset issuedAt);

    /// <summary>
    /// Mints a refresh token and the hash to store for it.
    /// </summary>
    /// <returns>The token to hand the caller, and the hash to keep.</returns>
    /// <remarks>
    /// Opaque and random rather than signed. Nothing needs to read it, and a token the server
    /// must look up is a token the server can revoke.
    /// </remarks>
    (string Token, string TokenHash) IssueRefreshToken();

    /// <summary>Hashes a presented refresh token so it can be looked up.</summary>
    /// <param name="token">The presented token.</param>
    /// <returns>The hash to search for.</returns>
    string HashRefreshToken(string token);
}
