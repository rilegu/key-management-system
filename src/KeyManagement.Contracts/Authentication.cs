namespace KeyManagement.Contracts;

/// <summary>Credentials offered at sign-in.</summary>
/// <param name="UserName">The holder's sign-in name.</param>
/// <param name="Password">The plaintext password. Sent over TLS, never logged, never stored.</param>
public sealed record LoginRequest(string UserName, string Password);

/// <summary>A refresh token being exchanged for a new access token.</summary>
/// <param name="RefreshToken">The token issued at sign-in.</param>
public sealed record RefreshRequest(string RefreshToken);

/// <summary>A established session.</summary>
/// <param name="AccessToken">Short-lived bearer token for API calls.</param>
/// <param name="RefreshToken">Long-lived, revocable, exchanged for new access tokens.</param>
/// <param name="ExpiresAt">When the access token stops being accepted, UTC.</param>
/// <param name="UserId">The holder this session belongs to.</param>
/// <param name="DisplayName">Name to show in the interface.</param>
/// <param name="Permissions">
/// What the holder may do. Sent so the client can hide what it should not offer; it is never
/// what grants anything, since every request is authorized again on the server.
/// </param>
public sealed record SessionResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    string DisplayName,
    IReadOnlyList<string> Permissions);
