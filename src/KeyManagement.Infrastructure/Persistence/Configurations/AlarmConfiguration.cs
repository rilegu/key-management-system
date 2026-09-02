using KeyManagement.Domain.Alarms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KeyManagement.Infrastructure.Persistence.Configurations;

/// <summary>Maps alarms.</summary>
public sealed class AlarmConfiguration : IEntityTypeConfiguration<Alarm>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Alarm> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Alarms");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Type).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(a => a.Severity).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(a => a.Scope).HasMaxLength(128).IsRequired();
        builder.Property(a => a.Summary).HasMaxLength(512).IsRequired();

        // One problem, one alarm. A sweep that notices the same overdue item every minute must
        // not add a row every minute, and this refuses it at the database rather than trusting
        // the check that comes before it.
        builder.HasIndex(a => a.Scope)
            .IsUnique()
            .HasFilter("[Status] = 'Active'");

        // The list an operator actually opens: what is still active, newest first.
        builder.HasIndex(a => new { a.Status, a.RaisedAt });

        builder.HasIndex(a => a.AssetId);
        builder.HasIndex(a => a.CabinetId);

        // As with the audit trail, the subject columns carry no foreign keys: an alarm has to
        // outlive whatever it describes.
    }
}
