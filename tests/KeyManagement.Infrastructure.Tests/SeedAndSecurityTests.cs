using KeyManagement.Application.Abstractions;
using KeyManagement.Domain.Access;
using KeyManagement.Infrastructure.Persistence;
using KeyManagement.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace KeyManagement.Infrastructure.Tests;

/// <summary>
/// Seeding an empty database, and the password hasher behind the administrator it creates.
/// </summary>
public sealed class SeedAndSecurityTests
{
    private const string AdministratorPassword = "correct horse battery staple";

    [Fact]
    public async Task Seeding_an_empty_database_produces_a_usable_starting_state()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await using (var scope = database.CreateScope())
        {
            var seeded = await TemporaryDatabase.Resolve<DatabaseSeeder>(scope)
                .SeedAsync(AdministratorPassword);
            Assert.True(seeded);
        }

        await database.WithContextAsync(async context =>
        {
            var admin = await context.Users
                .Include(u => u.Roles)
                .Include(u => u.GroupMemberships)
                .SingleAsync(u => u.UserName == "admin");

            Assert.Equal(UserStatus.Active, admin.Status);
            Assert.True(admin.Can(Permissions.ManageUsers));
            Assert.True(admin.Can(Permissions.ViewAudit));
            Assert.NotEmpty(admin.GroupMemberships);

            Assert.Equal(4, await context.Roles.CountAsync());
            Assert.Equal(5, await context.Assets.CountAsync());
            Assert.Equal(10, await context.Slots.CountAsync());

            // Five of the ten slots have a home asset; the rest are empty, as in a real cabinet.
            Assert.Equal(5, await context.Slots.CountAsync(s => s.AssetId != null));
        });
    }

    [Fact]
    public async Task Seeding_twice_changes_nothing()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await using (var first = database.CreateScope())
        {
            Assert.True(await TemporaryDatabase.Resolve<DatabaseSeeder>(first)
                .SeedAsync(AdministratorPassword));
        }

        await using (var second = database.CreateScope())
        {
            Assert.False(await TemporaryDatabase.Resolve<DatabaseSeeder>(second)
                .SeedAsync(AdministratorPassword));
        }

        await database.WithContextAsync(async context =>
            Assert.Equal(1, await context.Users.CountAsync()));
    }

    [Fact]
    public async Task The_seeded_password_verifies_and_is_not_stored_in_the_clear()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await using (var scope = database.CreateScope())
        {
            await TemporaryDatabase.Resolve<DatabaseSeeder>(scope).SeedAsync(AdministratorPassword);
        }

        await database.WithContextAsync(async context =>
        {
            var admin = await context.Users.SingleAsync(u => u.UserName == "admin");
            var hasher = new IdentityPasswordHasher();

            Assert.DoesNotContain(AdministratorPassword, admin.PasswordHash, StringComparison.Ordinal);
            Assert.Equal(
                PasswordVerification.Succeeded,
                hasher.Verify(admin.PasswordHash, AdministratorPassword));
            Assert.Equal(
                PasswordVerification.Failed,
                hasher.Verify(admin.PasswordHash, "not the password"));
        });
    }

    [Fact]
    public void The_same_password_hashes_differently_every_time()
    {
        // A random salt per hash. Without it, two holders choosing the same password would be
        // visibly identical in the database.
        var hasher = new IdentityPasswordHasher();

        var first = hasher.Hash(AdministratorPassword);
        var second = hasher.Hash(AdministratorPassword);

        Assert.NotEqual(first, second);
        Assert.Equal(PasswordVerification.Succeeded, hasher.Verify(first, AdministratorPassword));
        Assert.Equal(PasswordVerification.Succeeded, hasher.Verify(second, AdministratorPassword));
    }
}
