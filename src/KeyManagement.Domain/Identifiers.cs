namespace KeyManagement.Domain;

// Typed identifiers rather than bare Guids. With ten entity types all keyed the same way,
// passing an AssetId where a SlotId belongs is a mistake the compiler should catch, not the
// database. They are grouped in one file because each is a single line and reading them
// together is what makes the set obvious.
//
// New() uses a version 7 GUID: time-ordered, so inserts land at the end of the index instead
// of scattering across it. On a table that only ever grows, that is most of the cost.

/// <summary>Identifies a holder.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct UserId(Guid Value)
{
    /// <summary>Allocates an identifier for a new holder.</summary>
    public static UserId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifies a role.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct RoleId(Guid Value)
{
    /// <summary>Allocates an identifier for a new role.</summary>
    public static RoleId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifies an asset.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct AssetId(Guid Value)
{
    /// <summary>Allocates an identifier for a new asset.</summary>
    public static AssetId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifies an asset group.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct AssetGroupId(Guid Value)
{
    /// <summary>Allocates an identifier for a new asset group.</summary>
    public static AssetGroupId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifies a cabinet.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct CabinetId(Guid Value)
{
    /// <summary>Allocates an identifier for a new cabinet.</summary>
    public static CabinetId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifies a slot.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct SlotId(Guid Value)
{
    /// <summary>Allocates an identifier for a new slot.</summary>
    public static SlotId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifies a checkout.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct CheckoutId(Guid Value)
{
    /// <summary>Allocates an identifier for a new checkout.</summary>
    public static CheckoutId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifies an audit record.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct AuditEventId(Guid Value)
{
    /// <summary>Allocates an identifier for a new audit record.</summary>
    public static AuditEventId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifies a device event.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct DeviceEventId(Guid Value)
{
    /// <summary>Allocates an identifier for a new device event.</summary>
    public static DeviceEventId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Identifies a refresh token.</summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct RefreshTokenId(Guid Value)
{
    /// <summary>Allocates an identifier for a new refresh token.</summary>
    public static RefreshTokenId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Ties a command to every record it produced, across the API, the database and the device
/// link. Assigned once at the edge and carried through, so a late device result can still be
/// matched to the request that caused it.
/// </summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct CorrelationId(Guid Value)
{
    /// <summary>Allocates an identifier for a new command.</summary>
    public static CorrelationId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
