using KeyManagement.Domain.Access;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Cabinets;
using KeyManagement.Domain.Custody;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KeyManagement.Infrastructure.Persistence.Configurations;

/// <summary>Maps custody requests.</summary>
public sealed class CheckoutConfiguration : IEntityTypeConfiguration<Checkout>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Checkout> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Checkouts");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(c => c.DenialReason).HasMaxLength(256);

        // "What is out right now" is the dashboard's main query and the overdue sweep's
        // starting point. Filtered so the index holds only live rows, which stays small while
        // the table grows without bound.
        builder.HasIndex(c => c.State)
            .HasFilter("[State] IN ('Pending', 'Active', 'Overdue')");

        // The overdue sweep reads this directly.
        builder.HasIndex(c => c.DueAt);

        // An asset's custody history, newest first.
        builder.HasIndex(c => new { c.AssetId, c.RequestedAt });

        // What one holder has out.
        builder.HasIndex(c => c.UserId);

        // Matches a late device result back to the request that caused it.
        builder.HasIndex(c => c.CorrelationId);

        builder.HasOne<Asset>().WithMany().HasForeignKey(c => c.AssetId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Cabinet>().WithMany().HasForeignKey(c => c.CabinetId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Slot>().WithMany().HasForeignKey(c => c.SlotId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(c => c.IsSettled);
    }
}
