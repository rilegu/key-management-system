using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KeyManagement.Contracts;
using KeyManagement.Desktop.Services;

namespace KeyManagement.Desktop.ViewModels;

/// <summary>
/// The position board, the detail panels beside it, and the live activity feed under it.
/// </summary>
/// <remarks>
/// The screen this software is recognised by. Everything an operator needs at a glance is here:
/// which cabinet, whether it is reachable, what is in each position, who holds what is out, and
/// what has just happened.
/// </remarks>
public sealed partial class SystemViewerViewModel : ViewModelBase
{
    private const int ActivityFeedLength = 40;

    private readonly IKeyManagementClient _client;
    private readonly INotificationService _notifications;
    private readonly ILiveActivityFeed _live;
    private IReadOnlyList<AssetSummary> _items = [];
    private IReadOnlyList<CheckoutSummary> _openCheckouts = [];

    /// <summary>Creates the screen.</summary>
    /// <param name="client">The server.</param>
    /// <param name="notifications">Tells the operator what happened.</param>
    /// <param name="live">The live activity feed.</param>
    public SystemViewerViewModel(
        IKeyManagementClient client,
        INotificationService notifications,
        ILiveActivityFeed live)
    {
        _client = client;
        _notifications = notifications;
        _live = live;

        _live.ActivityReceived += OnActivityReceived;
    }

    /// <summary>Cabinets to choose between.</summary>
    public ObservableCollection<CabinetSummary> Cabinets { get; } = [];

    /// <summary>The selected cabinet's positions, in position order.</summary>
    public ObservableCollection<PositionViewModel> Positions { get; } = [];

    /// <summary>The most recent activity, newest first.</summary>
    public ObservableCollection<ActivityRowViewModel> Activity { get; } = [];

    /// <summary>The cabinet being viewed.</summary>
    [ObservableProperty]
    public partial CabinetSummary? SelectedCabinet { get; set; }

    /// <summary>The position the operator has selected, if any.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestItemCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReturnItemCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial PositionViewModel? SelectedPosition { get; set; }

    /// <summary>The item in the selected position, if there is one.</summary>
    [ObservableProperty]
    public partial AssetSummary? SelectedItem { get; set; }

    /// <summary>The open checkout for the selected item, if it is out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeldBy))]
    [NotifyPropertyChangedFor(nameof(TakenAt))]
    [NotifyPropertyChangedFor(nameof(Curfew))]
    public partial CheckoutSummary? SelectedCheckout { get; set; }

    /// <summary>Whether a position is selected.</summary>
    public bool HasSelection => SelectedPosition is not null;

    /// <summary>Who currently holds the selected item.</summary>
    public string HeldBy => SelectedCheckout?.UserDisplayName ?? "—";

    /// <summary>When the selected item was taken.</summary>
    public string TakenAt => Format(SelectedCheckout?.TakenAt ?? SelectedCheckout?.RequestedAt);

    /// <summary>When the selected item is due back.</summary>
    public string Curfew => SelectedCheckout?.DueAt is { } due ? Format(due) : "None set";

    /// <summary>Whether the live feed is connected.</summary>
    [ObservableProperty]
    public partial bool IsLive { get; set; }

    /// <summary>How many items are currently out across the whole system.</summary>
    [ObservableProperty]
    public partial int OutCount { get; set; }

    /// <summary>How many items the server cannot account for.</summary>
    [ObservableProperty]
    public partial int UnconfirmedCount { get; set; }

    /// <summary>How many positions are occupied in the selected cabinet.</summary>
    [ObservableProperty]
    public partial string Occupancy { get; set; } = "—";

    /// <summary>Loads cabinets, the board and the activity feed.</summary>
    /// <returns>A task that completes when the screen is ready.</returns>
    [RelayCommand]
    public Task LoadAsync() => RunAsync(async token =>
    {
        var cabinets = await _client.ListCabinetsAsync(token).ConfigureAwait(true);
        var previous = SelectedCabinet?.Id;

        Cabinets.Clear();
        foreach (var cabinet in cabinets)
        {
            Cabinets.Add(cabinet);
        }

        SelectedCabinet = Cabinets.FirstOrDefault(c => c.Id == previous) ?? Cabinets.FirstOrDefault();

        await RefreshBoardAsync(token).ConfigureAwait(true);
        await RefreshActivityAsync(token).ConfigureAwait(true);

        IsLive = _live.IsConnected;
    });

