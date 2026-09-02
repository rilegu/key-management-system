using System.Globalization;

namespace KeyManagement.DeviceSimulator;

/// <summary>
/// The command loop.
/// </summary>
/// <remarks>
/// Reads from standard input, so a person can drive a demonstration by typing and a test can
/// pipe the same script in. That is why fault injection is a command rather than a setting:
/// a scenario reads as a sequence of things that happened.
/// </remarks>
public sealed class SimulatorConsole
{
    private readonly IReadOnlyList<CabinetDevice> _cabinets;

    /// <summary>Creates the console.</summary>
    /// <param name="cabinets">The cabinets it drives.</param>
    public SimulatorConsole(IReadOnlyList<CabinetDevice> cabinets) => _cabinets = cabinets;

    /// <summary>Prints what can be typed.</summary>
    public static void PrintHelp()
    {
        Console.WriteLine("""
            Commands, one per line. A position may be prefixed with a cabinet name.

              take <position>              an item is removed
              put <position>               an item is put back
              fault <position>             the position reports a fault
              pin <user> <pin> <position>  someone asks at the keypad
              drop                         the link goes away
              attach                       the link comes back
              delay <milliseconds>         slow every frame down
              drops <percent>              lose that share of events
              duplicate <on|off>           send every event twice
              status                       what each cabinet believes
              help                         this
              quit                         stop
            """);
    }

    /// <summary>Runs until end of input or a quit command.</summary>
    /// <param name="stopping">Signals the rest of the process to stop.</param>
    /// <param name="cancellationToken">Cancels the loop.</param>
    /// <returns>A task that completes when the loop ends.</returns>
    public async Task RunAsync(CancellationTokenSource stopping, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stopping);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            // End of input. A piped scenario has finished, so stop rather than spinning.
            if (line is null)
            {
                break;
            }

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (words.Length == 0)
            {
                continue;
            }

            if (string.Equals(words[0], "quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            try
            {
                await ExecuteAsync(words, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A mistyped command must not take the simulator down with it.
                Console.WriteLine($"! {exception.Message}");
            }
        }

        await stopping.CancelAsync().ConfigureAwait(false);
    }

    private async Task ExecuteAsync(string[] words, CancellationToken cancellationToken)
    {
        var command = words[0].ToLowerInvariant();

        switch (command)
        {
            case "take" when words.Length >= 2:
                await ReportAsync(words[1], "Empty", cancellationToken).ConfigureAwait(false);
                break;

            case "put" when words.Length >= 2:
                await ReportAsync(words[1], "Occupied", cancellationToken).ConfigureAwait(false);
                break;

            case "fault" when words.Length >= 2:
                await ReportAsync(words[1], "Faulted", cancellationToken).ConfigureAwait(false);
                break;

            case "pin" when words.Length >= 4:
                foreach (var cabinet in Targets(words[3]))
                {
                    await cabinet.PresentAsync(words[1], words[2], Position(words[3]), cancellationToken)
                        .ConfigureAwait(false);
                }

                break;

            case "drop":
                foreach (var cabinet in _cabinets)
                {
                    cabinet.Drop();
                    Console.WriteLine($"[{cabinet.Name}] link dropped.");
                }

                break;

            case "attach":
                foreach (var cabinet in _cabinets)
                {
                    cabinet.Attach();
                }

                break;

            case "delay" when words.Length >= 2:
                Apply(f => f.Latency = TimeSpan.FromMilliseconds(int.Parse(words[1], CultureInfo.InvariantCulture)));
                break;

            case "drops" when words.Length >= 2:
                Apply(f => f.DropPercent = Math.Clamp(int.Parse(words[1], CultureInfo.InvariantCulture), 0, 100));
                break;

            case "duplicate" when words.Length >= 2:
                Apply(f => f.Duplicate = string.Equals(words[1], "on", StringComparison.OrdinalIgnoreCase));
                break;

            case "status":
                PrintStatus();
                break;

            case "help":
                PrintHelp();
                break;

            default:
                Console.WriteLine($"? {string.Join(' ', words)}");
                break;
        }
    }

    private async Task ReportAsync(string target, string state, CancellationToken cancellationToken)
    {
        foreach (var cabinet in Targets(target))
        {
            await cabinet.ReportAsync(Position(target), state, cancellationToken).ConfigureAwait(false);
        }
    }

    // "A01" means every cabinet that has an A01; "Reception:A01" means only that one. With a
    // single cabinet, which is the usual case, the prefix is noise.
    private IEnumerable<CabinetDevice> Targets(string target)
    {
        var separator = target.IndexOf(':', StringComparison.Ordinal);

        if (separator < 0)
        {
            return _cabinets;
        }

        var name = target[..separator];
        return _cabinets.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string Position(string target)
    {
        var separator = target.IndexOf(':', StringComparison.Ordinal);
        return separator < 0 ? target : target[(separator + 1)..];
    }

    private void Apply(Action<FaultInjection> change)
    {
        foreach (var cabinet in _cabinets)
        {
            change(cabinet.Faults);
            Console.WriteLine($"[{cabinet.Name}] faults: {cabinet.Faults}");
        }
    }

    private void PrintStatus()
    {
        foreach (var cabinet in _cabinets)
        {
            var link = cabinet.IsHeldOffline
                ? "held offline"
                : cabinet.IsAttached ? "attached" : "reconnecting";

            Console.WriteLine(
                $"[{cabinet.Name}] {link}, {cabinet.BufferedEvents} buffered, faults: {cabinet.Faults}");

            foreach (var (position, state) in cabinet.Positions())
            {
                Console.WriteLine($"    {position}  {state}");
            }
        }
    }
}
