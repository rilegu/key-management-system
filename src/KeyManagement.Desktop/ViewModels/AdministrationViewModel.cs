using System;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KeyManagement.Contracts;
using KeyManagement.Desktop.Services;

namespace KeyManagement.Desktop.ViewModels;

/// <summary>
/// One holder in the administration list.
/// </summary>
public sealed class HolderRowViewModel
{
    /// <summary>Creates a row.</summary>
    /// <param name="holder">The holder as the server reported them.</param>
    public HolderRowViewModel(HolderSummary holder)
    {
        ArgumentNullException.ThrowIfNull(holder);

        Id = holder.Id;
        UserName = holder.UserName;
        DisplayName = holder.DisplayName;
        Status = holder.Status;
        HasPin = holder.HasPin;
        Roles = holder.Roles.Count == 0 ? "—" : string.Join(", ", holder.Roles);
        Groups = holder.Groups.Count == 0 ? "—" : string.Join(", ", holder.Groups);
        IsActive = holder.Status == "Active";
    }

    /// <summary>Identifies the holder.</summary>
    public Guid Id { get; }

    /// <summary>Their sign-in name.</summary>
    public string UserName { get; }

    /// <summary>Name shown in the interface.</summary>
    public string DisplayName { get; }

    /// <summary>Whether they may use the system.</summary>
    public string Status { get; }

    /// <summary>Whether they can identify themselves at a cabinet.</summary>
    public bool HasPin { get; }

    /// <summary>Roles they hold.</summary>
    public string Roles { get; }

    /// <summary>Groups they may take from.</summary>
    public string Groups { get; }

    /// <summary>Whether they are currently active.</summary>
    public bool IsActive { get; }

    /// <summary>What the keypad column shows.</summary>
    public string Keypad => HasPin ? "PIN set" : "—";
}

/// <summary>
/// Holders, groups and items: the things custody decisions are made against.
/// </summary>
/// <remarks>
/// Every change here is refused or accepted by the server and audited there. What this screen
/// does is collect the fields; it decides nothing.
/// </remarks>
public sealed partial class AdministrationViewModel : ViewModelBase
{
    private readonly IKeyManagementClient _client;
    private readonly INotificationService _notifications;

    /// <summary>Creates the screen.</summary>
    /// <param name="client">The server.</param>
    /// <param name="notifications">Tells the operator what happened.</param>
    public AdministrationViewModel(IKeyManagementClient client, INotificationService notifications)
    {
        _client = client;
        _notifications = notifications;
    }

    /// <summary>Every holder.</summary>
    public ObservableCollection<HolderRowViewModel> Holders { get; } = [];

    /// <summary>Every item group.</summary>
    public ObservableCollection<AssetGroupSummary> Groups { get; } = [];

    /// <summary>Every role.</summary>
    public ObservableCollection<RoleSummary> Roles { get; } = [];

    /// <summary>The holder selected for granting.</summary>
    [ObservableProperty]
    public partial HolderRowViewModel? SelectedHolder { get; set; }

    /// <summary>The group selected for granting, or for a new item.</summary>
    [ObservableProperty]
    public partial AssetGroupSummary? SelectedGroup { get; set; }

    /// <summary>Sign-in name for a new holder.</summary>
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Enter a user name.")]
    public partial string NewUserName { get; set; } = string.Empty;

    /// <summary>Display name for a new holder.</summary>
    [ObservableProperty]
    public partial string NewDisplayName { get; set; } = string.Empty;

    /// <summary>Initial password for a new holder.</summary>
    [ObservableProperty]
    public partial string NewPassword { get; set; } = string.Empty;

    /// <summary>PIN for a new holder, if they should have one.</summary>
    public string NewPin { get; set; } = string.Empty;

    /// <summary>Name for a new group.</summary>
    [ObservableProperty]
    public partial string NewGroupName { get; set; } = string.Empty;

    /// <summary>Reference for a new item.</summary>
    [ObservableProperty]
    public partial string NewItemReference { get; set; } = string.Empty;

    /// <summary>Description for a new item.</summary>
    [ObservableProperty]
    public partial string NewItemDescription { get; set; } = string.Empty;

    /// <summary>Loads holders, roles and groups.</summary>
    /// <returns>A task that completes when the screen is ready.</returns>
    [RelayCommand]
    public Task LoadAsync() => RunAsync(async token =>
    {
        var holders = await _client.ListHoldersAsync(token).ConfigureAwait(true);
        var roles = await _client.ListRolesAsync(token).ConfigureAwait(true);
        var groups = await _client.ListGroupsAsync(token).ConfigureAwait(true);

        var selected = SelectedHolder?.Id;

        Holders.Clear();
        foreach (var holder in holders)
        {
            Holders.Add(new HolderRowViewModel(holder));
        }

        Roles.Clear();
        foreach (var role in roles)
        {
            Roles.Add(role);
        }

        Groups.Clear();
        foreach (var group in groups)
        {
            Groups.Add(group);
        }

        SelectedHolder = Holders.FirstOrDefault(h => h.Id == selected);
        SelectedGroup ??= Groups.FirstOrDefault();
    });

