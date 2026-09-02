using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Cabinets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KeyManagement.Infrastructure.Persistence.Configurations;

/// <summary>Maps cabinets.</summary>
public sealed class CabinetConfiguration : IEntityTypeConfiguration<Cabinet>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Cabinet> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Cabinets");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).HasMaxLength(128).IsRequired();
        builder.Property(c => c.Site).HasMaxLength(128).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(c => c.FirmwareVersion).HasMaxLength(64);
        builder.Property(c => c.CredentialHash).HasMaxLength(256);
        builder.Property(c => c.LastAppliedSequence).IsRequired();

        builder.HasIndex(c => c.Name).IsUnique();

        builder.HasMany(c => c.Slots)
            .WithOne()
            .HasForeignKey(s => s.CabinetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps slots.</summary>
public sealed class SlotConfiguration : IEntityTypeConfiguration<Slot>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Slot> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Slots");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Position).HasMaxLength(32).IsRequired();
        builder.Property(s => s.State).HasConversion<string>().HasMaxLength(32).IsRequired();

        // A cabinet cannot have two slots in the same position.
        builder.HasIndex(s => new { s.CabinetId, s.Position }).IsUnique();

        // An asset lives in at most one slot. Filtered, because many slots are unassigned and
        // SQLite would otherwise treat every NULL as a duplicate of the others.
        builder.HasIndex(s => s.AssetId)
            .IsUnique()
            .HasFilter("[AssetId] IS NOT NULL");

        builder.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(s => s.AssetId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
