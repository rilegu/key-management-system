using KeyManagement.Application.Custody;

namespace KeyManagement.Server;

/// <summary>
/// How often the sweep runs.
/// </summary>
public sealed class CustodySweepOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "CustodySweep";

    /// <summary>Whether to run at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long between passes.</summary>
    /// <remarks>
    /// Frequent enough that an overdue item surfaces while it still matters, and cheap enough
    /// that it does not: the sweep walks only what is currently out, not the whole history.
    /// </remarks>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Runs the custody sweep on a timer.
/// </summary>
/// <remarks>
/// The scheduling, and nothing else. What a pass actually does lives in
/// <see cref="CustodySweep"/>, where a test can run one against a clock it controls rather than
/// waiting for a timer.
/// </remarks>
public sealed class CustodySweepService : BackgroundService
{
    private readonly CustodySweepOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CustodySweepService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="options">How often to run.</param>
    /// <param name="scopes">Creates a scope per pass.</param>
    /// <param name="logger">Records what a pass found.</param>
    public CustodySweepService(
        CustodySweepOptions options,
        IServiceScopeFactory scopes,
        ILogger<CustodySweepService> logger)
    {
        _options = options;
        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var sweep = scope.ServiceProvider.GetRequiredService<CustodySweep>();
                var outcome = await sweep.RunAsync(stoppingToken).ConfigureAwait(false);

                if (outcome.MarkedOverdue > 0 || outcome.Abandoned > 0)
                {
                    Swept(_logger, outcome.MarkedOverdue, outcome.Abandoned, null);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed pass must not stop the timer. Whatever it missed is still there to
                // be found next time, because the sweep looks at state rather than at events.
                SweepFailed(_logger, exception);
            }
        }
    }

    private static readonly Action<ILogger, int, int, Exception?> Swept =
        LoggerMessage.Define<int, int>(
            LogLevel.Information,
            new EventId(1, nameof(Swept)),
            "Custody sweep marked {Overdue} overdue and closed {Abandoned} uncollected release(s).");

    private static readonly Action<ILogger, Exception?> SweepFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(SweepFailed)),
            "A custody sweep failed; the next pass will try again.");
}
