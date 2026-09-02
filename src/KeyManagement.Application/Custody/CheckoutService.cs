using KeyManagement.Application.Abstractions;
using KeyManagement.Contracts;
using KeyManagement.Domain;
using KeyManagement.Domain.Access;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Auditing;
using KeyManagement.Domain.Cabinets;
using KeyManagement.Domain.Custody;

namespace KeyManagement.Application.Custody;

/// <summary>
/// Decides custody requests and records what was decided.
/// </summary>
/// <remarks>
/// <para>
/// This is where authorization actually happens. The client hiding a button is presentation;
/// a request that reaches here is judged on the holder's permissions and group grants
/// regardless of what the interface offered.
/// </para>
/// <para>
/// A refusal is a recorded outcome, not an error. It produces a <see cref="Checkout"/> in
/// <see cref="CheckoutState.Denied"/> and an audit record, because a trail that keeps only
/// successes cannot tell "nothing happened" from "it was refused".
/// </para>
/// </remarks>
public sealed class CheckoutService
{
    private readonly IUserRepository _users;
    private readonly IAssetRepository _assets;
    private readonly ICabinetRepository _cabinets;
    private readonly ICheckoutRepository _checkouts;
    private readonly IAuditTrail _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly ICustodyEventPublisher _events;
    private readonly ICabinetGateway _gateway;

    /// <summary>Creates the service.</summary>
    /// <param name="users">Holders.</param>
    /// <param name="assets">Assets.</param>
    /// <param name="cabinets">Cabinets and slots.</param>
    /// <param name="checkouts">Custody requests.</param>
    /// <param name="audit">Records what happened.</param>
    /// <param name="unitOfWork">Commits the work.</param>
    /// <param name="clock">The current time.</param>
    /// <param name="events">Pushes what happened to connected clients.</param>
    /// <param name="gateway">Sends the release to the cabinet holding the item.</param>
    public CheckoutService(
        IUserRepository users,
        IAssetRepository assets,
        ICabinetRepository cabinets,
        ICheckoutRepository checkouts,
        IAuditTrail audit,
        IUnitOfWork unitOfWork,
        IClock clock,
        ICustodyEventPublisher events,
        ICabinetGateway gateway)
    {
        _users = users;
        _assets = assets;
        _cabinets = cabinets;
        _checkouts = checkouts;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _events = events;
        _gateway = gateway;
    }

    /// <summary>Judges a request to take custody of an asset.</summary>
    /// <param name="request">What is wanted.</param>
    /// <param name="requestedBy">The holder asking.</param>
    /// <param name="correlationId">Ties this command to its records.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The resulting checkout, permitted or refused.</returns>
    public async Task<CommandResult<CheckoutSummary>> RequestAsync(
        CheckoutRequest request,
        UserId requestedBy,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        AuditEvent? outcome = null;
        (CabinetId Cabinet, string Position)? release = null;

        var result = await _unitOfWork.InTransactionAsync(async token =>
        {
            var now = _clock.UtcNow;
            var user = await _users.FindByIdAsync(requestedBy, token).ConfigureAwait(false);
            var asset = await _assets.FindByIdAsync(new AssetId(request.AssetId), token)
                .ConfigureAwait(false);

            if (user is null || asset is null)
            {
                return NotFound(correlationId, asset is null ? "asset" : "holder");
            }

            var slot = await _cabinets.FindSlotHoldingAsync(asset.Id, token).ConfigureAwait(false);

            _audit.Record(new AuditEvent(
                    AuditEventType.CheckoutRequested,
                    now,
                    correlationId,
                    $"'{user.UserName}' requested {asset.Reference}.")
                .About(user.Id)
                .About(asset.Id));

            if (Refuse(user, asset, slot, slot is not null && _gateway.IsAttached(slot.CabinetId)) is { } reason)
            {
                var (refused, refusal) = await RecordRefusalAsync(
                    user, asset, slot, reason, now, correlationId, token).ConfigureAwait(false);
                outcome = refusal;
                return refused;
            }

            // Slot is non-null here: an unassigned asset is refused above.
            var checkout = Checkout.Authorize(
                asset.Id, user.Id, slot!.CabinetId, slot.Id, correlationId, now, request.DueAt);
            _checkouts.Add(checkout);

            // The decision is recorded before the cabinet is told anything. The device layer
            // picks this up and sends the unlock; it never decides for itself.
            asset.BeginCheckout();

            outcome = new AuditEvent(
                    AuditEventType.CheckoutAuthorized,
                    now,
                    correlationId,
                    $"Authorized {asset.Reference} to '{user.UserName}'.")
                .About(user.Id)
                .About(asset.Id);
            _audit.Record(outcome);

            release = (slot.CabinetId, slot.Position);

            await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);

            return new CommandResult<CheckoutSummary>(
                true,
                $"{asset.Reference} is released. Take it from {slot.Position}.",
                correlationId.Value,
                checkout.State.ToString(),
                Describe(checkout, asset, user));
        }, cancellationToken).ConfigureAwait(false);

