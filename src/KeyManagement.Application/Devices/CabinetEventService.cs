using System.Globalization;
using KeyManagement.Application.Abstractions;
using KeyManagement.Application.Custody;
using KeyManagement.Contracts;
using KeyManagement.Domain;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Auditing;
using KeyManagement.Domain.Cabinets;
using KeyManagement.Domain.Custody;

namespace KeyManagement.Application.Devices;

/// <summary>
/// Turns what a cabinet reports into what the system of record believes.
/// </summary>
/// <remarks>
/// <para>
/// This is where the custody loop closes. Until a cabinet says a position emptied, an authorized
/// release is only an intention; until it says a position filled, a started return has not
/// happened. Nothing here trusts the device to have been authorized — it decides what an
/// observation means given what the server already permitted.
/// </para>
/// <para>
/// Free of the wire format on purpose. It takes positions, states and sequence numbers, so the
/// protocol can change shape without any of these rules moving.
/// </para>
/// </remarks>
public sealed class CabinetEventService
{
    private readonly ICabinetRepository _cabinets;
    private readonly IUserRepository _users;
    private readonly IAssetRepository _assets;
    private readonly ICheckoutRepository _checkouts;
    private readonly IDeviceEventLog _deviceEvents;
    private readonly IAuditTrail _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IClock _clock;

    /// <summary>Creates the service.</summary>
    /// <param name="cabinets">Cabinets and positions.</param>
    /// <param name="users">Holders, for verifying a PIN entered at a keypad.</param>
    /// <param name="assets">Items.</param>
    /// <param name="checkouts">Custody requests.</param>
    /// <param name="deviceEvents">What the cabinet said, as it said it.</param>
    /// <param name="audit">The trail.</param>
    /// <param name="unitOfWork">Commits the work.</param>
    /// <param name="passwordHasher">Verifies the cabinet's credential.</param>
    /// <param name="clock">The current time.</param>
    public CabinetEventService(
        ICabinetRepository cabinets,
        IUserRepository users,
        IAssetRepository assets,
        ICheckoutRepository checkouts,
        IDeviceEventLog deviceEvents,
        IAuditTrail audit,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IClock clock)
    {
        _cabinets = cabinets;
        _users = users;
        _assets = assets;
        _checkouts = checkouts;
        _deviceEvents = deviceEvents;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _clock = clock;
    }

