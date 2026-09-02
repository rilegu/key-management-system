using System.Globalization;
using KeyManagement.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KeyManagement.Infrastructure.Persistence;

// One converter per typed identifier, registered in ConfigureConventions so no individual
// property ever has to mention one. Grouped in a single file because each is one line and the
// set only makes sense read together.

/// <summary>Stores a <see cref="UserId"/> as its underlying value.</summary>
public sealed class UserIdConverter : ValueConverter<UserId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public UserIdConverter()
        : base(id => id.Value, value => new UserId(value))
    {
    }
}

/// <summary>Stores a <see cref="RoleId"/> as its underlying value.</summary>
public sealed class RoleIdConverter : ValueConverter<RoleId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public RoleIdConverter()
        : base(id => id.Value, value => new RoleId(value))
    {
    }
}

/// <summary>Stores an <see cref="AssetId"/> as its underlying value.</summary>
public sealed class AssetIdConverter : ValueConverter<AssetId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public AssetIdConverter()
        : base(id => id.Value, value => new AssetId(value))
    {
    }
}

/// <summary>Stores an <see cref="AssetGroupId"/> as its underlying value.</summary>
public sealed class AssetGroupIdConverter : ValueConverter<AssetGroupId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public AssetGroupIdConverter()
        : base(id => id.Value, value => new AssetGroupId(value))
    {
    }
}

/// <summary>Stores a <see cref="CabinetId"/> as its underlying value.</summary>
public sealed class CabinetIdConverter : ValueConverter<CabinetId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public CabinetIdConverter()
        : base(id => id.Value, value => new CabinetId(value))
    {
    }
}

/// <summary>Stores a <see cref="SlotId"/> as its underlying value.</summary>
public sealed class SlotIdConverter : ValueConverter<SlotId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public SlotIdConverter()
        : base(id => id.Value, value => new SlotId(value))
    {
    }
}

/// <summary>Stores a <see cref="CheckoutId"/> as its underlying value.</summary>
public sealed class CheckoutIdConverter : ValueConverter<CheckoutId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public CheckoutIdConverter()
        : base(id => id.Value, value => new CheckoutId(value))
    {
    }
}

/// <summary>Stores an <see cref="AuditEventId"/> as its underlying value.</summary>
public sealed class AuditEventIdConverter : ValueConverter<AuditEventId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public AuditEventIdConverter()
        : base(id => id.Value, value => new AuditEventId(value))
    {
    }
}

/// <summary>Stores a <see cref="DeviceEventId"/> as its underlying value.</summary>
public sealed class DeviceEventIdConverter : ValueConverter<DeviceEventId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public DeviceEventIdConverter()
        : base(id => id.Value, value => new DeviceEventId(value))
    {
    }
}

/// <summary>Stores a <see cref="RefreshTokenId"/> as its underlying value.</summary>
public sealed class RefreshTokenIdConverter : ValueConverter<RefreshTokenId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public RefreshTokenIdConverter()
        : base(id => id.Value, value => new RefreshTokenId(value))
    {
    }
}

/// <summary>Stores an <see cref="AlarmId"/> as its underlying value.</summary>
public sealed class AlarmIdConverter : ValueConverter<AlarmId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public AlarmIdConverter()
        : base(id => id.Value, value => new AlarmId(value))
    {
    }
}

/// <summary>Stores a <see cref="CorrelationId"/> as its underlying value.</summary>
public sealed class CorrelationIdConverter : ValueConverter<CorrelationId, Guid>
{
    /// <summary>Creates the converter.</summary>
    public CorrelationIdConverter()
        : base(id => id.Value, value => new CorrelationId(value))
    {
    }
}

/// <summary>
/// Stores every timestamp as fixed-width UTC ISO-8601 text.
/// </summary>
/// <remarks>
/// <para>
/// Not the provider default, and not optional. SQLite's own mapping keeps each value's
/// original offset, so two moments in different offsets do not sort chronologically as text —
/// EF refuses <c>ORDER BY</c> on a <see cref="DateTimeOffset"/> outright rather than returning
/// a wrong answer. That would take out the audit trail's main query, the overdue sweep and
/// checkout history, all of which order by time.
/// </para>
/// <para>
/// Normalising to UTC in a fixed-width format makes text order and chronological order the
/// same thing, so the indexes on these columns do real work. It also enforces the rule that
/// the database holds UTC and only the UI converts, which is more reliable than trusting
/// every call site to have passed a UTC value.
/// </para>
/// </remarks>
public sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, string>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>Creates the converter.</summary>
    public UtcDateTimeOffsetConverter()
        : base(
            value => value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture),
            text => DateTimeOffset.ParseExact(
                text,
                Format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))
    {
    }
}
