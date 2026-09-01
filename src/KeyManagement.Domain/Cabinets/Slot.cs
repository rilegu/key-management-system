namespace KeyManagement.Domain.Cabinets;

/// <summary>
/// One position in a cabinet, holding at most one asset.
/// </summary>
public sealed class Slot
{
    private Slot()
    {
        Position = string.Empty;
    }

    internal Slot(CabinetId cabinetId, string position)
    {
        Id = SlotId.New();
        CabinetId = cabinetId;
        Position = position;
        State = SlotState.Unknown;
    }

    /// <summary>Identifies this slot.</summary>
    public SlotId Id { get; private set; }

    /// <summary>The cabinet it belongs to.</summary>
    public CabinetId CabinetId { get; private set; }

    /// <summary>Position label, unique within its cabinet.</summary>
    public string Position { get; private set; }

    /// <summary>The asset that lives here, or <see langword="null"/> if the slot is unassigned.</summary>
    public AssetId? AssetId { get; private set; }

    /// <summary>Physical condition as last reported by the cabinet.</summary>
    public SlotState State { get; private set; }

    /// <summary>When the cabinet last reported this slot, UTC.</summary>
    public DateTimeOffset? LastReportedAt { get; private set; }

    /// <summary>Assigns an asset to this slot as its home position.</summary>
    /// <param name="assetId">The asset, or <see langword="null"/> to unassign.</param>
    public void Assign(AssetId? assetId) => AssetId = assetId;

    /// <summary>Records a state the cabinet reported.</summary>
    /// <param name="state">What the cabinet says.</param>
    /// <param name="at">When it said it, UTC.</param>
    public void Report(SlotState state, DateTimeOffset at)
    {
        State = state;
        LastReportedAt = at;
    }

    /// <summary>Marks the slot untrusted, typically because its cabinet went offline.</summary>
    /// <param name="at">When trust was lost, UTC.</param>
    public void MarkUnknown(DateTimeOffset at) => Report(SlotState.Unknown, at);
}