    /// <summary>Decides whether a cabinet may attach, and what it should replay.</summary>
    /// <param name="cabinetName">The name it claims in its handshake.</param>
    /// <param name="certificateThumbprint">Fingerprint of the certificate it actually presented.</param>
    /// <param name="certificateCommonName">The name that certificate was issued to.</param>
    /// <param name="firmwareVersion">What it says it is running.</param>
    /// <param name="protocolVersion">The wire version it speaks.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Acceptance and the sequence to resume from, or a refusal.</returns>
    /// <remarks>
    /// <para>
    /// Three things must agree: the cabinet exists, the certificate it presented is the one that
    /// cabinet was enrolled with, and the name it claims matches the name on that certificate.
    /// The last check is what stops a cabinet with a perfectly valid certificate of its own from
    /// attaching as a different one.
    /// </para>
    /// <para>
    /// Every refusal reads the same, for the same reason a wrong password and an unknown account
    /// do: the answer should not tell a guesser which part they got right.
    /// </para>
    /// </remarks>
    public async Task<CabinetAttachment> AttachAsync(
        string cabinetName,
        string certificateThumbprint,
        string certificateCommonName,
        string firmwareVersion,
        int protocolVersion,
        CancellationToken cancellationToken = default)
    {
        if (protocolVersion != ProtocolVersion)
        {
            return CabinetAttachment.Refused(
                $"Protocol version {protocolVersion} is not supported.");
        }

        var cabinet = await _cabinets.FindByNameAsync(cabinetName, cancellationToken)
            .ConfigureAwait(false);

        if (cabinet?.CertificateThumbprint is null
            || string.IsNullOrEmpty(certificateThumbprint)
            || !string.Equals(
                cabinet.CertificateThumbprint, certificateThumbprint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(cabinetName, certificateCommonName, StringComparison.Ordinal))
        {
            return CabinetAttachment.Refused("This cabinet is not enrolled with that certificate.");
        }

        var now = _clock.UtcNow;
        var sessionId = Guid.CreateVersion7();

        cabinet.MarkOnline(now, firmwareVersion);

        _audit.Record(new AuditEvent(
                AuditEventType.CabinetOnline,
                now,
                new CorrelationId(sessionId),
                $"Cabinet '{cabinet.Name}' attached, firmware {firmwareVersion}.")
            .About(cabinet.Id));

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CabinetAttachment(true, cabinet.Id, sessionId, cabinet.LastAppliedSequence, null);
    }

    /// <summary>Records that a cabinet stopped answering.</summary>
    /// <param name="cabinetId">The cabinet.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when it is recorded.</returns>
    /// <remarks>
    /// Its positions become <see cref="SlotState.Unknown"/>. A stale reading presented as
    /// current is the failure this avoids: the board should say it does not know, rather than
    /// showing where things were an hour ago as though it were now.
    /// </remarks>
    public async Task MarkOfflineAsync(CabinetId cabinetId, CancellationToken cancellationToken = default)
    {
        var cabinet = await _cabinets.FindWithSlotsAsync(cabinetId, cancellationToken)
            .ConfigureAwait(false);

        if (cabinet is null || cabinet.Status == CabinetStatus.Offline)
        {
            return;
        }

        var now = _clock.UtcNow;
        cabinet.MarkOffline(now);

        _audit.Record(new AuditEvent(
                AuditEventType.CabinetOffline,
                now,
                CorrelationId.New(),
                $"Cabinet '{cabinet.Name}' stopped answering. Its positions are no longer confirmed.")
            .About(cabinet.Id));

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies one reported position change.</summary>
    /// <param name="cabinetId">The cabinet reporting.</param>
    /// <param name="report">What it says changed.</param>
    /// <param name="payload">The message as received, kept verbatim.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the server did with it.</returns>
    public Task<ReportOutcome> ApplyReportAsync(
        CabinetId cabinetId,
        PositionReport report,
        string payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        return _unitOfWork.InTransactionAsync(async token =>
        {
            var now = _clock.UtcNow;
            var cabinet = await _cabinets.FindWithSlotsAsync(cabinetId, token).ConfigureAwait(false);

            if (cabinet is null)
            {
                return ReportOutcome.Unknown;
            }

            // The whole duplicate and out-of-order story, in one line. A cabinet that replays
            // after reconnecting sends things the server already has, and applying them twice
            // would move custody twice.
            if (cabinet.HasApplied(report.Sequence))
            {
                _deviceEvents.Record(Received(cabinetId, report, payload, now, applied: false));
                await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
                return ReportOutcome.AlreadyApplied;
            }

            var slot = await _cabinets.FindSlotAsync(cabinetId, report.Position, token)
                .ConfigureAwait(false);

            if (slot is null)
            {
                _deviceEvents.Record(Received(cabinetId, report, payload, now, applied: false));
                await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
                return ReportOutcome.Unknown;
            }

            cabinet.AdvanceSequence(report.Sequence);
            slot.Report(report.State, report.At);

            var outcome = slot.AssetId is { } assetId
                ? await ApplyToItemAsync(cabinet, assetId, report, token).ConfigureAwait(false)
                : ReportOutcome.Applied;

            _deviceEvents.Record(Received(cabinetId, report, payload, now, applied: true));
            await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);

            return outcome;
        }, cancellationToken);
    }

    /// <summary>
    /// Judges a request typed at a cabinet keypad.
    /// </summary>
    /// <param name="cabinetId">The cabinet the request came from.</param>
    /// <param name="position">The position wanted.</param>
    /// <param name="userName">Who the person says they are.</param>
    /// <param name="pin">The PIN they entered.</param>
    /// <param name="correlationId">Ties the request to its answer and its audit records.</param>
    /// <param name="checkouts">The same custody service a workstation request goes through.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether the item is released, and a line for the cabinet display.</returns>
    /// <remarks>
    /// <para>
    /// The keypad only establishes who is standing there. Everything after that is
    /// <see cref="CheckoutService"/>, unchanged and unaware of where the request came from, so a
    /// holder gets the same answer at a cabinet as at a desk. Any other arrangement would mean
    /// two sets of authorization rules that have to be kept in step.
    /// </para>
    /// <para>
    /// A PIN is a second factor at a physical door, not a password. It is short by nature, which
    /// is why it is checked here against a hash and why a wrong one is audited.
    /// </para>
    /// </remarks>
    public async Task<(bool Granted, string Message)> RequestAtCabinetAsync(
        CabinetId cabinetId,
        string position,
        string userName,
        string pin,
        CorrelationId correlationId,
        CheckoutService checkouts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkouts);

        var now = _clock.UtcNow;
        var user = await _users.FindByUserNameAsync(userName, cancellationToken).ConfigureAwait(false);

        if (user?.PinHash is null
            || string.IsNullOrEmpty(pin)
            || _passwordHasher.Verify(user.PinHash, pin) == PasswordVerification.Failed)
        {
            _audit.Record(new AuditEvent(
                    AuditEventType.SignInFailed,
                    now,
                    correlationId,
                    $"PIN refused at {position} for '{userName}'.")
                .About(cabinetId));

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return (false, "That user name or PIN is not correct.");
        }

        var slot = await _cabinets.FindSlotAsync(cabinetId, position, cancellationToken)
            .ConfigureAwait(false);

        if (slot?.AssetId is not { } assetId)
        {
            return (false, "There is nothing assigned to that position.");
        }

        var result = await checkouts
            .RequestAsync(new CheckoutRequest(assetId.Value, null), user.Id, correlationId, cancellationToken)
            .ConfigureAwait(false);

        return (result.Success, result.Message);
    }

    /// <summary>The wire version this build accepts.</summary>
    public static int ProtocolVersion => 2;

