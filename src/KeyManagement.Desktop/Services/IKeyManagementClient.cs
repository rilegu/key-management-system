using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KeyManagement.Contracts;

namespace KeyManagement.Desktop.Services;

/// <summary>
/// Everything the client can ask the server for.
/// </summary>
/// <remarks>
/// The only way out of the desktop application. There is no database connection, no device
/// protocol and no authorization logic on this side of the wire: what the interface offers is
/// presentation, and every request is judged again on the server.
/// </remarks>
public interface IKeyManagementClient
{
    /// <summary>The signed-in holder, or <see langword="null"/> before sign-in.</summary>
    SessionResponse? Session { get; }

    /// <summary>Exchanges credentials for a session.</summary>
    /// <param name="userName">Sign-in name.</param>
    /// <param name="password">Password.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The session, or a refusal.</returns>
    Task<CommandResult<SessionResponse>> SignInAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>Ends the session held by this client.</summary>
    void SignOut();

    /// <summary>Reads the dashboard.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Cabinets, open checkouts, uncertain items and recent activity.</returns>
    Task<DashboardSummary> GetDashboardAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists items.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Every item and where it is.</returns>
    Task<IReadOnlyList<AssetSummary>> ListItemsAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists cabinets.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Every cabinet and its link state.</returns>
    Task<IReadOnlyList<CabinetSummary>> ListCabinetsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one cabinet position by position.</summary>
    /// <param name="cabinetId">The cabinet.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The snapshot, or <see langword="null"/> if there is no such cabinet.</returns>
    Task<CabinetSnapshot?> GetCabinetSnapshotAsync(
        Guid cabinetId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists what is currently out.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The open checkouts.</returns>
    Task<IReadOnlyList<CheckoutSummary>> ListOpenCheckoutsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Requests custody of an item.</summary>
    /// <param name="itemId">The item wanted.</param>
    /// <param name="curfew">When it will be returned, or <see langword="null"/> if open-ended.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome, which may be a refusal with its reason.</returns>
    Task<CommandResult<CheckoutSummary>> RequestItemAsync(
        Guid itemId,
        DateTimeOffset? curfew,
        CancellationToken cancellationToken = default);

    /// <summary>Starts returning an item that is out.</summary>
    /// <param name="checkoutId">The checkout being closed.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome.</returns>
    Task<CommandResult<CheckoutSummary>> ReturnItemAsync(
        Guid checkoutId,
        CancellationToken cancellationToken = default);

    /// <summary>Searches the activity trail, newest first.</summary>
    /// <param name="query">How to narrow the search.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The matching records.</returns>
    Task<IReadOnlyList<AuditEventSummary>> SearchActivityAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised when the server cannot be reached or answers unexpectedly.
/// </summary>
public sealed class KeyManagementClientException : Exception
{
    /// <summary>Creates an exception with no detail.</summary>
    public KeyManagementClientException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong, written for the person at the workstation.</param>
    public KeyManagementClientException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an underlying cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public KeyManagementClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
