using KeyManagement.Domain.Access;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KeyManagement.Infrastructure.Persistence.Configurations;

/// <summary>Maps holders.</summary>
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<User> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Users");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.UserName).HasMaxLength(128).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(256).IsRequired();
        builder.Property(u => u.PinHash).HasMaxLength(256);
        builder.Property(u => u.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.HasIndex(u => u.UserName).IsUnique();

        // EF finds the _roles and _groupMemberships backing fields by convention, which is
        // what lets these navigations stay read-only on the entity.
        builder.HasMany(u => u.Roles).WithMany().UsingEntity("UserRoles");

        builder.HasMany(u => u.GroupMemberships)
            .WithOne()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Maps roles.</summary>
public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Roles");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Name).HasMaxLength(128).IsRequired();

        // Stored as an integer rather than a string: it is a flags set, and a comma-joined
        // string of flag names cannot be filtered on without parsing it back.
        builder.Property(r => r.Permissions).HasConversion<int>().IsRequired();

        builder.HasIndex(r => r.Name).IsUnique();
    }
}

/// <summary>Maps the grant of an asset group to a holder.</summary>
public sealed class AssetGroupMembershipConfiguration : IEntityTypeConfiguration<AssetGroupMembership>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AssetGroupMembership> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AssetGroupMemberships");

        // The pair is the identity: a holder is granted a group once or not at all.
        builder.HasKey(m => new { m.UserId, m.AssetGroupId });

        builder.HasIndex(m => m.AssetGroupId);
    }
}

/// <summary>Maps issued refresh tokens.</summary>
public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RefreshTokens");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash).HasMaxLength(256).IsRequired();

        // Presentation looks a token up by its hash, so this index is the hot path for every
        // refresh. Unique because two tokens hashing the same is a collision worth failing on.
        builder.HasIndex(t => t.TokenHash).IsUnique();

        // Revoking every token for a holder is one query behind this.
        builder.HasIndex(t => t.UserId);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
