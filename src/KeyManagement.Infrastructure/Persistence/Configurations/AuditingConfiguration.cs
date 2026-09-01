using KeyManagement.Domain.Auditing;
using KeyManagement.Domain.Cabinets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KeyManagement.Infrastructure.Persistence.Configurations;

/// <summary>Maps the audit trail.</summary>
public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AuditEvents");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(e => e.Summary).HasMaxLength(512).IsRequired();

        // Reading the trail is almost always "what happened, most recent first".
        builder.HasIndex(e => e.OccurredAt);

        // Reassembles one command's whole story: request, decision, device command, result.
        builder.HasIndex(e => e.CorrelationId);

        builder.HasIndex(e => e.Type);
        builder.HasIndex(e => e.UserId);
        builder.HasIndex(e => e.AssetId);

        // The subject columns carry no foreign keys, unlike everywhere else in this schema.
        // An audit trail has to outlive what it describes: a record naming a holder must not
        // become unwritable, or deletable, because of what later happens to that holder's row.
    }
}

/// <summary>Maps messages received from cabinets.</summary>
public sealed class DeviceEventConfiguration : IEntityTypeConfiguration<DeviceEvent>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DeviceEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DeviceEvents");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Payload).HasMaxLength(4096).IsRequired();

        // A cabinet never issues the same sequence number twice. Unique here means a
        // duplicate delivery cannot be recorded twice even if the discard logic is wrong,
        // which makes this the backstop behind the sequence check rather than a repeat of it.
        builder.HasIndex(e => new { e.CabinetId, e.Sequence }).IsUnique();

        builder.HasIndex(e => e.ReceivedAt);

        builder.HasOne<Cabinet>()
            .WithMany()
            .HasForeignKey(e => e.CabinetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
