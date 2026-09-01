using System.Security.Claims;
using KeyManagement.Domain;
using KeyManagement.Domain.Access;

namespace KeyManagement.Server;

/// <summary>
/// Names the authorization policies and reads the caller out of a request.
/// </summary>
public static class Authorization
{
    /// <summary>Policy requiring the permission to request and return assets.</summary>
    public const string CanCheckOut = nameof(Permissions.CheckoutAsset);

    /// <summary>Policy requiring the permission to manage holders and roles.</summary>
    public const string CanManageUsers = nameof(Permissions.ManageUsers);

    /// <summary>Policy requiring the permission to acknowledge alarms.</summary>
    public const string CanAcknowledgeAlarms = nameof(Permissions.AcknowledgeAlarm);

    /// <summary>Policy requiring the permission to read the audit trail.</summary>
    public const string CanViewAudit = nameof(Permissions.ViewAudit);

    /// <summary>Every policy, each requiring the matching permission claim.</summary>
    public static readonly string[] All =
        [CanCheckOut, CanManageUsers, CanAcknowledgeAlarms, CanViewAudit];

    /// <summary>
    /// Reads the signed-in holder from the token.
    /// </summary>
    /// <param name="principal">The caller.</param>
    /// <returns>The holder's identifier.</returns>
    /// <exception cref="InvalidOperationException">
    /// The token carries no usable subject. Authentication has already run by the time this is
    /// reached, so it means the token was issued wrongly rather than that the caller is
    /// anonymous.
    /// </exception>
    public static UserId RequireUserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // JwtBearer may or may not map "sub" onto NameIdentifier depending on whether the
        // inbound claim map is cleared, so both are checked rather than relying on one.
        var subject = principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(subject, out var id)
            ? new UserId(id)
            : throw new InvalidOperationException("The access token carries no usable subject.");
    }
}
