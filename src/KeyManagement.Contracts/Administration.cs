namespace KeyManagement.Contracts;

/// <summary>A holder, with what they have been granted.</summary>
/// <param name="Id">Identifies the holder.</param>
/// <param name="UserName">Their sign-in name.</param>
/// <param name="DisplayName">Name shown in the interface and in the trail.</param>
/// <param name="Status">Whether they may use the system.</param>
/// <param name="HasPin">Whether they can identify themselves at a cabinet keypad.</param>
/// <param name="Roles">Roles they hold.</param>
/// <param name="Groups">Item groups they may take from.</param>
public sealed record HolderSummary(
    Guid Id,
    string UserName,
    string DisplayName,
    string Status,
    bool HasPin,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Groups);

/// <summary>A role and what it allows.</summary>
/// <param name="Id">Identifies the role.</param>
/// <param name="Name">Its name.</param>
/// <param name="Permissions">What holders in it may do.</param>
public sealed record RoleSummary(Guid Id, string Name, IReadOnlyList<string> Permissions);

/// <summary>An item group.</summary>
/// <param name="Id">Identifies the group.</param>
/// <param name="Name">Its name.</param>
/// <param name="Description">What belongs in it.</param>
/// <param name="ItemCount">How many items are in it.</param>
public sealed record AssetGroupSummary(Guid Id, string Name, string? Description, int ItemCount);

/// <summary>A new holder.</summary>
/// <param name="UserName">Unique sign-in name.</param>
/// <param name="DisplayName">Name shown in the interface.</param>
/// <param name="Password">Their initial password.</param>
/// <param name="Pin">A PIN for cabinet keypads, if they should have one.</param>
public sealed record CreateHolderRequest(
    string UserName,
    string DisplayName,
    string Password,
    string? Pin);

/// <summary>A change to a holder.</summary>
/// <param name="DisplayName">Their name, unchanged if omitted.</param>
/// <param name="Status">Whether they may use the system.</param>
public sealed record AmendHolderRequest(string? DisplayName, string? Status);

/// <summary>A grant or withdrawal of a role or a group.</summary>
/// <param name="Id">The role or group.</param>
/// <param name="Granted">Whether it is being given or taken away.</param>
public sealed record GrantRequest(Guid Id, bool Granted);

/// <summary>A new item group.</summary>
/// <param name="Name">Unique name.</param>
/// <param name="Description">What belongs in it.</param>
public sealed record CreateGroupRequest(string Name, string? Description);

/// <summary>A new item.</summary>
/// <param name="Reference">Unique reference, as printed on the fob.</param>
/// <param name="Description">What it opens, or what it is.</param>
/// <param name="AssetGroupId">The group checkout access is granted through.</param>
public sealed record CreateItemRequest(string Reference, string Description, Guid AssetGroupId);
