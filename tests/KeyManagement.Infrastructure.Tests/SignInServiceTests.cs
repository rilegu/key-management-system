using KeyManagement.Application.Authentication;
using KeyManagement.Contracts;
using KeyManagement.Domain;
using KeyManagement.Domain.Access;
using KeyManagement.Domain.Auditing;
using KeyManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KeyManagement.Infrastructure.Tests;

/// <summary>
/// Sign-in and session renewal over real persistence.
/// </summary>
public sealed class SignInServiceTests
{
    private const string Password = "correct horse battery staple";
    private const string AdministratorPin = "4821";

    private static async Task<TemporaryDatabase> ArrangeAsync(
        UserStatus status = UserStatus.Active)
    {
        var database = await TemporaryDatabase.CreateAsync();

        await using (var scope = database.CreateScope())
        {
            await TemporaryDatabase.Resolve<DatabaseSeeder>(scope).SeedAsync(Password, AdministratorPin);
        }

        if (status != UserStatus.Active)
        {
            await database.WithContextAsync(async context =>
            {
                var user = await context.Users.SingleAsync(u => u.UserName == "admin");
                user.SetStatus(status);
                await context.SaveChangesAsync();
            });
        }

        return database;
    }

    private static async Task<CommandResult<SessionResponse>> SignInAsync(
        TemporaryDatabase database,
        string userName,
        string password)
    {
        await using var scope = database.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SignInService>()
            .SignInAsync(new LoginRequest(userName, password), CorrelationId.New());
    }

    [Fact]
    public async Task Correct_credentials_produce_a_session_and_an_audit_record()
    {
        await using var database = await ArrangeAsync();

        var result = await SignInAsync(database, "admin", Password);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotEmpty(result.Data.AccessToken);
        Assert.NotEmpty(result.Data.RefreshToken);
        Assert.Contains(nameof(Permissions.ManageUsers), result.Data.Permissions);

        await database.WithContextAsync(async context =>
            Assert.True(await context.AuditEvents
                .AnyAsync(e => e.Type == AuditEventType.SignInSucceeded)));
    }

    [Theory]
    [InlineData("admin", "the wrong password")]
    [InlineData("nobody", "the wrong password")]
    public async Task An_unknown_account_and_a_wrong_password_are_indistinguishable(
        string userName,
        string password)
    {
        // Same message either way. Telling a caller that the account exists but the password
        // is wrong hands them half the credential for free.
        await using var database = await ArrangeAsync();

        var result = await SignInAsync(database, userName, password);

        Assert.False(result.Success);
        Assert.Equal("The user name or password is not correct.", result.Message);
    }

    [Fact]
    public async Task A_refused_sign_in_is_recorded()
    {
        await using var database = await ArrangeAsync();

        await SignInAsync(database, "admin", "the wrong password");

        await database.WithContextAsync(async context =>
        {
            var refusal = await context.AuditEvents
                .SingleAsync(e => e.Type == AuditEventType.SignInFailed);
            Assert.Contains("admin", refusal.Summary, StringComparison.Ordinal);

            // The attempted password must not appear anywhere in the trail.
            Assert.DoesNotContain("wrong password", refusal.Summary, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_suspended_holder_cannot_sign_in_even_with_the_right_password()
    {
        await using var database = await ArrangeAsync(UserStatus.Suspended);

        var result = await SignInAsync(database, "admin", Password);

        Assert.False(result.Success);
        Assert.Contains("not active", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refresh_token_is_good_for_one_use()
    {
        // Single use is what makes a stolen token stop working: the rightful holder's next
        // refresh revokes it, and the thief's replay fails.
        await using var database = await ArrangeAsync();

        var session = await SignInAsync(database, "admin", Password);
        var refreshToken = session.Data!.RefreshToken;

        CommandResult<SessionResponse> first;
        await using (var scope = database.CreateScope())
        {
            first = await scope.ServiceProvider.GetRequiredService<SignInService>()
                .RefreshAsync(new RefreshRequest(refreshToken), CorrelationId.New());
        }

        Assert.True(first.Success);
        Assert.NotEqual(refreshToken, first.Data!.RefreshToken);

        CommandResult<SessionResponse> replay;
        await using (var scope = database.CreateScope())
        {
            replay = await scope.ServiceProvider.GetRequiredService<SignInService>()
                .RefreshAsync(new RefreshRequest(refreshToken), CorrelationId.New());
        }

        Assert.False(replay.Success);
    }

    [Fact]
    public async Task An_unknown_refresh_token_is_refused()
    {
        await using var database = await ArrangeAsync();

        await using var scope = database.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<SignInService>()
            .RefreshAsync(new RefreshRequest("not a token this server issued"), CorrelationId.New());

        Assert.False(result.Success);
    }
}