    /// <summary>Requests the item in the selected position.</summary>
    /// <returns>A task that completes when the request is answered.</returns>
    [RelayCommand(CanExecute = nameof(CanRequestItem))]
    public Task RequestItemAsync() => RunAsync(async token =>
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var result = await _client
            .RequestItemAsync(item.Id, curfew: null, token)
            .ConfigureAwait(true);

        // A refusal is a normal answer with a reason worth reading, not a failure to report as
        // an error. It is shown as the server wrote it.
        if (result.Success)
        {
            _notifications.Success(result.Message);
        }
        else
        {
            _notifications.Problem(result.Message);
        }

        await RefreshBoardAsync(token).ConfigureAwait(true);
    });

    /// <summary>Starts returning the item in the selected position.</summary>
    /// <returns>A task that completes when the return is started.</returns>
    [RelayCommand(CanExecute = nameof(CanReturnItem))]
    public Task ReturnItemAsync() => RunAsync(async token =>
    {
        if (SelectedCheckout is not { } checkout)
        {
            return;
        }

        var result = await _client.ReturnItemAsync(checkout.Id, token).ConfigureAwait(true);

        if (result.Success)
        {
            _notifications.Success(result.Message);
        }
        else
        {
            _notifications.Problem(result.Message);
        }

        await RefreshBoardAsync(token).ConfigureAwait(true);
    });

    /// <summary>Selects a position and updates the detail panel.</summary>
    /// <param name="position">The position clicked.</param>
    [RelayCommand]
    public void SelectPosition(PositionViewModel? position)
    {
        foreach (var tile in Positions)
        {
            tile.IsSelected = ReferenceEquals(tile, position);
        }

        SelectedPosition = position;
        SelectedItem = position?.ItemId is { } id
            ? _items.FirstOrDefault(i => i.Id == id)
            : null;
        SelectedCheckout = SelectedItem is null
            ? null
            : _openCheckouts.FirstOrDefault(c => c.AssetId == SelectedItem.Id);
    }

    /// <summary>Connects the live feed for the signed-in holder.</summary>
    /// <returns>A task that completes once connected or given up.</returns>
    public async Task StartLiveFeedAsync()
    {
        if (_client.Session is not { } session)
        {
            return;
        }

        try
        {
            await _live.ConnectAsync(session.AccessToken).ConfigureAwait(true);
            IsLive = _live.IsConnected;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The board still works without a live feed; it just needs reloading by hand. A
            // failure here must not stop the screen from opening.
            IsLive = false;
        }
    }

    private bool CanRequestItem =>
        SelectedItem is not null && SelectedPosition is { IsIn: true };

    private bool CanReturnItem =>
        SelectedCheckout is not null && SelectedPosition is { IsOut: true };

    private static string Format(DateTimeOffset? moment) =>
        moment is { } value
            ? value.ToLocalTime().ToString("dd MMM HH:mm", System.Globalization.CultureInfo.CurrentCulture)
            : "—";

    private async Task RefreshBoardAsync(System.Threading.CancellationToken token)
    {
        _items = await _client.ListItemsAsync(token).ConfigureAwait(true);
        _openCheckouts = await _client.ListOpenCheckoutsAsync(token).ConfigureAwait(true);

        OutCount = _items.Count(i => i.CustodyState is "CheckedOut" or "CheckoutPending");
        UnconfirmedCount = _items.Count(i => i.CustodyState is "Unknown" or "Faulted");

        var selected = SelectedPosition?.Position;
        Positions.Clear();

        if (SelectedCabinet is not { } cabinet)
        {
            Occupancy = "—";
            SelectPosition(null);
            return;
        }

        var snapshot = await _client.GetCabinetSnapshotAsync(cabinet.Id, token).ConfigureAwait(true);

        if (snapshot is null)
        {
            Occupancy = "—";
            SelectPosition(null);
            return;
        }

        foreach (var slot in snapshot.Slots)
        {
            var item = slot.AssetId is { } assetId
                ? _items.FirstOrDefault(i => i.Id == assetId)
                : null;

            Positions.Add(new PositionViewModel(slot, item));
        }

        var occupied = Positions.Count(p => !p.IsEmpty);
        Occupancy = $"{occupied} of {Positions.Count} positions assigned";

        SelectPosition(Positions.FirstOrDefault(p => p.Position == selected));
    }

    private async Task RefreshActivityAsync(System.Threading.CancellationToken token)
    {
        var recent = await _client
            .SearchActivityAsync(new AuditQuery(Take: ActivityFeedLength), token)
            .ConfigureAwait(true);

        Activity.Clear();
        foreach (var record in recent)
        {
            Activity.Add(new ActivityRowViewModel(record));
        }
    }

    private void OnActivityReceived(AuditEventSummary activity)
    {
        // Already on the UI thread: the stream posts there before raising this.
        Activity.Insert(0, new ActivityRowViewModel(activity));

        while (Activity.Count > ActivityFeedLength)
        {
            Activity.RemoveAt(Activity.Count - 1);
        }

        IsLive = _live.IsConnected;
    }

    partial void OnSelectedCabinetChanged(CabinetSummary? value)
    {
        if (value is not null && !IsBusy)
        {
            _ = RunAsync(RefreshBoardAsync);
        }
    }
}