    private static DeviceEvent Received(
        CabinetId cabinetId,
        PositionReport report,
        string payload,
        DateTimeOffset receivedAt,
        bool applied) =>
        new(cabinetId, report.Sequence, nameof(PositionReport), payload, report.At, receivedAt, applied);

    private async Task<ReportOutcome> ApplyToItemAsync(
        Cabinet cabinet,
        AssetId assetId,
        PositionReport report,
        CancellationToken cancellationToken)
    {
        var asset = await _assets.FindByIdAsync(assetId, cancellationToken).ConfigureAwait(false);

        if (asset is null)
        {
            return ReportOutcome.Unknown;
        }

        var checkout = await _checkouts.FindOpenForAssetAsync(assetId, cancellationToken)
            .ConfigureAwait(false);

        return report.State switch
        {
            SlotState.Empty => ItemRemoved(cabinet, asset, checkout, report),
            SlotState.Occupied => ItemReplaced(cabinet, asset, checkout, report),
            SlotState.Faulted => PositionFaulted(cabinet, asset, report),
            _ => ReportOutcome.Applied,
        };
    }

    private ReportOutcome ItemRemoved(
        Cabinet cabinet,
        Asset asset,
        Checkout? checkout,
        PositionReport report)
    {
        // The release we authorized has been collected. This is the only thing that turns an
        // intention into custody.
        if (asset.CustodyState == AssetCustodyState.CheckoutPending
            && checkout is { State: CheckoutState.Pending })
        {
            asset.ConfirmTaken();
            checkout.ConfirmTaken(report.At);

            _audit.Record(new AuditEvent(
                    AuditEventType.CheckoutCompleted,
                    report.At,
                    checkout.CorrelationId,
                    $"{asset.Reference} taken from {cabinet.Name} {report.Position}.")
                .About(checkout.UserId)
                .About(asset.Id)
                .About(cabinet.Id));

            return ReportOutcome.Applied;
        }

        // The item was already out and its whereabouts were uncertain. The cabinet confirming
        // the position is empty settles it.
        if (asset.IsUncertain && checkout is not null)
        {
            asset.Reconcile(AssetCustodyState.CheckedOut);
            Reconciled(asset, cabinet, report, "still out");
            return ReportOutcome.Applied;
        }

        // Nothing authorized this. Custody becomes uncertain rather than the trail quietly
        // recording a checkout nobody asked for, and someone is told.
        if (asset.CustodyState is AssetCustodyState.Available)
        {
            asset.MarkUnknown();

            _audit.Record(new AuditEvent(
                    AuditEventType.UnauthorizedSlotChange,
                    report.At,
                    CorrelationId.New(),
                    $"{asset.Reference} left {cabinet.Name} {report.Position} with no release authorized.")
                .About(asset.Id)
                .About(cabinet.Id));

            return ReportOutcome.Unauthorized;
        }

        return ReportOutcome.Applied;
    }

    private ReportOutcome ItemReplaced(
        Cabinet cabinet,
        Asset asset,
        Checkout? checkout,
        PositionReport report)
    {
        if (asset.CustodyState == AssetCustodyState.Available)
        {
            return ReportOutcome.Applied;
        }

        if (asset.IsUncertain)
        {
            asset.Reconcile(AssetCustodyState.Available);
            Reconciled(asset, cabinet, report, "back in its position");
        }
        else
        {
            // An item can come back without anyone starting a return: a holder simply puts it
            // in the slot. The state machine still walks the same path, so the record is the
            // same either way.
            if (asset.CustodyState == AssetCustodyState.CheckedOut)
            {
                asset.BeginReturn();
            }

            if (asset.CustodyState == AssetCustodyState.ReturnPending)
            {
                asset.ConfirmReturned();
            }
        }

        if (checkout is { State: CheckoutState.Active or CheckoutState.Overdue })
        {
            checkout.ConfirmReturned(report.At);

            _audit.Record(new AuditEvent(
                    AuditEventType.ReturnCompleted,
                    report.At,
                    checkout.CorrelationId,
                    $"{asset.Reference} returned to {cabinet.Name} {report.Position}.")
                .About(checkout.UserId)
                .About(asset.Id)
                .About(cabinet.Id));
        }

        return ReportOutcome.Applied;
    }

    private ReportOutcome PositionFaulted(Cabinet cabinet, Asset asset, PositionReport report)
    {
        if (asset.CustodyState != AssetCustodyState.Faulted)
        {
            asset.MarkFaulted();
        }

        _audit.Record(new AuditEvent(
                AuditEventType.SlotFaulted,
                report.At,
                CorrelationId.New(),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1} reported a fault. Custody of {2} is not confirmed.",
                    cabinet.Name,
                    report.Position,
                    asset.Reference))
            .About(asset.Id)
            .About(cabinet.Id));

        return ReportOutcome.Applied;
    }

    private void Reconciled(Asset asset, Cabinet cabinet, PositionReport report, string settledAs) =>
        _audit.Record(new AuditEvent(
                AuditEventType.CustodyReconciled,
                report.At,
                CorrelationId.New(),
                $"{asset.Reference} reconciled from {cabinet.Name} {report.Position}: {settledAs}.")
            .About(asset.Id)
            .About(cabinet.Id));
}
