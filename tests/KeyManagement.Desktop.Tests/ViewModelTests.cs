using System;
using System.Linq;
using System.Threading.Tasks;
using KeyManagement.Contracts;
using KeyManagement.Desktop;
using KeyManagement.Desktop.Services;
using KeyManagement.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KeyManagement.Desktop.Tests;

/// <summary>
/// Sign-in behaviour.
/// </summary>
public sealed class SignInViewModelTests
{
    [Fact]
    public void Sign_in_is_unavailable_until_both_fields_are_filled()
    {
        var screen = new SignInViewModel(new FakeKeyManagementClient(), new RecordingNavigation());

        Assert.False(screen.SignInCommand.CanExecute(null));

        screen.UserName = "admin";
        Assert.False(screen.SignInCommand.CanExecute(null));

        screen.Password = "secret";
        Assert.True(screen.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_refusal_is_shown_as_the_server_wrote_it()
    {
        // Not reinterpreted here. The wording is deliberately identical for an unknown account
        // and a wrong password, and rewriting it client-side would undo that.
        var client = new FakeKeyManagementClient
        {
            SignInResult = new CommandResult<SessionResponse>(
                false, "The user name or password is not correct.",
                Guid.CreateVersion7(), "Denied", null),
        };
        var navigation = new RecordingNavigation();
        var screen = new SignInViewModel(client, navigation) { UserName = "admin", Password = "wrong" };

        await screen.SignInCommand.ExecuteAsync(null);

        Assert.Equal("The user name or password is not correct.", screen.ErrorMessage);
        Assert.Empty(navigation.Requested);
    }

    [Fact]
    public async Task A_successful_sign_in_clears_the_password_and_moves_on()
    {
        var navigation = new RecordingNavigation();
        var screen = new SignInViewModel(new FakeKeyManagementClient(), navigation)
        {
            UserName = "admin",
            Password = "correct horse battery staple",
        };

        await screen.SignInCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, screen.Password);
        Assert.Equal(Destination.SystemViewer, Assert.Single(navigation.Requested));
    }
}

/// <summary>
/// The position board.
/// </summary>
public sealed class SystemViewerViewModelTests
{
    private static SystemViewerViewModel Create(
        out FakeKeyManagementClient client,
        out FakeLiveActivityFeed feed,
        out RecordingNotifications notifications)
    {
        client = new FakeKeyManagementClient().WithSeedData();
        feed = new FakeLiveActivityFeed();
        notifications = new RecordingNotifications();
        return new SystemViewerViewModel(client, notifications, feed);
    }

    [Fact]
    public async Task The_board_shows_a_tile_for_every_position()
    {
        var screen = Create(out var client, out _, out _);

        await screen.LoadAsync();

        Assert.Equal(client.Slots.Count, screen.Positions.Count);
        Assert.Equal("3 of 5 positions assigned", screen.Occupancy);
    }

    [Fact]
    public async Task Each_position_carries_the_state_its_item_is_in()
    {
        var screen = Create(out _, out _, out _);

        await screen.LoadAsync();

        var available = screen.Positions.Single(p => p.Position == "A01");
        var outOfCabinet = screen.Positions.Single(p => p.Position == "A02");
        var unconfirmed = screen.Positions.Single(p => p.Position == "A03");
        var unassigned = screen.Positions.Single(p => p.Position == "A04");

        Assert.True(available.IsIn);
        Assert.Equal("In cabinet", available.StateWord);
        Assert.True(outOfCabinet.IsOut);
        Assert.Equal("Out of cabinet", outOfCabinet.StateWord);
        Assert.True(unconfirmed.IsUnconfirmed);
        Assert.Equal("Not confirmed", unconfirmed.StateWord);

        // A position with nothing assigned reads as empty whatever the hardware reports.
        Assert.True(unassigned.IsEmpty);
        Assert.Equal("No item", unassigned.StateWord);
    }

