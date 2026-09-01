namespace KeyManagement.Contracts;

/// <summary>A cabinet and whether the server can currently reach it.</summary>
/// <param name="Id">Identifies the cabinet.</param>
/// <param name="Name">Its name.</param>
/// <param name="Site">Where it physically is.</param>
/// <param name="Status">Whether the server has a working link to it.</param>
/// <param name="LastSeenAt">Last contact of any kind, UTC.</param>
/// <param name="FirmwareVersion">What it reported at its last handshake.</param>
/// <param name="SlotCount">How many slots it holds.</param>
public sealed record CabinetSummary(
    Guid Id,
    string Name,
    string Site,
    string Status,
    DateTimeOffset? LastSeenAt,
    string? FirmwareVersion,
    int SlotCount);

/// <summary>One slot and what it last reported.</summary>
/// <param name="Id">Identifies the slot.</param>
/// <param name="Position">Its position label within the cabinet.</param>
/// <param name="State">Its physical condition as last reported.</param>
/// <param name="LastReportedAt">When the cabinet last reported it, UTC.</param>
/// <param name="AssetId">The asset that lives here, when one is assigned.</param>
/// <param name="AssetReference">That asset's label.</param>
public sealed record SlotSummary(
    Guid Id,
    string Position,
    string State,
    DateTimeOffset? LastReportedAt,
    Guid? AssetId,
    string? AssetReference);

/// <summary>A cabinet with every slot it holds.</summary>
/// <param name="Cabinet">The cabinet.</param>
/// <param name="Slots">Its slots, in position order.</param>
public sealed record CabinetSnapshot(CabinetSummary Cabinet, IReadOnlyList<SlotSummary> Slots);
