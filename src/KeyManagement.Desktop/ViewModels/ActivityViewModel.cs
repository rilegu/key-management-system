using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KeyManagement.Contracts;
using KeyManagement.Desktop.Services;

namespace KeyManagement.Desktop.ViewModels;

/// <summary>
/// The activity trail, searchable.
/// </summary>
public sealed partial class ActivityViewModel : ViewModelBase
{
    /// <summary>The filter entry meaning no type filter.</summary>
    public const string AllTypes = "Everything";

    private readonly IKeyManagementClient _client;

    /// <summary>Creates the screen.</summary>
    /// <param name="client">The server.</param>
    public ActivityViewModel(IKeyManagementClient client)
    {
        _client = client;

        Types.Add(AllTypes);
        foreach (var type in new[]
        {
            "CheckoutRequested", "CheckoutAuthorized", "CheckoutDenied", "CheckoutCompleted",
            "ReturnRequested", "ReturnCompleted", "SignInSucceeded", "SignInFailed",
            "CabinetOffline", "SlotFaulted", "UnauthorizedSlotChange",
        })
        {
            Types.Add(type);
        }
    }

    /// <summary>The records matching the current filters, newest first.</summary>
    public ObservableCollection<ActivityRowViewModel> Records { get; } = [];

    /// <summary>The types available to filter by.</summary>
    public ObservableCollection<string> Types { get; } = [];

    /// <summary>How far back to look, in hours.</summary>
    public ObservableCollection<int> Windows { get; } = [1, 8, 24, 168, 720];

    /// <summary>The type filter, as the model names it.</summary>
    [ObservableProperty]
    public partial string SelectedType { get; set; } = AllTypes;

    /// <summary>How far back the search reaches, in hours.</summary>
    [ObservableProperty]
    public partial int SelectedWindow { get; set; } = 24;

    /// <summary>How many records came back.</summary>
    [ObservableProperty]
    public partial string ResultSummary { get; set; } = string.Empty;

    /// <summary>Turns a filter value into the words used on screen.</summary>
    /// <param name="type">The model's type name, or the all-types entry.</param>
    /// <returns>The operator-facing phrase.</returns>
    public static string DescribeType(string type) =>
        type == AllTypes ? AllTypes : Vocabulary.ActivityWord(type);

    /// <summary>Turns a window in hours into the words used on screen.</summary>
    /// <param name="hours">The window.</param>
    /// <returns>The operator-facing phrase.</returns>
    public static string DescribeWindow(int hours) => hours switch
    {
        1 => "Last hour",
        8 => "Last 8 hours",
        24 => "Last 24 hours",
        168 => "Last 7 days",
        _ => "Last 30 days",
    };

    /// <summary>Runs the search.</summary>
    /// <returns>A task that completes when the results are in.</returns>
    [RelayCommand]
    public Task SearchAsync() => RunAsync(async token =>
    {
        var query = new AuditQuery(
            From: DateTimeOffset.UtcNow.AddHours(-SelectedWindow),
            Type: SelectedType == AllTypes ? null : SelectedType,
            Take: 250);

        var records = await _client.SearchActivityAsync(query, token).ConfigureAwait(true);

        Records.Clear();
        foreach (var record in records)
        {
            Records.Add(new ActivityRowViewModel(record));
        }

        // The server caps what it returns, so a full page means there may be more behind it.
        ResultSummary = Records.Count == 250
            ? "First 250 records. Narrow the search to see further back."
            : $"{Records.Count} records";
    });

    partial void OnSelectedTypeChanged(string value) => _ = SearchAsync();

    partial void OnSelectedWindowChanged(int value) => _ = SearchAsync();
}
