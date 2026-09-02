using KeyManagement.Application.Abstractions;
using KeyManagement.Contracts;
using KeyManagement.Domain;
using KeyManagement.Domain.Access;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Auditing;

namespace KeyManagement.Application.Administration;

/// <summary>
/// Creating and amending the things custody decisions are made against.
/// </summary>
/// <remarks>
/// <para>
/// Every change here is audited, because these are the changes that decide what everyone else
/// may do. Granting someone a group is a more consequential act than any single checkout, and a
/// trail that records the checkouts but not the grant explains nothing.
/// </para>
/// <para>
/// Nothing is deleted. A holder is suspended, not removed, so the records naming them keep a
/// subject.
/// </para>
/// </remarks>
public sealed class AdministrationService
{
    private readonly IUserRepository _users;
    private readonly IAdministrationStore _store;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditTrail _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    /// <summary>Creates the service.</summary>
    /// <param name="users">Holders.</param>
    /// <param name="store">Everything else being administered.</param>
    /// <param name="passwordHasher">Hashes new passwords and PINs.</param>
    /// <param name="audit">The trail.</param>
    /// <param name="unitOfWork">Commits the work.</param>
    /// <param name="clock">The current time.</param>
    public AdministrationService(
        IUserRepository users,
        IAdministrationStore store,
        IPasswordHasher passwordHasher,
        IAuditTrail audit,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _users = users;
        _store = store;
        _passwordHasher = passwordHasher;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    /// <summary>Creates a holder.</summary>
    /// <param name="request">Who to create.</param>
    /// <param name="actor">Who is creating them.</param>
    /// <param name="correlationId">Ties this to its audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome.</returns>
    public async Task<CommandResult> CreateHolderAsync(
        CreateHolderRequest request,
        UserId actor,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.UserName)
            || string.IsNullOrWhiteSpace(request.DisplayName)
            || string.IsNullOrWhiteSpace(request.Password))
        {
            return Refused("A holder needs a user name, a display name and a password.", correlationId);
        }

        if (await _users.FindByUserNameAsync(request.UserName, cancellationToken).ConfigureAwait(false)
            is not null)
        {
            return Refused($"'{request.UserName}' is already taken.", correlationId);
        }

        var holder = new User(
            request.UserName, request.DisplayName, _passwordHasher.Hash(request.Password));

        if (!string.IsNullOrWhiteSpace(request.Pin))
        {
            holder.SetPinHash(_passwordHasher.Hash(request.Pin));
        }

        _store.Add(holder);
        await RecordAsync(actor, correlationId, $"created holder '{request.UserName}'", cancellationToken)
            .ConfigureAwait(false);

