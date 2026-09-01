using KeyManagement.Application.Abstractions;
using Microsoft.AspNetCore.Identity;

namespace KeyManagement.Infrastructure.Security;

/// <summary>
/// Hashes secrets with PBKDF2-HMAC-SHA512.
/// </summary>
/// <remarks>
/// Wraps <see cref="PasswordHasher{TUser}"/> used standalone, from
/// <c>Microsoft.Extensions.Identity.Core</c>. The full Identity stack brings user stores,
/// sign-in managers and a schema this system already has its own version of; the hasher on its
/// own is the only part worth taking, and it is a maintained implementation with a sensible
/// work factor rather than one written here.
/// </remarks>
public sealed class IdentityPasswordHasher : IPasswordHasher
{
    // The generic parameter is only a marker; nothing about the secret's owner is used.
    private readonly PasswordHasher<object> _hasher = new();
    private readonly object _subject = new();

    /// <inheritdoc />
    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return _hasher.HashPassword(_subject, password);
    }

    /// <inheritdoc />
    public PasswordVerification Verify(string hash, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        return _hasher.VerifyHashedPassword(_subject, hash, password) switch
        {
            PasswordVerificationResult.Success => PasswordVerification.Succeeded,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SucceededButNeedsRehash,
            _ => PasswordVerification.Failed,
        };
    }
}
