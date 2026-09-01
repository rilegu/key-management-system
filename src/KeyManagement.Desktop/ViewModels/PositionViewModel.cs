using System;
using CommunityToolkit.Mvvm.ComponentModel;
using KeyManagement.Contracts;

namespace KeyManagement.Desktop.ViewModels;

/// <summary>
/// One position on the board.
/// </summary>
/// <remarks>
/// State reaches the tile as a set of booleans, one per style class, because a view binds a
/// class to a boolean. Deriving them here keeps every colour decision out of the view and lets
/// the whole board re-theme without touching a template.
/// </remarks>
public sealed partial class PositionViewModel : ObservableObject
{
    /// <summary>Creates a position from a snapshot row.</summary>
    /// <param name="slot">The position as the server reported it.</param>
    /// <param name="item">The item assigned to it, if there is one.</param>
    public PositionViewModel(SlotSummary slot, AssetSummary? item)
    {
        ArgumentNullException.ThrowIfNull(slot);

        Position = slot.Position;
        ItemId = item?.Id;
        Reference = item?.Reference ?? "—";
        Description = item?.Description ?? string.Empty;
        LastReportedAt = slot.LastReportedAt;

        // A position with no item assigned is empty regardless of what the hardware reports,
        // and most of a real cabinet looks like this.
        if (item is null)
        {
            StateWord = "No item";
            StyleClass = "empty";
        }
        else
        {
            StateWord = Vocabulary.CustodyWord(item.CustodyState);
            StyleClass = Vocabulary.CustodyClass(item.CustodyState);
        }
    }

    /// <summary>Position label within the cabinet.</summary>
    public string Position { get; }

    /// <summary>The item assigned here, if any.</summary>
    public Guid? ItemId { get; }

    /// <summary>The item's reference, or a dash when the position is empty.</summary>
    public string Reference { get; }

    /// <summary>What the item is.</summary>
    public string Description { get; }

    /// <summary>What this position's state is called on screen.</summary>
    public string StateWord { get; }

    /// <summary>When the cabinet last reported this position.</summary>
    public DateTimeOffset? LastReportedAt { get; }

    /// <summary>Whether the operator has this position selected.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>The item is in its position.</summary>
    public bool IsIn => StyleClass == "in";

    /// <summary>The item is with a holder.</summary>
    public bool IsOut => StyleClass == "out";

    /// <summary>Released, or on its way back, and not yet confirmed either way.</summary>
    public bool IsReleased => StyleClass == "released";

    /// <summary>The cabinet reports a fault here.</summary>
    public bool IsFault => StyleClass == "fault";

    /// <summary>The server cannot establish where the item is.</summary>
    public bool IsUnconfirmed => StyleClass == "unconfirmed";

    /// <summary>Nothing is assigned to this position.</summary>
    public bool IsEmpty => StyleClass == "empty";

    private string StyleClass { get; }
}
