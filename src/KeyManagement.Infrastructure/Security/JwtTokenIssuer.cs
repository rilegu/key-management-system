using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using KeyManagement.Application.Abstractions;
using KeyManagement.Domain.Access;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace KeyManagement.Infrastructure.Security;

/// <summary>
/// How access and refresh tokens are minted and validated.
/// </summary>
public sealed class JwtOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "Jwt";

    /// <summary>Who issued the token.</summary>
    public string Issuer { get; set; } = "key-management-system";

    /// <summary>Who the token is for.</summary>
    public string Audience { get; set; } = "key-management-system";

    /// <summary>
    /// Signing key. At least 32 bytes, since HMAC-SHA256 is the algorithm.
    /// </summary>
    /// <remarks>
    /// Supplied by configuration and never committed. The server refuses to start without one
    /// rather than falling back to a built-in default, which would be the same key on every
    /// deployment and therefore no key at all.
    /// </remarks>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>How long an access token is accepted for.</summary>
    /// <remarks>Short, because it cannot be revoked; the refresh token is the revocable half.</remarks>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a refresh token is accepted for.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Mints signed access tokens and opaque refresh tokens.
/// </summary>
public sealed class JwtTokenIssuer : ITokenIssuer
{
    /// <summary>Claim carrying one permission the holder has.</summary>
    public const string PermissionClaimType = "kms:permission";

    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;

    /// <summary>Creates the issuer.</summary>
    /// <param name="options">Signing and lifetime configuration.</param>
    /// <exception cref="ArgumentException">The signing key is missing or too short.</exception>
    public JwtTokenIssuer(JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
        {
            throw new ArgumentException(
                "A signing key of at least 32 bytes is required.", nameof(options));
        }

        _options = options;
        _credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
    }

    /// <inheritdoc />
    public TimeSpan AccessTokenLifetime => _options.AccessTokenLifetime;

    /// <inheritdoc />
    public TimeSpan RefreshTokenLifetime => _options.RefreshTokenLifetime;

    /// <inheritdoc />
    public string IssueAccessToken(User user, DateTimeOffset issuedAt)
    {
        ArgumentNullException.ThrowIfNull(user);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.GivenName, user.DisplayName),
        };

        // One claim per permission rather than a packed value, so an authorization policy is
        // a claim requirement and needs no parsing.
        foreach (var permission in Enum.GetValues<Permissions>())
        {
            if (permission != Permissions.None && user.EffectivePermissions.HasFlag(permission))
            {
                claims.Add(new Claim(PermissionClaimType, permission.ToString()));
            }
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = issuedAt.Add(_options.AccessTokenLifetime).UtcDateTime,
            SigningCredentials = _credentials,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <inheritdoc />
    public (string Token, string TokenHash) IssueRefreshToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (token, HashRefreshToken(token));
    }

    /// <inheritdoc />
    public string HashRefreshToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // SHA-256, not a password hash. The token is 256 bits of randomness, so there is
        // nothing to brute force and no salt to add; the hash exists only so a stolen
        // database yields no usable token.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
