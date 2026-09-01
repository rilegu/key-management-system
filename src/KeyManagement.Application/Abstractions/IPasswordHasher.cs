namespace KeyManagement.Application.Abstractions;

/// <summary>
/// Hashes and verifies holder passwords and cabinet PINs.
/// </summary>
/// <remarks>
/// An interface so the domain and use cases never name a hashing library, and so the work
/// factor can be raised later without touching them.
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>Hashes a secret for storage.</summary>
    /// <param name="password">The plaintext secret. Never stored or logged.</param>
    /// <returns>The hash, including its salt and parameters.</returns>
    string Hash(string password);

    /// <summary>Checks a secret against a stored hash.</summary>
    /// <param name="hash">The stored hash.</param>
    /// <param name="password">The plaintext secret to check.</param>
    /// <returns>Whether it matched, and whether the stored hash is now out of date.</returns>
    PasswordVerification Verify(string hash, string password);
}

/// <summary>
/// The outcome of checking a secret against a stored hash.
/// </summary>
public enum PasswordVerification
{
    /// <summary>Did not match.</summary>
    Failed = 0,

    /// <summary>Matched.</summary>
    Succeeded = 1,

    /// <summary>Matched, but the stored hash uses outdated parameters and should be replaced.</summary>
    SucceededButNeedsRehash = 2,
}
