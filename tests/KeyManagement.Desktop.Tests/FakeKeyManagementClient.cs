using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KeyManagement.Contracts;
using KeyManagement.Desktop.Services;

namespace KeyManagement.Desktop.Tests;

/// <summary>
/// A server that answers from memory.
/// </summary>
/// <remarks>
/// The view models depend on <see cref="IKeyManagementClient"/> and nothing else, which is what
/// lets the whole client be exercised here with no host, no database and no network.
/// </remarks>
public sealed class FakeKeyManagementClient : IKeyManagementClient
{
    private readonly Guid _cabinetId = Guid.CreateVersion7();

    /// <summary>Items the fake server holds.</summary>
    public List<AssetSummary> Items { get; } = [];

    /// <summary>Positions in the cabinet.</summary>
    public List<SlotSummary> Slots { get; } = [];

    /// <summary>Checkouts that have not settled.</summary>
    public List<CheckoutSummary> OpenCheckouts { get; } = [];

    /// <summary>Records in the activity trail.</summary>
    public List<AuditEventSummary> Activity { get; } = [];

    /// <summary>What the next sign-in returns.</summary>
    public CommandResult<SessionResponse>? SignInResult { get; set; }

    /// <summary>What the next custody command returns.</summary>
    public CommandResult<CheckoutSummary>? CommandResult { get; set; }

    /// <summary>How many custody requests were made.</summary>
    public int RequestCount { get; private set; }

    /// <summary>How many returns were started.</summary>
    public int ReturnCount { get; private set; }

    /// <inheritdoc />
    public SessionResponse? Session { get; private set; }

    /// <summary>Fills the fake with one cabinet, five positions and three items.</summary>
    /// <returns>The client, for chaining.</returns>
    public FakeKeyManagementClient WithSeedData()
    {
        var group = Guid.CreateVersion7();

        AssetSummary Item(string reference, string description, string state) =>
            new(Guid.CreateVersion7(), reference, description, group, "Plant room",
                state, "Reception", null);

        var available = Item("PR-001", "Boiler house main door", "Available");
        var out1 = Item("PR-002", "Riser cupboard", "CheckedOut");
        var unknown = Item("PR-003", "Roof plant enclosure", "Unknown");
        Items.AddRange([available, out1, unknown]);

        Slots.AddRange(
        [
            new SlotSummary(Guid.CreateVersion7(), "A01", "Occupied", DateTimeOffset.UtcNow, available.Id, available.Reference),
            new SlotSummary(Guid.CreateVersion7(), "A02", "Empty", DateTimeOffset.UtcNow, out1.Id, out1.Reference),
            new SlotSummary(Guid.CreateVersion7(), "A03", "Unknown", DateTimeOffset.UtcNow, unknown.Id, unknown.Reference),

            // Two positions with nothing assigned, as most of a real cabinet is.
            new SlotSummary(Guid.CreateVersion7(), "A04", "Empty", null, null, null),
            new SlotSummary(Guid.CreateVersion7(), "A05", "Empty", null, null, null),
        ]);

        OpenCheckouts.Add(new CheckoutSummary(
            Guid.CreateVersion7(), out1.Id, out1.Reference, Guid.CreateVersion7(), "J Smith",
            "Active", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-2),
            DateTimeOffset.UtcNow.AddHours(4), null, null));

        return this;
    }

    /// <inheritdoc />
    public Task<CommandResult<SessionResponse>> SignInAsync(
        string userName, string password, CancellationToken cancellationToken = default)
    {
        var result = SignInResult ?? new CommandResult<SessionResponse>(
            true, "Signed in.", Guid.CreateVersion7(), "Active",
            new SessionResponse("token", "refresh", DateTimeOffset.UtcNow.AddMinutes(15),
                Guid.CreateVersion7(), "Administrator", ["CheckoutAsset", "ViewAudit"]));

        if (result.Success)
        {
            Session = result.Data;
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public void SignOut() => Session = null;

    /// <inheritdoc />
    public Task<DashboardSummary> GetDashboardAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DashboardSummary([], OpenCheckouts, [], Activity));

    /// <inheritdoc />
    public Task<IReadOnlyList<AssetSummary>> ListItemsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AssetSummary>>(Items);

    /// <inheritdoc />
    public Task<IReadOnlyList<CabinetSummary>> ListCabinetsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CabinetSummary>>(
        [
            new CabinetSummary(_cabinetId, "Reception", "Ground floor", "Online",
                DateTimeOffset.UtcNow, "1.4.2", Slots.Count),
        ]);

    /// <inheritdoc />
    public Task<CabinetSnapshot?> GetCabinetSnapshotAsync(
        Guid cabinetId, CancellationToken cancellationToken = default) =>
        Task.FromResult<CabinetSnapshot?>(new CabinetSnapshot(
            new CabinetSummary(_cabinetId, "Reception", "Ground floor", "Online",
                DateTimeOffset.UtcNow, "1.4.2", Slots.Count),
            Slots));

    /// <inheritdoc />
    public Task<IReadOnlyList<CheckoutSummary>> ListOpenCheckoutsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CheckoutSummary>>(OpenCheckouts);

    /// <inheritdoc />
    public Task<CommandResult<CheckoutSummary>> RequestItemAsync(
        Guid itemId, DateTimeOffset? curfew, CancellationToken cancellationToken = default)
    {
        RequestCount++;
        return Task.FromResult(CommandResult ?? Succeeded("Released."));
    }

    /// <inheritdoc />
    public Task<CommandResult<CheckoutSummary>> ReturnItemAsync(
        Guid checkoutId, CancellationToken cancellationToken = default)
    {
        ReturnCount++;
        return Task.FromResult(CommandResult ?? Succeeded("Put it back in its position."));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditEventSummary>> SearchActivityAsync(
        AuditQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AuditEventSummary>>(
            Activity.Take(query.Take).ToList());

    private static CommandResult<CheckoutSummary> Succeeded(string message) =>
        new(true, message, Guid.CreateVersion7(), "Pending", null);
}

/// <summary>
/// A live feed nothing is connected to, which tests raise by hand.
/// </summary>
public sealed class FakeLiveActivityFeed : ILiveActivityFeed
{
    /// <inheritdoc />
    public event Action<AuditEventSummary>? ActivityReceived;

    /// <inheritdoc />
    public bool IsConnected { get; private set; }

    /// <inheritdoc />
    public Task ConnectAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    /// <summary>Pushes a record as the server would.</summary>
    /// <param name="activity">What happened.</param>
    public void Push(AuditEventSummary activity) => ActivityReceived?.Invoke(activity);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Records what a view model asked the shell to do.</summary>
public sealed class RecordingNavigation : INavigationService
{
    /// <summary>Where it was asked to go, in order.</summary>
    public List<Destination> Requested { get; } = [];

    /// <inheritdoc />
    public void Show(Destination destination) => Requested.Add(destination);
}

/// <summary>Records what was said to the operator.</summary>
public sealed class RecordingNotifications : INotificationService
{
    /// <summary>Confirmations, in order.</summary>
    public List<string> Successes { get; } = [];

    /// <summary>Refusals and failures, in order.</summary>
    public List<string> Problems { get; } = [];

    /// <inheritdoc />
    public void Success(string message) => Successes.Add(message);

    /// <inheritdoc />
    public void Problem(string message) => Problems.Add(message);
}