    /// <summary>Creates a holder from the fields above the list.</summary>
    /// <returns>A task that completes when the holder is created.</returns>
    [RelayCommand]
    public Task CreateHolderAsync() => RunAsync(async token =>
    {
        var result = await _client
            .CreateHolderAsync(
                new CreateHolderRequest(
                    NewUserName.Trim(),
                    string.IsNullOrWhiteSpace(NewDisplayName) ? NewUserName.Trim() : NewDisplayName.Trim(),
                    NewPassword,
                    string.IsNullOrWhiteSpace(NewPin) ? null : NewPin),
                token)
            .ConfigureAwait(true);

        Report(result);

        if (result.Success)
        {
            // Cleared on success only. A refused attempt keeps what was typed, because the
            // usual reason is a name already taken and the rest is still wanted.
            NewUserName = string.Empty;
            NewDisplayName = string.Empty;
            NewPassword = string.Empty;
            NewPin = string.Empty;
            await LoadAsync().ConfigureAwait(true);
        }
    });

    /// <summary>Suspends or reinstates the selected holder.</summary>
    /// <param name="row">The holder.</param>
    /// <returns>A task that completes when the change is saved.</returns>
    [RelayCommand]
    public Task ToggleStatusAsync(HolderRowViewModel? row) => RunAsync(async token =>
    {
        if (row is null)
        {
            return;
        }

        var status = row.IsActive ? "Suspended" : "Active";
        Report(await _client
            .AmendHolderAsync(row.Id, new AmendHolderRequest(null, status), token)
            .ConfigureAwait(true));

        await LoadAsync().ConfigureAwait(true);
    });

    /// <summary>Grants the selected group to the selected holder.</summary>
    /// <returns>A task that completes when the grant is saved.</returns>
    [RelayCommand]
    public Task GrantGroupAsync() => RunAsync(async token =>
    {
        if (SelectedHolder is not { } holder || SelectedGroup is not { } group)
        {
            return;
        }

        Report(await _client
            .SetHolderGroupAsync(holder.Id, new GrantRequest(group.Id, true), token)
            .ConfigureAwait(true));

        await LoadAsync().ConfigureAwait(true);
    });

    /// <summary>Withdraws the selected group from the selected holder.</summary>
    /// <returns>A task that completes when the change is saved.</returns>
    [RelayCommand]
    public Task WithdrawGroupAsync() => RunAsync(async token =>
    {
        if (SelectedHolder is not { } holder || SelectedGroup is not { } group)
        {
            return;
        }

        Report(await _client
            .SetHolderGroupAsync(holder.Id, new GrantRequest(group.Id, false), token)
            .ConfigureAwait(true));

        await LoadAsync().ConfigureAwait(true);
    });

    /// <summary>Creates an item group.</summary>
    /// <returns>A task that completes when the group is created.</returns>
    [RelayCommand]
    public Task CreateGroupAsync() => RunAsync(async token =>
    {
        var result = await _client
            .CreateGroupAsync(new CreateGroupRequest(NewGroupName.Trim(), null), token)
            .ConfigureAwait(true);

        Report(result);

        if (result.Success)
        {
            NewGroupName = string.Empty;
            await LoadAsync().ConfigureAwait(true);
        }
    });

    /// <summary>Creates an item in the selected group.</summary>
    /// <returns>A task that completes when the item is created.</returns>
    [RelayCommand]
    public Task CreateItemAsync() => RunAsync(async token =>
    {
        if (SelectedGroup is not { } group)
        {
            _notifications.Problem("Choose a group for the item first.");
            return;
        }

        var result = await _client
            .CreateItemAsync(
                new CreateItemRequest(NewItemReference.Trim(), NewItemDescription.Trim(), group.Id),
                token)
            .ConfigureAwait(true);

        Report(result);

        if (result.Success)
        {
            NewItemReference = string.Empty;
            NewItemDescription = string.Empty;
            await LoadAsync().ConfigureAwait(true);
        }
    });

    private void Report(CommandResult result)
    {
        if (result.Success)
        {
            _notifications.Success(result.Message);
        }
        else
        {
            _notifications.Problem(result.Message);
        }
    }
}
