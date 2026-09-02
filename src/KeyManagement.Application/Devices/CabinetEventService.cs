using System.Globalization;
using KeyManagement.Application.Abstractions;
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
    private readonly IAssetRepository _assets;
    private readonly ICheckoutRepository _checkouts;
    private readonly IDeviceEventLog _deviceEvents;
    private readonly IAuditTrail _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IClock _clock;

    /// <summary>Creates the service.</summary>
    /// <param name="cabinets">Cabinets and positions.</param>
    /// <param name="assets">Items.</param>
    /// <param name="checkouts">Custody requests.</param>
    /// <param name="deviceEvents">What the cabinet said, as it said it.</param>
    /// <param name="audit">The trail.</param>
    /// <param name="unitOfWork">Commits the work.</param>
    /// <param name="passwordHasher">Verifies the cabinet's credential.</param>
    /// <param name="clock">The current time.</param>
    public CabinetEventService(
        ICabinetRepository cabinets,
        IAssetRepository assets,
        ICheckoutRepository checkouts,
        IDeviceEventLog deviceEvents,
        IAuditTrail audit,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IClock clock)
    {
        _cabinets = cabinets;
        _assets = assets;
        _checkouts = checkouts;
        _deviceEvents = deviceEvents;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _clock = clock;
    }

    /// <summary>Decides whether a cabinet may attach, and what it should replay.</summary>
    /// <param name="cabinetName">The name it presents.</param>
    /// <param name="credential">The secret it presents.</param>
    /// <param name="firmwareVersion">What it says it is running.</param>
    /// <param name="protocolVersion">The wire version it speaks.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Acceptance and the sequence to resume from, or a refusal.</returns>
    /// <remarks>
    /// An unknown cabinet and a wrong credential are refused identically, for the same reason a
    /// wrong password and an unknown account are: the answer should not tell a guesser which
    /// half they got right.
    /// </remarks>
    public async Task<CabinetAttachment> AttachAsync(
        string cabinetName,
        string credential,
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

        if (cabinet?.CredentialHash is null
            || string.IsNullOrEmpty(credential)
            || _passwordHasher.Verify(cabinet.CredentialHash, credential) == PasswordVerification.Failed)
        {
            return CabinetAttachment.Refused("The cabinet name or credential is not correct.");
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

    /// <summary>The wire version this build accepts.</summary>
    public static int ProtocolVersion => 1;

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
