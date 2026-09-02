using KeyManagement.Domain;
using KeyManagement.Domain.Alarms;

namespace KeyManagement.Application.Abstractions;

/// <summary>
/// Raises and finds alarms.
/// </summary>
public interface IAlarmRepository
{
    /// <summary>Records a new alarm.</summary>
    /// <param name="alarm">The alarm.</param>
    void Add(Alarm alarm);

    /// <summary>Finds an alarm.</summary>
    /// <param name="id">The alarm.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The alarm, or <see langword="null"/>.</returns>
    Task<Alarm?> FindByIdAsync(AlarmId id, CancellationToken cancellationToken = default);

    /// <summary>Whether the same problem is already raised and unacknowledged.</summary>
    /// <param name="scope">Identifies the problem.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when an active alarm already covers it.</returns>
    Task<bool> IsActiveAsync(string scope, CancellationToken cancellationToken = default);

    /// <summary>Finds the active alarm covering a problem, if there is one.</summary>
    /// <param name="scope">Identifies the problem.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The alarm, or <see langword="null"/>.</returns>
    Task<Alarm?> FindActiveAsync(string scope, CancellationToken cancellationToken = default);
}