        return new CommandResult(true, $"'{request.UserName}' created.", correlationId.Value, "Active");
    }

    /// <summary>Amends a holder's name or status.</summary>
    /// <param name="holderId">Who to amend.</param>
    /// <param name="request">What to change.</param>
    /// <param name="actor">Who is changing it.</param>
    /// <param name="correlationId">Ties this to its audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome.</returns>
    public async Task<CommandResult> AmendHolderAsync(
        UserId holderId,
        AmendHolderRequest request,
        UserId actor,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var holder = await _users.FindByIdAsync(holderId, cancellationToken).ConfigureAwait(false);

        if (holder is null)
        {
            return Refused("No such holder.", correlationId);
        }

        // Suspending yourself locks you out with nobody left to undo it, if you are the only
        // administrator. Refusing is kinder than the alternative.
        if (holderId == actor && request.Status is not null and not nameof(UserStatus.Active))
        {
            return Refused("You cannot suspend your own account.", correlationId);
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            holder.SetDisplayName(request.DisplayName);
        }

        if (request.Status is not null
            && Enum.TryParse<UserStatus>(request.Status, ignoreCase: true, out var status))
        {
            holder.SetStatus(status);
        }

        await RecordAsync(
                actor, correlationId, $"amended holder '{holder.UserName}'", cancellationToken)
            .ConfigureAwait(false);

        return new CommandResult(true, "Saved.", correlationId.Value, holder.Status.ToString());
    }

    /// <summary>Grants or withdraws a role.</summary>
    /// <param name="holderId">The holder.</param>
    /// <param name="request">Which role, and whether it is being given or taken.</param>
    /// <param name="actor">Who is changing it.</param>
    /// <param name="correlationId">Ties this to its audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome.</returns>
    public async Task<CommandResult> SetRoleAsync(
        UserId holderId,
        GrantRequest request,
        UserId actor,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var holder = await _users.FindByIdAsync(holderId, cancellationToken).ConfigureAwait(false);
        var role = await _store.FindRoleAsync(new RoleId(request.Id), cancellationToken)
            .ConfigureAwait(false);

        if (holder is null || role is null)
        {
            return Refused("No such holder or role.", correlationId);
        }

        if (request.Granted)
        {
            holder.Grant(role);
        }
        else
        {
            holder.Revoke(role);
        }

        var what = request.Granted ? "granted" : "withdrew";
        await RecordAsync(
                actor,
                correlationId,
                $"{what} role '{role.Name}' {(request.Granted ? "to" : "from")} '{holder.UserName}'",
                cancellationToken)
            .ConfigureAwait(false);

        return new CommandResult(true, "Saved.", correlationId.Value, "Active");
    }

    /// <summary>Grants or withdraws access to an item group.</summary>
    /// <param name="holderId">The holder.</param>
    /// <param name="request">Which group, and whether it is being given or taken.</param>
    /// <param name="actor">Who is changing it.</param>
    /// <param name="correlationId">Ties this to its audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome.</returns>
    public async Task<CommandResult> SetGroupAsync(
        UserId holderId,
        GrantRequest request,
        UserId actor,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var holder = await _users.FindByIdAsync(holderId, cancellationToken).ConfigureAwait(false);
        var group = await _store.FindGroupAsync(new AssetGroupId(request.Id), cancellationToken)
            .ConfigureAwait(false);

        if (holder is null || group is null)
        {
            return Refused("No such holder or group.", correlationId);
        }

        if (request.Granted)
        {
            holder.GrantGroup(group.Id);
        }
        else
        {
            holder.RevokeGroup(group.Id);
        }

        var what = request.Granted ? "granted" : "withdrew";
        await RecordAsync(
                actor,
                correlationId,
                $"{what} group '{group.Name}' {(request.Granted ? "to" : "from")} '{holder.UserName}'",
                cancellationToken)
            .ConfigureAwait(false);

        return new CommandResult(true, "Saved.", correlationId.Value, "Active");
    }

    /// <summary>Creates an item group.</summary>
    /// <param name="request">The group.</param>
    /// <param name="actor">Who is creating it.</param>
    /// <param name="correlationId">Ties this to its audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome.</returns>
    public async Task<CommandResult> CreateGroupAsync(
        CreateGroupRequest request,
        UserId actor,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Refused("A group needs a name.", correlationId);
        }

        _store.Add(new AssetGroup(request.Name, request.Description));
        await RecordAsync(actor, correlationId, $"created group '{request.Name}'", cancellationToken)
            .ConfigureAwait(false);

        return new CommandResult(true, $"'{request.Name}' created.", correlationId.Value, "Active");
    }

    /// <summary>Creates an item.</summary>
    /// <param name="request">The item.</param>
    /// <param name="actor">Who is creating it.</param>
    /// <param name="correlationId">Ties this to its audit record.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome.</returns>
    public async Task<CommandResult> CreateItemAsync(
        CreateItemRequest request,
        UserId actor,
        CorrelationId correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Reference) || string.IsNullOrWhiteSpace(request.Description))
        {
            return Refused("An item needs a reference and a description.", correlationId);
        }

        var group = await _store.FindGroupAsync(new AssetGroupId(request.AssetGroupId), cancellationToken)
            .ConfigureAwait(false);

        if (group is null)
        {
            return Refused("No such group.", correlationId);
        }

        _store.Add(new Asset(request.Reference, request.Description, group.Id));
        await RecordAsync(
                actor,
                correlationId,
                $"created item '{request.Reference}' in group '{group.Name}'",
                cancellationToken)
            .ConfigureAwait(false);

        return new CommandResult(
            true, $"'{request.Reference}' created.", correlationId.Value, nameof(AssetCustodyState.Available));
    }

    private static CommandResult Refused(string message, CorrelationId correlationId) =>
        new(false, message, correlationId.Value, "Denied");

    private async Task RecordAsync(
        UserId actor,
        CorrelationId correlationId,
        string what,
        CancellationToken cancellationToken)
    {
        var by = await _users.FindByIdAsync(actor, cancellationToken).ConfigureAwait(false);

        _audit.Record(new AuditEvent(
                AuditEventType.ConfigurationChanged,
                _clock.UtcNow,
                correlationId,
                $"'{by?.UserName ?? actor.ToString()}' {what}.")
            .About(actor));

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
