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
/// Every item and where it is.
/// </summary>
public sealed partial class ItemsViewModel : ViewModelBase
{
    /// <summary>The filter entry meaning no group filter.</summary>
    public const string AllGroups = "All groups";

    private readonly IKeyManagementClient _client;
    private readonly INotificationService _notifications;
    private IReadOnlyList<AssetSummary> _all = [];
    private IReadOnlyList<CheckoutSummary> _open = [];

    /// <summary>Creates the screen.</summary>
    /// <param name="client">The server.</param>
    /// <param name="notifications">Tells the operator what happened.</param>
    public ItemsViewModel(IKeyManagementClient client, INotificationService notifications)
    {
        _client = client;
        _notifications = notifications;
    }

    /// <summary>The items matching the current filters.</summary>
    public ObservableCollection<ItemRowViewModel> Items { get; } = [];

    /// <summary>Groups to filter by, with <see cref="AllGroups"/> first.</summary>
    public ObservableCollection<string> Groups { get; } = [AllGroups];

    /// <summary>The group filter.</summary>
    [ObservableProperty]
    public partial string SelectedGroup { get; set; } = AllGroups;

    /// <summary>Free text matched against reference and description.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Whether to show only what is currently out.</summary>
    [ObservableProperty]
    public partial bool OutOnly { get; set; }

    /// <summary>How many items are shown against how many exist.</summary>
    [ObservableProperty]
    public partial string ResultSummary { get; set; } = string.Empty;

    /// <summary>Loads every item.</summary>
    /// <returns>A task that completes when the list is ready.</returns>
    [RelayCommand]
    public Task LoadAsync() => RunAsync(async token =>
    {
        _all = await _client.ListItemsAsync(token).ConfigureAwait(true);
        _open = await _client.ListOpenCheckoutsAsync(token).ConfigureAwait(true);

        var groups = _all.Select(i => i.AssetGroupName).Distinct().OrderBy(n => n).ToList();
        Groups.Clear();
        Groups.Add(AllGroups);
        foreach (var group in groups)
        {
            Groups.Add(group);
        }

        SelectedGroup = AllGroups;
        Apply();
    });

    /// <summary>Requests an item.</summary>
    /// <param name="row">The item wanted.</param>
    /// <returns>A task that completes when the request is answered.</returns>
    [RelayCommand]
    public Task RequestAsync(ItemRowViewModel? row) => RunAsync(async token =>
    {
        if (row is null)
        {
            return;
        }

        var result = await _client.RequestItemAsync(row.Id, curfew: null, token).ConfigureAwait(true);

        if (result.Success)
        {
            _notifications.Success(result.Message);
        }
        else
        {
            _notifications.Problem(result.Message);
        }

        _all = await _client.ListItemsAsync(token).ConfigureAwait(true);
        _open = await _client.ListOpenCheckoutsAsync(token).ConfigureAwait(true);
        Apply();
    });

    /// <summary>Starts returning an item that is out.</summary>
    /// <param name="row">The item being returned.</param>
    /// <returns>A task that completes when the return is started.</returns>
    [RelayCommand]
    public Task ReturnAsync(ItemRowViewModel? row) => RunAsync(async token =>
    {
        if (row?.CheckoutId is not { } checkoutId)
        {
            return;
        }

        var result = await _client.ReturnItemAsync(checkoutId, token).ConfigureAwait(true);

        if (result.Success)
        {
            _notifications.Success(result.Message);
        }
        else
        {
            _notifications.Problem(result.Message);
        }

        _all = await _client.ListItemsAsync(token).ConfigureAwait(true);
        _open = await _client.ListOpenCheckoutsAsync(token).ConfigureAwait(true);
        Apply();
    });

    private void Apply()
    {
        var matching = _all.AsEnumerable();

        if (SelectedGroup != AllGroups)
        {
            matching = matching.Where(i => i.AssetGroupName == SelectedGroup);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var needle = SearchText.Trim();
            matching = matching.Where(i =>
                i.Reference.Contains(needle, System.StringComparison.OrdinalIgnoreCase)
                || i.Description.Contains(needle, System.StringComparison.OrdinalIgnoreCase));
        }

        if (OutOnly)
        {
            matching = matching.Where(i => i.CustodyState is "CheckedOut" or "CheckoutPending");
        }

        Items.Clear();
        foreach (var item in matching.OrderBy(i => i.Reference))
        {
            Items.Add(new ItemRowViewModel(
                item, _open.FirstOrDefault(c => c.AssetId == item.Id)));
        }

        ResultSummary = Items.Count == _all.Count
            ? $"{_all.Count} items"
            : $"{Items.Count} of {_all.Count} items";
    }

    partial void OnSelectedGroupChanged(string value) => Apply();

    partial void OnSearchTextChanged(string value) => Apply();

    partial void OnOutOnlyChanged(bool value) => Apply();
}
