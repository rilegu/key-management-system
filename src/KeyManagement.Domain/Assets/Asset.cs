using KeyManagement.Domain.Custody;

namespace KeyManagement.Domain.Assets;

/// <summary>
/// A key or item held in a cabinet and issued to holders.
/// </summary>
/// <remarks>
/// Every state change goes through one of the methods here, and each one asks
/// <see cref="CustodyTransitions"/> first. Nothing outside this type sets
/// <see cref="CustodyState"/>, which is what makes the state machine a rule rather than a
/// diagram in a document.
/// </remarks>
public sealed class Asset
{
    private Asset()
    {
        Reference = string.Empty;
        Description = string.Empty;
    }

    /// <summary>Creates an available asset.</summary>
    /// <param name="reference">Unique reference, typically stamped on the fob.</param>
    /// <param name="description">What the key opens, or what the item is.</param>
    /// <param name="assetGroupId">The group checkout access is granted through.</param>
    public Asset(string reference, string description, AssetGroupId assetGroupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        Id = AssetId.New();
        Reference = reference;
        Description = description;
        AssetGroupId = assetGroupId;
        CustodyState = AssetCustodyState.Available;
    }

    /// <summary>Identifies this asset.</summary>
    public AssetId Id { get; private set; }

    /// <summary>Unique across all assets, and the label a person reads off the fob.</summary>
    public string Reference { get; private set; }

    /// <summary>What the key opens, or what the item is.</summary>
    public string Description { get; private set; }

    /// <summary>The group checkout access is granted through.</summary>
    public AssetGroupId AssetGroupId { get; private set; }

    /// <summary>Where the asset is, as far as the system of record can tell.</summary>
    public AssetCustodyState CustodyState { get; private set; }

    /// <summary>Whether custody is currently uncertain and awaiting reconciliation.</summary>
    public bool IsUncertain =>
        CustodyState is AssetCustodyState.Faulted or AssetCustodyState.Unknown;

    /// <summary>Records that a checkout was authorized and the cabinet instructed.</summary>
    public void BeginCheckout() => MoveTo(AssetCustodyState.CheckoutPending);

    /// <summary>Records that the cabinet confirmed the asset was taken.</summary>
    public void ConfirmTaken() => MoveTo(AssetCustodyState.CheckedOut);

    /// <summary>Records that an authorized checkout was never collected.</summary>
    public void AbandonCheckout() => MoveTo(AssetCustodyState.Available);

    /// <summary>Records that a return was started.</summary>
    public void BeginReturn() => MoveTo(AssetCustodyState.ReturnPending);

    /// <summary>Records that the cabinet confirmed the asset is back in its slot.</summary>
    public void ConfirmReturned() => MoveTo(AssetCustodyState.Available);

    /// <summary>Records that a started return did not complete; the holder still has it.</summary>
    public void AbandonReturn() => MoveTo(AssetCustodyState.CheckedOut);

    /// <summary>Records that the cabinet reports a fault for this asset's slot.</summary>
    public void MarkFaulted() => MoveTo(AssetCustodyState.Faulted);

    /// <summary>Records that the server can no longer establish where the asset is.</summary>
    public void MarkUnknown() => MoveTo(AssetCustodyState.Unknown);

    /// <summary>Resolves uncertainty once the cabinet reports again.</summary>
    /// <param name="resolved">What the cabinet says: available or checked out.</param>
    /// <exception cref="ArgumentOutOfRangeException">The resolution is itself uncertain.</exception>
    /// <exception cref="InvalidCustodyTransitionException">The asset was not in an uncertain state.</exception>
    public void Reconcile(AssetCustodyState resolved)
    {
        if (resolved is not (AssetCustodyState.Available or AssetCustodyState.CheckedOut))
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolved),
                resolved,
                "Reconciliation must settle on a definite state.");
        }

        MoveTo(resolved);
    }

    /// <summary>Replaces the description and group.</summary>
    /// <param name="description">The new description.</param>
    /// <param name="assetGroupId">The new group.</param>
    public void Amend(string description, AssetGroupId assetGroupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Description = description;
        AssetGroupId = assetGroupId;
    }

    private void MoveTo(AssetCustodyState next)
    {
        CustodyTransitions.EnsureLegal(CustodyState, next, Id);
        CustodyState = next;
    }
}
