using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using KeyManagement.Desktop.Services;

namespace KeyManagement.Desktop.ViewModels;

/// <summary>
/// Shared behaviour for every screen: a busy flag, an error line, and one way to run async work.
/// </summary>
/// <remarks>
/// Derives from <see cref="ObservableValidator"/> rather than <c>ObservableObject</c> so any
/// screen can annotate its inputs and surface the errors through data binding. Screens with
/// nothing to validate pay nothing for it.
/// </remarks>
public abstract partial class ViewModelBase : ObservableValidator
{
    private int _depth;

    /// <summary>Whether work is in flight. Bound to disable commands and show progress.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>The last thing that went wrong, or <see langword="null"/>.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>
    /// Runs work with the busy flag set and transport failures turned into a readable line.
    /// </summary>
    /// <param name="work">What to do.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the work does.</returns>
    /// <remarks>
    /// Every screen loads the same way, so the failure handling lives here rather than being
    /// repeated, half-completely, in each command.
    /// </remarks>
    protected async Task RunAsync(
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        // Nesting is counted rather than refused. A command that acts and then reloads calls
        // this twice, and rejecting the inner call would make the reload silently do nothing —
        // which looked exactly like a screen that had not refreshed.
        _depth++;
        IsBusy = true;

        if (_depth == 1)
        {
            ErrorMessage = null;
        }

        try
        {
            await work(cancellationToken).ConfigureAwait(true);
        }
        catch (KeyManagementClientException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (OperationCanceledException)
        {
            // The screen moved on or the window closed. Nothing to report.
        }
        finally
        {
            _depth--;
            IsBusy = _depth > 0;
        }
    }
}