        await PublishAsync(outcome, cancellationToken).ConfigureAwait(false);

        // Only now is the cabinet told. The decision is recorded first, so a release always has
        // an authorization behind it that survives whatever the device does next.
        if (result.Success && release is { } instruction)
        {
            await _gateway
                .UnlockAsync(instruction.Cabinet, instruction.Position, correlationId, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Starts the return of an asset that is out.</summary>
    /// <param name="checkoutId">The checkout being closed.</param>
    /// <param name="requestedBy">The holder returning it.</param>
    /// <param name="correlationId">Ties this command to its records.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The checkout as it now stands.</returns>
    /// <remarks>
    /// This begins a return rather than completing one. The asset is not back until the
    /// cabinet says so, so it moves to <see cref="AssetCustodyState.ReturnPending"/> and the
    /// device layer confirms it.
    /// </remarks>
    public async Task<CommandResult<CheckoutSummary>> ReturnAsync(
        CheckoutId checkoutId,
        UserId requestedBy,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default)
    {
        AuditEvent? outcome = null;

        var result = await _unitOfWork.InTransactionAsync(async token =>
        {
            var now = _clock.UtcNow;
            var checkout = await _checkouts.FindByIdAsync(checkoutId, token).ConfigureAwait(false);

            if (checkout is null)
            {
                return NotFound(correlationId, "checkout");
            }

            var user = await _users.FindByIdAsync(requestedBy, token).ConfigureAwait(false);
            var asset = await _assets.FindByIdAsync(checkout.AssetId, token).ConfigureAwait(false);

            if (user is null || asset is null)
            {
                return NotFound(correlationId, asset is null ? "asset" : "holder");
            }

            if (checkout.State is not (CheckoutState.Active or CheckoutState.Overdue))
            {
                return Refused(
                    $"That checkout is {checkout.State} and cannot be returned.",
                    correlationId,
                    checkout.State.ToString());
            }

            asset.BeginReturn();

            outcome = new AuditEvent(
                    AuditEventType.ReturnRequested,
                    now,
                    correlationId,
                    $"'{user.UserName}' started returning {asset.Reference}.")
                .About(user.Id)
                .About(asset.Id);
            _audit.Record(outcome);

            await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);

            return new CommandResult<CheckoutSummary>(
                true,
                $"Put {asset.Reference} back in its slot.",
                correlationId.Value,
                checkout.State.ToString(),
                Describe(checkout, asset, user));
        }, cancellationToken).ConfigureAwait(false);

        await PublishAsync(outcome, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Why the request must be refused, or <see langword="null"/> if it may proceed.
    /// </summary>
    /// <remarks>
    /// Every reason is a sentence for the person at the workstation. The order matters: the
    /// most specific reason wins, so a suspended holder is told that rather than being told
    /// about group membership.
    /// </remarks>
    private static string? Refuse(User user, Asset asset, Slot? slot, bool cabinetAttached) => user switch
    {
        { Status: not UserStatus.Active } =>
            "This account is not active.",
        _ when !user.Can(Permissions.CheckoutAsset) =>
            "You do not have permission to check out assets.",
        _ when !user.CanCheckOutFrom(asset.AssetGroupId) =>
            "This asset is not in a group you may check out from.",
        _ when slot is null =>
            "This item is not assigned to a position, so no cabinet can release it.",

        // A key cannot come out of a cabinet the server cannot reach. Authorizing one anyway
        // would leave a checkout waiting on a release that was never sent.
        _ when !cabinetAttached =>
            "The cabinet holding this item is not connected.",
        _ when asset.IsUncertain =>
            "This asset's whereabouts are not confirmed. It must be reconciled first.",
        _ when asset.CustodyState != AssetCustodyState.Available =>
            $"This asset is {asset.CustodyState} and cannot be taken.",
        _ => null,
    };

    private static CommandResult<CheckoutSummary> NotFound(CorrelationId correlationId, string what) =>
        new(false, $"No such {what}.", correlationId.Value, "NotFound", null);

    private static CommandResult<CheckoutSummary> Refused(
        string message,
        CorrelationId correlationId,
        string state) =>
        new(false, message, correlationId.Value, state, null);

    private static CheckoutSummary Describe(Checkout checkout, Asset asset, User user) =>
        new(
            checkout.Id.Value,
            asset.Id.Value,
            asset.Reference,
            user.Id.Value,
            user.DisplayName,
            checkout.State.ToString(),
            checkout.RequestedAt,
            checkout.TakenAt,
            checkout.DueAt,
            checkout.ReturnedAt,
            checkout.DenialReason);

    private async Task<(CommandResult<CheckoutSummary> Result, AuditEvent Outcome)> RecordRefusalAsync(
        User user,
        Asset asset,
        Slot? slot,
        string reason,
        DateTimeOffset now,
        CorrelationId correlationId,
        CancellationToken cancellationToken)
    {
        // A refusal is still a checkout record. Without one there is nothing tying the audit
        // entry to a request, and no way to count how often a holder is turned away.
        var denied = Checkout.Deny(
            asset.Id,
            user.Id,
            slot?.CabinetId ?? default,
            slot?.Id ?? default,
            correlationId,
            now,
            reason);

        // Only persist the record when it names a real slot. A foreign key to a slot that
        // does not exist would fail the write and lose the audit entry with it.
        if (slot is not null)
        {
            _checkouts.Add(denied);
        }

        var refusal = new AuditEvent(
                AuditEventType.CheckoutDenied,
                now,
                correlationId,
                $"Refused {asset.Reference} to '{user.UserName}': {reason}")
            .About(user.Id)
            .About(asset.Id);
        _audit.Record(refusal);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var result = new CommandResult<CheckoutSummary>(
            false, reason, correlationId.Value, CheckoutState.Denied.ToString(), null);

        return (result, refusal);
    }

    /// <summary>
    /// Announces the outcome to connected clients, after the transaction has committed.
    /// </summary>
    /// <remarks>
    /// After, never inside. Pushing from within the transaction would announce a change that a
    /// later rollback undoes, leaving every client showing something that never happened.
    /// </remarks>
    private Task PublishAsync(AuditEvent? outcome, CancellationToken cancellationToken)
    {
        if (outcome is null)
        {
            return Task.CompletedTask;
        }

        return _events.PublishAsync(
            new AuditEventSummary(
                outcome.Id.Value,
                outcome.Type.ToString(),
                outcome.OccurredAt,
                outcome.CorrelationId.Value,
                outcome.Summary,
                outcome.UserId?.Value,
                outcome.AssetId?.Value,
                outcome.CabinetId?.Value),
            cancellationToken);
    }
}
