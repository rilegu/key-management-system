using KeyManagement.Application.Abstractions;
using KeyManagement.Application.Alarms;
using KeyManagement.Domain;
using KeyManagement.Domain.Alarms;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Auditing;
using KeyManagement.Domain.Custody;

namespace KeyManagement.Application.Custody;

/// <summary>
/// What the sweep found.
/// </summary>
/// <param name="MarkedOverdue">Checkouts that passed their curfew.</param>
/// <param name="Abandoned">Releases nobody collected.</param>
public sealed record SweepOutcome(int MarkedOverdue, int Abandoned);

/// <summary>
/// Notices the things that happen by the passage of time rather than by anyone acting.
/// </summary>
/// <remarks>
/// <para>
/// Two problems, both of which are the absence of an event rather than an event. An item held
/// past its curfew, and a position released that nobody ever emptied. Nothing reports either,
/// so something has to go looking.
/// </para>
/// <para>
/// Free of any scheduler. It is one pass, callable from a test with a controlled clock; the
/// host decides how often to run it.
/// </para>
/// </remarks>
public sealed class CustodySweep
{
    /// <summary>
    /// How long a released position waits before the release is treated as uncollected.
    /// </summary>
    /// <remarks>
    /// Comfortably longer than the cabinet's own unlock window, so the cabinet has relocked and
    /// the item demonstrably stayed put. Abandoning earlier would race the person reaching for
    /// it.
    /// </remarks>
    public static readonly TimeSpan UncollectedAfter = TimeSpan.FromMinutes(2);

    private readonly ICheckoutRepository _checkouts;
    private readonly IAssetRepository _assets;
    private readonly IAlarmRepository _alarms;
    private readonly AlarmService _alarmService;
    private readonly IAuditTrail _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    /// <summary>Creates the sweep.</summary>
    /// <param name="checkouts">Custody requests.</param>
    /// <param name="assets">Items.</param>
    /// <param name="alarms">Alarms.</param>
    /// <param name="alarmService">Raises them once each.</param>
    /// <param name="audit">The trail.</param>
    /// <param name="unitOfWork">Commits the work.</param>
    /// <param name="clock">The current time.</param>
    public CustodySweep(
        ICheckoutRepository checkouts,
        IAssetRepository assets,
        IAlarmRepository alarms,
        AlarmService alarmService,
        IAuditTrail audit,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _checkouts = checkouts;
        _assets = assets;
        _alarms = alarms;
        _alarmService = alarmService;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    /// <summary>Runs one pass.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What it found.</returns>
    public async Task<SweepOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var overdue = 0;
        var abandoned = 0;

        foreach (var checkout in await _checkouts.ListOpenAsync(cancellationToken).ConfigureAwait(false))
        {
            if (checkout.State == CheckoutState.Active && checkout.IsOverdueAt(now))
            {
                await MarkOverdueAsync(checkout, now, cancellationToken).ConfigureAwait(false);
                overdue++;
            }
            else if (checkout.State == CheckoutState.Pending
                     && now - checkout.RequestedAt > UncollectedAfter)
            {
                await AbandonAsync(checkout, now, cancellationToken).ConfigureAwait(false);
                abandoned++;
            }
        }

        if (overdue > 0 || abandoned > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new SweepOutcome(overdue, abandoned);
    }

    private async Task MarkOverdueAsync(
        Checkout checkout,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        checkout.MarkOverdue();

        var asset = await _assets.FindByIdAsync(checkout.AssetId, cancellationToken).ConfigureAwait(false);
        var reference = asset?.Reference ?? checkout.AssetId.ToString();
        var due = checkout.DueAt ?? now;

        _audit.Record(new AuditEvent(
                AuditEventType.CustodyReconciled,
                now,
                checkout.CorrelationId,
                $"{reference} is overdue; it was due back at {due:u}.")
            .About(checkout.UserId)
            .About(checkout.AssetId));

        var alarm = Alarm.Raise(
                AlarmType.OverdueItem,
                AlarmSeverity.Warning,
                Alarm.OverdueScope(checkout.Id),
                $"{reference} is overdue. It was due back at {due:u}.",
                now,
                checkout.CorrelationId)
            .About(checkout.UserId)
            .About(checkout.AssetId);

        await _alarmService.RaiseAsync(alarm, cancellationToken).ConfigureAwait(false);
    }

    private async Task AbandonAsync(
        Checkout checkout,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        checkout.Abandon();

        var asset = await _assets.FindByIdAsync(checkout.AssetId, cancellationToken).ConfigureAwait(false);
        var reference = asset?.Reference ?? checkout.AssetId.ToString();

        // The item never left, so it goes back to being available. Leaving it released would
        // block it forever on the strength of a request nobody followed through.
        if (asset is { CustodyState: AssetCustodyState.CheckoutPending })
        {
            asset.AbandonCheckout();
        }

        _audit.Record(new AuditEvent(
                AuditEventType.CustodyReconciled,
                now,
                checkout.CorrelationId,
                $"{reference} was released and never collected; the request is closed.")
            .About(checkout.UserId)
            .About(checkout.AssetId));

        var alarm = Alarm.Raise(
                AlarmType.UncollectedRelease,
                AlarmSeverity.Information,
                Alarm.UncollectedScope(checkout.Id),
                $"{reference} was released and never collected.",
                now,
                checkout.CorrelationId)
            .About(checkout.UserId)
            .About(checkout.AssetId);

        await _alarmService.RaiseAsync(alarm, cancellationToken).ConfigureAwait(false);
    }
}
