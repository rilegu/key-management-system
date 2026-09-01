using KeyManagement.Application.Abstractions;
using KeyManagement.Contracts;
using KeyManagement.Domain;
using KeyManagement.Domain.Access;
using KeyManagement.Domain.Auditing;

namespace KeyManagement.Application.Authentication;

/// <summary>
/// Establishes and renews sessions.
/// </summary>
public sealed class SignInService
{
    private readonly IUserRepository _users;
    private readonly IRefreshTokenStore _refreshTokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenIssuer _tokens;
    private readonly IAuditTrail _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    /// <summary>Creates the service.</summary>
    /// <param name="users">Holders.</param>
    /// <param name="refreshTokens">Issued refresh tokens.</param>
    /// <param name="passwordHasher">Verifies passwords.</param>
    /// <param name="tokens">Mints access and refresh tokens.</param>
    /// <param name="audit">Records what happened.</param>
    /// <param name="unitOfWork">Commits the work.</param>
    /// <param name="clock">The current time.</param>
    public SignInService(
        IUserRepository users,
        IRefreshTokenStore refreshTokens,
        IPasswordHasher passwordHasher,
        ITokenIssuer tokens,
        IAuditTrail audit,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _passwordHasher = passwordHasher;
        _tokens = tokens;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    /// <summary>Signs a holder in.</summary>
    /// <param name="request">The credentials offered.</param>
    /// <param name="correlationId">Ties this attempt to its audit records.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A session, or a refusal.</returns>
    /// <remarks>
    /// An unknown account and a wrong password return the same message, and both do the same
    /// work: the hash is verified even when there is no holder, so the response time does not
    /// say which accounts exist.
    /// </remarks>
    public async Task<CommandResult<SessionResponse>> SignInAsync(
        LoginRequest request,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        var user = await _users.FindByUserNameAsync(request.UserName, cancellationToken)
            .ConfigureAwait(false);

        var verified = VerifyOrDecoy(user, request.Password);

        if (user is null || verified == PasswordVerification.Failed)
        {
            _audit.Record(Refusal(
                AuditEventType.SignInFailed,
                now,
                correlationId,
                $"Sign-in refused for '{request.UserName}'.",
                user?.Id));
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Refused("The user name or password is not correct.", correlationId);
        }

        if (user.Status != UserStatus.Active)
        {
            _audit.Record(Refusal(
                AuditEventType.SignInFailed,
                now,
                correlationId,
                $"Sign-in refused for '{request.UserName}': account is {user.Status}.",
                user.Id));
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Refused("This account is not active. Contact an administrator.", correlationId);
        }

        var session = await IssueSessionAsync(user, now, cancellationToken).ConfigureAwait(false);

        _audit.Record(new AuditEvent(
            AuditEventType.SignInSucceeded, now, correlationId, $"'{user.UserName}' signed in.")
            .About(user.Id));
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CommandResult<SessionResponse>(
            true, "Signed in.", correlationId.Value, "Active", session);
    }

    /// <summary>Exchanges a refresh token for a new session.</summary>
    /// <param name="request">The token being exchanged.</param>
    /// <param name="correlationId">Ties this attempt to its audit records.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A session, or a refusal.</returns>
    /// <remarks>
    /// The presented token is revoked as it is exchanged, so each one is good for a single
    /// use. A token replayed after that fails, which is how a stolen one stops working once
    /// the rightful holder has used it.
    /// </remarks>
    public async Task<CommandResult<SessionResponse>> RefreshAsync(
        RefreshRequest request,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        var hash = _tokens.HashRefreshToken(request.RefreshToken);
        var existing = await _refreshTokens.FindByHashAsync(hash, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null || !existing.IsUsableAt(now))
        {
            _audit.Record(Refusal(
                AuditEventType.SignInFailed,
                now,
                correlationId,
                "Refresh refused: token is unknown, expired or already used.",
                existing?.UserId));
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Refused("Your session has ended. Sign in again.", correlationId);
        }

        var user = await _users.FindByIdAsync(existing.UserId, cancellationToken)
            .ConfigureAwait(false);

        if (user is null || user.Status != UserStatus.Active)
        {
            existing.Revoke(now);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Refused("This account is not active. Contact an administrator.", correlationId);
        }

        existing.Revoke(now);
        var session = await IssueSessionAsync(user, now, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CommandResult<SessionResponse>(
            true, "Session renewed.", correlationId.Value, "Active", session);
    }

    private static CommandResult<SessionResponse> Refused(string message, CorrelationId correlationId) =>
        new(false, message, correlationId.Value, "Denied", null);

    private static AuditEvent Refusal(
        AuditEventType type,
        DateTimeOffset at,
        CorrelationId correlationId,
        string summary,
        UserId? userId)
    {
        var record = new AuditEvent(type, at, correlationId, summary);
        return userId is { } id ? record.About(id) : record;
    }

    private PasswordVerification VerifyOrDecoy(User? user, string password)
    {
        if (user is not null)
        {
            return _passwordHasher.Verify(user.PasswordHash, password);
        }

        // No such holder. Hash anyway against a throwaway value so an unknown account costs
        // the same as a wrong password; otherwise the response time enumerates valid accounts.
        _passwordHasher.Verify(_passwordHasher.Hash("decoy"), password);
        return PasswordVerification.Failed;
    }

    private Task<SessionResponse> IssueSessionAsync(
        User user,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var accessToken = _tokens.IssueAccessToken(user, now);
        var (refreshToken, refreshHash) = _tokens.IssueRefreshToken();

        _refreshTokens.Add(new RefreshToken(
            user.Id, refreshHash, now, now.Add(_tokens.RefreshTokenLifetime)));

        var permissions = Enum.GetValues<Permissions>()
            .Where(p => p != Permissions.None && user.EffectivePermissions.HasFlag(p))
            .Select(p => p.ToString())
            .ToArray();

        return Task.FromResult(new SessionResponse(
            accessToken,
            refreshToken,
            now.Add(_tokens.AccessTokenLifetime),
            user.Id.Value,
            user.DisplayName,
            permissions));
    }
}
