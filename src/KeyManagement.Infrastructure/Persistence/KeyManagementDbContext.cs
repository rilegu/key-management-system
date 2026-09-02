using System.Reflection;
using KeyManagement.Domain;
using KeyManagement.Domain.Access;
using KeyManagement.Domain.Alarms;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Auditing;
using KeyManagement.Domain.Cabinets;
using KeyManagement.Domain.Custody;
using Microsoft.EntityFrameworkCore;

namespace KeyManagement.Infrastructure.Persistence;

/// <summary>
/// The custody database.
/// </summary>
/// <remarks>
/// There is no <c>DbSet</c> for <see cref="AuditEvent"/> updates by design: audit records are
/// added and read, never amended. <see cref="AppendOnlyAuditInterceptor"/> enforces that even
/// when an entry reaches the change tracker some other way.
/// </remarks>
public sealed class KeyManagementDbContext : DbContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="options">Provider and connection configuration.</param>
    public KeyManagementDbContext(DbContextOptions<KeyManagementDbContext> options)
        : base(options)
    {
    }

    /// <summary>Holders.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>Roles.</summary>
    public DbSet<Role> Roles => Set<Role>();

    /// <summary>Issued refresh tokens.</summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>Assets held in cabinets.</summary>
    public DbSet<Asset> Assets => Set<Asset>();

    /// <summary>Groups that checkout access is granted over.</summary>
    public DbSet<AssetGroup> AssetGroups => Set<AssetGroup>();

    /// <summary>Cabinets.</summary>
    public DbSet<Cabinet> Cabinets => Set<Cabinet>();

    /// <summary>Slots within cabinets.</summary>
    public DbSet<Slot> Slots => Set<Slot>();

    /// <summary>Custody requests and their outcomes.</summary>
    public DbSet<Checkout> Checkouts => Set<Checkout>();

    /// <summary>The append-only audit trail.</summary>
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    /// <summary>Messages received from cabinets, as reported.</summary>
    public DbSet<DeviceEvent> DeviceEvents => Set<DeviceEvent>();

    /// <summary>Things an operator is expected to look at.</summary>
    public DbSet<Alarm> Alarms => Set<Alarm>();

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Properties<UserId>().HaveConversion<UserIdConverter>();
        configurationBuilder.Properties<RoleId>().HaveConversion<RoleIdConverter>();
        configurationBuilder.Properties<AssetId>().HaveConversion<AssetIdConverter>();
        configurationBuilder.Properties<AssetGroupId>().HaveConversion<AssetGroupIdConverter>();
        configurationBuilder.Properties<CabinetId>().HaveConversion<CabinetIdConverter>();
        configurationBuilder.Properties<SlotId>().HaveConversion<SlotIdConverter>();
        configurationBuilder.Properties<CheckoutId>().HaveConversion<CheckoutIdConverter>();
        configurationBuilder.Properties<AuditEventId>().HaveConversion<AuditEventIdConverter>();
        configurationBuilder.Properties<DeviceEventId>().HaveConversion<DeviceEventIdConverter>();
        configurationBuilder.Properties<RefreshTokenId>().HaveConversion<RefreshTokenIdConverter>();
        configurationBuilder.Properties<CorrelationId>().HaveConversion<CorrelationIdConverter>();
        configurationBuilder.Properties<AlarmId>().HaveConversion<AlarmIdConverter>();

        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();

        // Free text that is not a name or a label. Anything shorter is capped where it is
        // configured, so a runaway value fails on the way in rather than on the way out.
        configurationBuilder.Properties<string>().HaveMaxLength(512);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}