    [Fact]
    public async Task Selecting_a_position_fills_the_detail_panel_with_who_holds_it()
    {
        var screen = Create(out _, out _, out _);
        await screen.LoadAsync();

        screen.SelectPosition(screen.Positions.Single(p => p.Position == "A02"));

        Assert.True(screen.HasSelection);
        Assert.Equal("J Smith", screen.HeldBy);
        Assert.NotEqual("—", screen.Curfew);
        Assert.True(screen.Positions.Single(p => p.Position == "A02").IsSelected);
        Assert.False(screen.Positions.Single(p => p.Position == "A01").IsSelected);
    }

    [Fact]
    public async Task Release_is_offered_only_for_an_item_in_its_position()
    {
        var screen = Create(out _, out _, out _);
        await screen.LoadAsync();

        screen.SelectPosition(screen.Positions.Single(p => p.Position == "A01"));
        Assert.True(screen.RequestItemCommand.CanExecute(null));
        Assert.False(screen.ReturnItemCommand.CanExecute(null));

        screen.SelectPosition(screen.Positions.Single(p => p.Position == "A02"));
        Assert.False(screen.RequestItemCommand.CanExecute(null));
        Assert.True(screen.ReturnItemCommand.CanExecute(null));

        // Nothing to do with an empty position, or one whose item is unaccounted for.
        screen.SelectPosition(screen.Positions.Single(p => p.Position == "A04"));
        Assert.False(screen.RequestItemCommand.CanExecute(null));
        Assert.False(screen.ReturnItemCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_refused_request_is_shown_as_a_problem_carrying_the_servers_reason()
    {
        var screen = Create(out var client, out _, out var notifications);
        await screen.LoadAsync();

        client.CommandResult = new CommandResult<CheckoutSummary>(
            false, "This item is not in a group you may check out from.",
            Guid.CreateVersion7(), "Denied", null);

        screen.SelectPosition(screen.Positions.Single(p => p.Position == "A01"));
        await screen.RequestItemAsync();

        Assert.Equal(
            "This item is not in a group you may check out from.",
            Assert.Single(notifications.Problems));
        Assert.Empty(notifications.Successes);
    }

    [Fact]
    public async Task A_permitted_request_is_confirmed()
    {
        var screen = Create(out var client, out _, out var notifications);
        await screen.LoadAsync();

        screen.SelectPosition(screen.Positions.Single(p => p.Position == "A01"));
        await screen.RequestItemAsync();

        Assert.Single(notifications.Successes);
        Assert.Equal(1, client.RequestCount);
    }

    [Fact]
    public async Task A_live_record_appears_at_the_top_of_the_feed()
    {
        var screen = Create(out _, out var feed, out _);
        await screen.LoadAsync();

        feed.Push(new AuditEventSummary(
            Guid.CreateVersion7(), "CheckoutAuthorized", DateTimeOffset.UtcNow,
            Guid.CreateVersion7(), "Authorized PR-001 to J Smith.", null, null, null));

        var first = Assert.Single(screen.Activity);
        Assert.Equal("Item released", first.What);
    }

    [Fact]
    public async Task The_feed_does_not_grow_without_bound()
    {
        var screen = Create(out _, out var feed, out _);
        await screen.LoadAsync();

        for (var i = 0; i < 60; i++)
        {
            feed.Push(new AuditEventSummary(
                Guid.CreateVersion7(), "SignInSucceeded", DateTimeOffset.UtcNow,
                Guid.CreateVersion7(), "Signed in.", null, null, null));
        }

        Assert.Equal(40, screen.Activity.Count);
    }
}

/// <summary>
/// The items table and its filters.
/// </summary>
public sealed class ItemsViewModelTests
{
    private static async Task<ItemsViewModel> LoadedAsync(FakeKeyManagementClient client)
    {
        var screen = new ItemsViewModel(client, new RecordingNotifications());
        await screen.LoadAsync();
        return screen;
    }

    [Fact]
    public async Task Every_item_is_listed_with_where_it_is()
    {
        var screen = await LoadedAsync(new FakeKeyManagementClient().WithSeedData());

        Assert.Equal(3, screen.Items.Count);
        Assert.Equal("3 items", screen.ResultSummary);

        var held = screen.Items.Single(i => i.Reference == "PR-002");
        Assert.Equal("J Smith", held.HeldBy);
        Assert.True(held.IsOut);
    }

    [Fact]
    public async Task Searching_matches_reference_and_description()
    {
        var screen = await LoadedAsync(new FakeKeyManagementClient().WithSeedData());

        screen.SearchText = "boiler";
        Assert.Equal("PR-001", Assert.Single(screen.Items).Reference);

        screen.SearchText = "PR-003";
        Assert.Equal("PR-003", Assert.Single(screen.Items).Reference);

        screen.SearchText = string.Empty;
        Assert.Equal(3, screen.Items.Count);
    }

    [Fact]
    public async Task Out_of_cabinet_only_narrows_to_what_is_held()
    {
        var screen = await LoadedAsync(new FakeKeyManagementClient().WithSeedData());

        screen.OutOnly = true;

        Assert.Equal("PR-002", Assert.Single(screen.Items).Reference);
        Assert.Equal("1 of 3 items", screen.ResultSummary);
    }

    [Fact]
    public async Task Actions_are_offered_only_where_they_make_sense()
    {
        var screen = await LoadedAsync(new FakeKeyManagementClient().WithSeedData());

        var available = screen.Items.Single(i => i.Reference == "PR-001");
        var held = screen.Items.Single(i => i.Reference == "PR-002");
        var unconfirmed = screen.Items.Single(i => i.Reference == "PR-003");

        Assert.True(available.CanRequest);
        Assert.False(available.CanReturn);
        Assert.False(held.CanRequest);
        Assert.True(held.CanReturn);

        // Neither, until its whereabouts are reconciled.
        Assert.False(unconfirmed.CanRequest);
        Assert.False(unconfirmed.CanReturn);
    }
}

/// <summary>
/// The alarm list.
/// </summary>
public sealed class AlarmsViewModelTests
{
    private static AlarmSummary Alarm(string type, string severity, string status) =>
        new(Guid.CreateVersion7(), type, severity, status, $"{type} happened.",
            DateTimeOffset.UtcNow, Guid.CreateVersion7(), null, "PR-001", null, null, null);

    [Fact]
    public async Task Outstanding_only_hides_what_has_been_dealt_with()
    {
        var client = new FakeKeyManagementClient();
        client.Alarms.Add(Alarm("OverdueItem", "Warning", "Active"));
        client.Alarms.Add(Alarm("PositionFault", "Warning", "Acknowledged"));

        var screen = new AlarmsViewModel(client, new RecordingNotifications());
        await screen.LoadAsync();

        Assert.Single(screen.Alarms);

        screen.ActiveOnly = false;
        await screen.LoadAsync();

        Assert.Equal(2, screen.Alarms.Count);
    }

    [Fact]
    public async Task Severity_reaches_the_row_as_a_style_class()
    {
        // Colour is chosen here, never in the view, so both themes follow without a template
        // knowing anything about alarms.
        var client = new FakeKeyManagementClient();
        client.Alarms.Add(Alarm("UnauthorizedRemoval", "Critical", "Active"));
        client.Alarms.Add(Alarm("OverdueItem", "Warning", "Active"));
        client.Alarms.Add(Alarm("UncollectedRelease", "Information", "Active"));

        var screen = new AlarmsViewModel(client, new RecordingNotifications());
        await screen.LoadAsync();

        Assert.True(screen.Alarms.Single(a => a.What == "Unauthorised removal").IsCritical);
        Assert.True(screen.Alarms.Single(a => a.What == "Overdue item").IsWarning);
        Assert.True(screen.Alarms.Single(a => a.What == "Released, not collected").IsInformation);
    }

    [Fact]
    public async Task Acknowledging_tells_the_server_and_reloads()
    {
        // The reload is the part worth asserting. A command that acts and then refreshes runs
        // the shared async helper twice, and an earlier version of it refused the inner call —
        // so the server was told and the screen never changed.
        var client = new FakeKeyManagementClient();
        client.Alarms.Add(Alarm("OverdueItem", "Warning", "Active"));

        var notifications = new RecordingNotifications();
        var screen = new AlarmsViewModel(client, notifications);
        await screen.LoadAsync();
        Assert.Single(screen.Alarms);

        var acknowledged = screen.Alarms[0];
        client.Alarms.Clear();

        await screen.AcknowledgeAsync(acknowledged);

        Assert.Single(client.Acknowledged);
        Assert.Single(notifications.Successes);
        Assert.Empty(screen.Alarms);
    }

    [Fact]
    public async Task An_empty_list_says_so_rather_than_showing_nothing()
    {
        var screen = new AlarmsViewModel(new FakeKeyManagementClient(), new RecordingNotifications());

        await screen.LoadAsync();

        Assert.Equal("Nothing outstanding.", screen.ResultSummary);
    }
}

/// <summary>
/// Holders, groups and items.
/// </summary>
public sealed class AdministrationViewModelTests
{
    private static FakeKeyManagementClient Seeded()
    {
        var client = new FakeKeyManagementClient();
        client.HolderList.Add(new HolderSummary(
            Guid.CreateVersion7(), "admin", "Administrator", "Active", true,
            ["Administrator"], ["Plant room"]));
        client.HolderList.Add(new HolderSummary(
            Guid.CreateVersion7(), "jsmith", "J Smith", "Suspended", false, [], []));
        client.GroupList.Add(new AssetGroupSummary(Guid.CreateVersion7(), "Plant room", null, 3));
        return client;
    }

    [Fact]
    public async Task Holders_show_what_each_has_been_granted()
    {
        var screen = new AdministrationViewModel(Seeded(), new RecordingNotifications());

        await screen.LoadAsync();

        var admin = screen.Holders.Single(h => h.UserName == "admin");
        Assert.True(admin.IsActive);
        Assert.Equal("PIN set", admin.Keypad);
        Assert.Equal("Plant room", admin.Groups);

        // Nothing granted reads as a dash rather than as blank, so an empty cell is never
        // mistaken for a column that failed to load.
        var other = screen.Holders.Single(h => h.UserName == "jsmith");
        Assert.False(other.IsActive);
        Assert.Equal("—", other.Roles);
        Assert.Equal("—", other.Keypad);
    }

    [Fact]
    public async Task Suspending_sends_the_opposite_of_the_current_status()
    {
        var client = Seeded();
        var screen = new AdministrationViewModel(client, new RecordingNotifications());
        await screen.LoadAsync();

        await screen.ToggleStatusAsync(screen.Holders.Single(h => h.UserName == "admin"));
        await screen.ToggleStatusAsync(screen.Holders.Single(h => h.UserName == "jsmith"));

        Assert.Equal("Suspended", client.StatusChanges[0].Status);
        Assert.Equal("Active", client.StatusChanges[1].Status);
    }

    [Fact]
    public async Task Granting_a_group_needs_both_a_holder_and_a_group()
    {
        var client = Seeded();
        var screen = new AdministrationViewModel(client, new RecordingNotifications());
        await screen.LoadAsync();

        // No holder chosen yet.
        await screen.GrantGroupAsync();
        Assert.Empty(client.GroupChanges);

        screen.SelectedHolder = screen.Holders.Single(h => h.UserName == "jsmith");
        await screen.GrantGroupAsync();

        var change = Assert.Single(client.GroupChanges);
        Assert.True(change.Granted);
        Assert.Equal(screen.SelectedHolder.Id, change.Holder);
    }

    [Fact]
    public async Task Withdrawing_a_group_sends_the_opposite_of_granting()
    {
        var client = Seeded();
        var screen = new AdministrationViewModel(client, new RecordingNotifications());
        await screen.LoadAsync();
        screen.SelectedHolder = screen.Holders[0];

        await screen.WithdrawGroupAsync();

        Assert.False(Assert.Single(client.GroupChanges).Granted);
    }

    [Fact]
    public async Task A_refused_creation_keeps_what_was_typed()
    {
        // The usual reason is a name already taken, and retyping the rest helps nobody.
        var client = Seeded();
        client.AdministrationResult = new CommandResult(
            false, "'jsmith' is already taken.", Guid.CreateVersion7(), "Denied");

        var notifications = new RecordingNotifications();
        var screen = new AdministrationViewModel(client, notifications)
        {
            NewUserName = "jsmith",
            NewDisplayName = "J Smith",
            NewPassword = "secret",
        };

        await screen.CreateHolderAsync();

        Assert.Equal("jsmith", screen.NewUserName);
        Assert.Equal("secret", screen.NewPassword);
        Assert.Single(notifications.Problems);
    }

    [Fact]
    public async Task A_created_holder_clears_the_fields_and_appears_in_the_list()
    {
        var client = Seeded();
        var screen = new AdministrationViewModel(client, new RecordingNotifications())
        {
            NewUserName = "anewbie",
            NewPassword = "secret",
        };

        await screen.CreateHolderAsync();

        Assert.Equal(string.Empty, screen.NewUserName);
        Assert.Equal(string.Empty, screen.NewPassword);
        Assert.Contains(screen.Holders, h => h.UserName == "anewbie");
    }

    [Fact]
    public async Task An_item_needs_a_group_chosen_first()
    {
        var client = new FakeKeyManagementClient();
        var notifications = new RecordingNotifications();
        var screen = new AdministrationViewModel(client, notifications)
        {
            NewItemReference = "PR-004",
            NewItemDescription = "A door",
        };

        await screen.LoadAsync();
        await screen.CreateItemAsync();

        Assert.Single(notifications.Problems);
    }
}

/// <summary>
/// How the application shuts down.
/// </summary>
public sealed class ShutdownTests
{
    [Fact]
    public void Every_disposable_service_can_be_torn_down_synchronously()
    {
        // The container disposes itself synchronously when the window closes, and it throws on
        // a service that offers only IAsyncDisposable. That crashed the application on exit,
        // and nothing else here closes a window, so the contract is asserted directly.
        var services = new ServiceCollection();
        services.AddSingleton<ILiveActivityFeed>(
            _ => new LiveActivityFeed(new Uri("http://localhost:5140")));

        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<ILiveActivityFeed>();

        provider.Dispose();
    }

    [Fact]
    public void The_feed_survives_being_disposed_before_it_ever_connected()
    {
        var feed = new LiveActivityFeed(new Uri("http://localhost:5140"));

        feed.Dispose();
        feed.Dispose();
    }
}

/// <summary>
/// The words the interface uses.
/// </summary>
public sealed class VocabularyTests
{
    [Theory]
    [InlineData("Available", "In cabinet", "in")]
    [InlineData("CheckedOut", "Out of cabinet", "out")]
    [InlineData("CheckoutPending", "Released", "released")]
    [InlineData("ReturnPending", "Awaiting return", "released")]
    [InlineData("Faulted", "Fault", "fault")]
    [InlineData("Unknown", "Not confirmed", "unconfirmed")]
    public void Custody_states_read_as_this_industry_words(
        string state, string word, string styleClass)
    {
        Assert.Equal(word, Vocabulary.CustodyWord(state));
        Assert.Equal(styleClass, Vocabulary.CustodyClass(state));
    }

    [Theory]
    [InlineData("CheckoutDenied", true)]
    [InlineData("SignInFailed", true)]
    [InlineData("UnauthorizedSlotChange", true)]
    [InlineData("CheckoutAuthorized", false)]
    [InlineData("ReturnCompleted", false)]
    public void Refusals_and_faults_are_marked_in_the_trail(string type, bool expected) =>
        Assert.Equal(expected, Vocabulary.IsRefusal(type));

    [Fact]
    public void An_unrecognised_state_falls_back_rather_than_throwing()
    {
        // A server that grows a new state must not crash a client that has not caught up.
        Assert.Equal("SomethingNew", Vocabulary.CustodyWord("SomethingNew"));
        Assert.Equal("unconfirmed", Vocabulary.CustodyClass("SomethingNew"));
    }
}
