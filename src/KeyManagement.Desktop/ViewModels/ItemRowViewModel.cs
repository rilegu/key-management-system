using System;
using KeyManagement.Contracts;

namespace KeyManagement.Desktop.ViewModels;

/// <summary>
/// One row in the items table.
/// </summary>
public sealed class ItemRowViewModel
{
    /// <summary>Creates a row.</summary>
    /// <param name="item">The item as the server reported it.</param>
    /// <param name="openCheckout">The open checkout for it, if it is out.</param>
    public ItemRowViewModel(AssetSummary item, CheckoutSummary? openCheckout)
    {
        ArgumentNullException.ThrowIfNull(item);

        Id = item.Id;
        Reference = item.Reference;
        Description = item.Description;
        GroupName = item.AssetGroupName;
        CabinetName = item.CabinetName ?? "—";
        Position = item.SlotPosition ?? "—";
        StateWord = Vocabulary.CustodyWord(item.CustodyState);
        StateClass = Vocabulary.CustodyClass(item.CustodyState);
        HeldBy = openCheckout?.UserDisplayName ?? "—";
        CheckoutId = openCheckout?.Id;

        CanRequest = item.CustodyState == "Available";
        CanReturn = openCheckout is not null && item.CustodyState == "CheckedOut";
    }

    /// <summary>Identifies the item.</summary>
    public Guid Id { get; }

    /// <summary>The label on the fob.</summary>
    public string Reference { get; }

    /// <summary>What it opens, or what it is.</summary>
    public string Description { get; }

    /// <summary>The group checkout access is granted through.</summary>
    public string GroupName { get; }

    /// <summary>The cabinet it belongs to.</summary>
    public string CabinetName { get; }

    /// <summary>Its position within that cabinet.</summary>
    public string Position { get; }

    /// <summary>What its state is called on screen.</summary>
    public string StateWord { get; }

    /// <summary>Who has it, when it is out.</summary>
    public string HeldBy { get; }

    /// <summary>The open checkout, when there is one.</summary>
    public Guid? CheckoutId { get; }

    /// <summary>Whether requesting it is worth offering.</summary>
    /// <remarks>
    /// Presentation only. The server decides, and will refuse a request this flag allowed if
    /// the holder is not entitled to the item.
    /// </remarks>
    public bool CanRequest { get; }

    /// <summary>Whether returning it is worth offering.</summary>
    public bool CanReturn { get; }

    /// <summary>The item is in its position.</summary>
    public bool IsIn => StateClass == "in";

    /// <summary>The item is with a holder.</summary>
    public bool IsOut => StateClass == "out";

    /// <summary>Released, or on its way back.</summary>
    public bool IsReleased => StateClass == "released";

    /// <summary>The cabinet reports a fault.</summary>
    public bool IsFault => StateClass == "fault";

    /// <summary>Whereabouts not established.</summary>
    public bool IsUnconfirmed => StateClass == "unconfirmed";

    private string StateClass { get; }
}
