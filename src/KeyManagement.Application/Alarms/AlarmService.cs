using KeyManagement.Application.Abstractions;
using KeyManagement.Contracts;
using KeyManagement.Domain;
using KeyManagement.Domain.Access;
using KeyManagement.Domain.Alarms;
using KeyManagement.Domain.Auditing;

namespace KeyManagement.Application.Alarms;

/// <summary>
/// Raises alarms once, and records who dealt with them.
/// </summary>
/// <remarks>
/// Raising goes through here rather than being done wherever a problem is noticed, so the
/// once-per-problem rule and the matching audit record live in one place instead of being
/// remembered at every call site.
/// </remarks>
public sealed class AlarmService
{
    private readonly IAlarmRepository _alarms;
    private readonly IUserRepository _users;
    private readonly IAuditTrail _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    /// <summary>Creates the service.</summary>
    /// <param name="alarms">Alarms.</param>
    /// <param name="users">Holders, for checking who may acknowledge.</param>
    /// <param name="audit">The trail.</param>
    /// <param name="unitOfWork">Commits the work.</param>
    /// <param name="clock">The current time.</param>
    public AlarmService(
        IAlarmRepository alarms,
        IUserRepository users,
        IAuditTrail audit,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _alarms = alarms;
        _users = users;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    /// <summary>
    /// Raises an alarm unless the same problem is already raised.
    /// </summary>
    /// <param name="alarm">The alarm to raise.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The alarm, or <see langword="null"/> if one was already active for it.</returns>
    /// <remarks>
    /// Not saved here. The caller decides what else belongs in the same transaction, which for
    /// a custody change is the change itself.
    /// </remarks>
    public async Task<Alarm?> RaiseAsync(Alarm alarm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        if (await _alarms.IsActiveAsync(alarm.Scope, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        _alarms.Add(alarm);
        return alarm;
    }

    /// <summary>Records that someone has seen an alarm.</summary>
    /// <param name="alarmId">The alarm.</param>
    /// <param name="acknowledgedBy">Who is acknowledging it.</param>
    /// <param name="correlationId">Ties this to its audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome, refused if the holder may not acknowledge.</returns>
    public async Task<CommandResult> AcknowledgeAsync(
        AlarmId alarmId,
        UserId acknowledgedBy,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByIdAsync(acknowledgedBy, cancellationToken).ConfigureAwait(false);

        // Checked here as well as by the endpoint policy. A future caller that is not this API
        // is judged by the same rule.
        if (user is null || !user.Can(Permissions.AcknowledgeAlarm))
        {
            return new CommandResult(
                false, "You do not have permission to acknowledge alarms.", correlationId.Value, "Denied");
        }

        var alarm = await _alarms.FindByIdAsync(alarmId, cancellationToken).ConfigureAwait(false);

        if (alarm is null)
        {
            return new CommandResult(false, "No such alarm.", correlationId.Value, "NotFound");
        }

        if (alarm.Status == AlarmStatus.Acknowledged)
        {
            return new CommandResult(
                true, "That alarm was already acknowledged.", correlationId.Value, alarm.Status.ToString());
        }

        var now = _clock.UtcNow;
        alarm.Acknowledge(acknowledgedBy, now);

        _audit.Record(new AuditEvent(
                AuditEventType.ConfigurationChanged,
                now,
                correlationId,
                $"'{user.UserName}' acknowledged: {alarm.Summary}")
            .About(user.Id));

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CommandResult(true, "Acknowledged.", correlationId.Value, alarm.Status.ToString());
    }
}
