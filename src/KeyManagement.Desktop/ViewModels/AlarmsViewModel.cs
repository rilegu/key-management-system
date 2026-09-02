using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KeyManagement.Contracts;
using KeyManagement.Desktop.Services;

namespace KeyManagement.Desktop.ViewModels;

/// <summary>
/// One alarm in the list.
/// </summary>
public sealed class AlarmRowViewModel
{
    /// <summary>Creates a row.</summary>
    /// <param name="alarm">The alarm as the server reported it.</param>
    public AlarmRowViewModel(AlarmSummary alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        Id = alarm.Id;
        What = Vocabulary.AlarmWord(alarm.Type);
        Detail = alarm.Summary;
        Reference = alarm.AssetReference ?? "—";
        RaisedAt = alarm.RaisedAt;
        IsActive = alarm.Status == "Active";
        AcknowledgedBy = alarm.AcknowledgedBy ?? "—";
        SeverityClass = alarm.Severity switch
        {
            "Critical" => "fault",
            "Warning" => "out",
            _ => "released",
        };
        SeverityWord = alarm.Severity;
    }

    /// <summary>Identifies the alarm.</summary>
    public Guid Id { get; }

    /// <summary>What it is about, in the words used on screen.</summary>
    public string What { get; }

    /// <summary>The line the server wrote.</summary>
    public string Detail { get; }

    /// <summary>The item involved, when there was one.</summary>
    public string Reference { get; }

    /// <summary>When it was raised.</summary>
    public DateTimeOffset RaisedAt { get; }

    /// <summary>Whether it still needs dealing with.</summary>
    public bool IsActive { get; }

    /// <summary>Who acknowledged it.</summary>
    public string AcknowledgedBy { get; }

    /// <summary>How much it matters.</summary>
    public string SeverityWord { get; }

    /// <summary>When it was raised, local.</summary>
    public string When =>
        RaisedAt.ToLocalTime().ToString("dd MMM  HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>Critical, and shown as a fault.</summary>
    public bool IsCritical => SeverityClass == "fault";

    /// <summary>A warning.</summary>
    public bool IsWarning => SeverityClass == "out";

    /// <summary>Information only.</summary>
    public bool IsInformation => SeverityClass == "released";

    private string SeverityClass { get; }
}

/// <summary>
/// Alarms, and acknowledging them.
/// </summary>
public sealed partial class AlarmsViewModel : ViewModelBase
{
    private readonly IKeyManagementClient _client;
    private readonly INotificationService _notifications;

    /// <summary>Creates the screen.</summary>
    /// <param name="client">The server.</param>
    /// <param name="notifications">Tells the operator what happened.</param>
    public AlarmsViewModel(IKeyManagementClient client, INotificationService notifications)
    {
        _client = client;
        _notifications = notifications;
    }

    /// <summary>The alarms currently shown.</summary>
    public ObservableCollection<AlarmRowViewModel> Alarms { get; } = [];

    /// <summary>Whether to leave out ones already acknowledged.</summary>
    [ObservableProperty]
    public partial bool ActiveOnly { get; set; } = true;

    /// <summary>How many are shown.</summary>
    [ObservableProperty]
    public partial string ResultSummary { get; set; } = string.Empty;

    /// <summary>Loads alarms.</summary>
    /// <returns>A task that completes when the list is ready.</returns>
    [RelayCommand]
    public Task LoadAsync() => RunAsync(async token =>
    {
        var alarms = await _client.ListAlarmsAsync(ActiveOnly, token).ConfigureAwait(true);

        Alarms.Clear();
        foreach (var alarm in alarms)
        {
            Alarms.Add(new AlarmRowViewModel(alarm));
        }

        ResultSummary = Alarms.Count == 0
            ? ActiveOnly ? "Nothing outstanding." : "No alarms."
            : $"{Alarms.Count} alarm{(Alarms.Count == 1 ? string.Empty : "s")}";
    });

    /// <summary>Records that this operator has seen an alarm.</summary>
    /// <param name="row">The alarm.</param>
    /// <returns>A task that completes when it is acknowledged.</returns>
    [RelayCommand]
    public Task AcknowledgeAsync(AlarmRowViewModel? row) => RunAsync(async token =>
    {
        if (row is null)
        {
            return;
        }

        var result = await _client.AcknowledgeAlarmAsync(row.Id, token).ConfigureAwait(true);

        if (result.Success)
        {
            _notifications.Success(result.Message);
        }
        else
        {
            _notifications.Problem(result.Message);
        }

        await LoadAsync().ConfigureAwait(true);
    });

    partial void OnActiveOnlyChanged(bool value) => _ = LoadAsync();
}
