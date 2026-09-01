using KeyManagement.Domain.Assets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KeyManagement.Infrastructure.Persistence.Configurations;

/// <summary>Maps assets.</summary>
public sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Assets");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Reference).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Description).HasMaxLength(256).IsRequired();

        // Stored as a name rather than a number. Someone will eventually read this table
        // directly during an incident, and "CheckedOut" needs no lookup table.
        builder.Property(a => a.CustodyState).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.HasIndex(a => a.Reference).IsUnique();

        // The dashboard and every authorization check filter by group.
        builder.HasIndex(a => a.AssetGroupId);

        // Finds everything currently out, and everything whose whereabouts are uncertain.
        builder.HasIndex(a => a.CustodyState);

        builder.HasOne<AssetGroup>()
            .WithMany()
            .HasForeignKey(a => a.AssetGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(a => a.IsUncertain);
    }
}

/// <summary>Maps asset groups.</summary>
public sealed class AssetGroupConfiguration : IEntityTypeConfiguration<AssetGroup>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AssetGroup> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AssetGroups");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.Name).HasMaxLength(128).IsRequired();
        builder.Property(g => g.Description).HasMaxLength(512);

        builder.HasIndex(g => g.Name).IsUnique();
    }
}
