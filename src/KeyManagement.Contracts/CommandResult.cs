namespace KeyManagement.Contracts;

/// <summary>
/// What every command returns, whether it succeeded or was refused.
/// </summary>
/// <remarks>
/// A refused custody request is not an error. It is an outcome the system exists to produce
/// and record, so it comes back with a normal status and <see cref="Success"/> false rather
/// than as a failure the client has to interpret from a status code.
/// </remarks>
/// <param name="Success">Whether the command did what was asked.</param>
/// <param name="Message">One line, written for the person at the workstation.</param>
/// <param name="CorrelationId">Ties this command to its audit records and any device command it caused.</param>
/// <param name="State">The resulting state, so the client does not have to re-query to find out.</param>
public sealed record CommandResult(bool Success, string Message, Guid CorrelationId, string State);

/// <summary>
/// A command result that also carries what the command produced.
/// </summary>
/// <typeparam name="T">The payload.</typeparam>
/// <param name="Success">Whether the command did what was asked.</param>
/// <param name="Message">One line, written for the person at the workstation.</param>
/// <param name="CorrelationId">Ties this command to its audit records and any device command it caused.</param>
/// <param name="State">The resulting state.</param>
/// <param name="Data">What the command produced, or <see langword="null"/> when it was refused.</param>
public sealed record CommandResult<T>(
    bool Success,
    string Message,
    Guid CorrelationId,
    string State,
    T? Data);
