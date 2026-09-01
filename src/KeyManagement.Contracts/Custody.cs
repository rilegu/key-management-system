namespace KeyManagement.Contracts;

/// <summary>A request to take custody of an asset.</summary>
/// <param name="AssetId">The asset wanted.</param>
/// <param name="DueAt">When it will be returned, UTC, or <see langword="null"/> if open-ended.</param>
public sealed record CheckoutRequest(Guid AssetId, DateTimeOffset? DueAt);

/// <summary>An asset and where it currently is.</summary>
/// <param name="Id">Identifies the asset.</param>
/// <param name="Reference">The label on the fob.</param>
/// <param name="Description">What it opens, or what it is.</param>
/// <param name="AssetGroupId">The group checkout access is granted through.</param>
/// <param name="AssetGroupName">That group's name.</param>
/// <param name="CustodyState">Where it is, as far as the system of record can tell.</param>
/// <param name="CabinetName">The cabinet it belongs to, when it is assigned to a slot.</param>
/// <param name="SlotPosition">The slot it belongs to, when it is assigned to one.</param>
public sealed record AssetSummary(
    Guid Id,
    string Reference,
    string Description,
    Guid AssetGroupId,
    string AssetGroupName,
    string CustodyState,
    string? CabinetName,
    string? SlotPosition);

/// <summary>A custody request and what became of it.</summary>
/// <param name="Id">Identifies the checkout.</param>
/// <param name="AssetId">The asset requested.</param>
/// <param name="AssetReference">That asset's label.</param>
/// <param name="UserId">The holder who asked.</param>
/// <param name="UserDisplayName">That holder's name.</param>
/// <param name="State">Where the request got to.</param>
/// <param name="RequestedAt">When the holder asked, UTC.</param>
/// <param name="TakenAt">When the asset was confirmed taken, UTC.</param>
/// <param name="DueAt">When it is due back, UTC.</param>
/// <param name="ReturnedAt">When it was confirmed back, UTC.</param>
/// <param name="DenialReason">Why it was refused, when it was.</param>
public sealed record CheckoutSummary(
    Guid Id,
    Guid AssetId,
    string AssetReference,
    Guid UserId,
    string UserDisplayName,
    string State,
    DateTimeOffset RequestedAt,
    DateTimeOffset? TakenAt,
    DateTimeOffset? DueAt,
    DateTimeOffset? ReturnedAt,
    string? DenialReason);
