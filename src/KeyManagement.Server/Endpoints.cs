using System.Security.Claims;
using KeyManagement.Application.Abstractions;
using KeyManagement.Application.Authentication;
using KeyManagement.Application.Custody;
using KeyManagement.Contracts;
using KeyManagement.Domain;
using Microsoft.AspNetCore.Mvc;

namespace KeyManagement.Server;

/// <summary>
/// The HTTP surface.
/// </summary>
/// <remarks>
/// Endpoints do three things and no more: read the request, call a use case, shape the
/// response. Every authorization decision belongs to the use case, so that a future caller
/// that is not this API is judged by the same rules.
/// </remarks>
public static class Endpoints
{
    /// <summary>Maps every endpoint.</summary>
    /// <param name="app">The application.</param>
    /// <returns>The application, for chaining.</returns>
    public static WebApplication MapKeyManagementEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapAuthentication(app);
        MapCustody(app);
        MapReads(app);

        return app;
    }

    private static void MapAuthentication(WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").AllowAnonymous().WithTags("Authentication");

        auth.MapPost("/login", async (
                LoginRequest request,
                SignInService signIn,
                CancellationToken cancellationToken) =>
            {
                var result = await signIn.SignInAsync(
                    request, CorrelationId.New(), cancellationToken);

                // A refusal returns 401 because the caller is not authenticated, which is a
                // transport-level fact. Custody refusals differ: those are outcomes, and they
                // return 200 with success false.
                return result.Success
                    ? Results.Ok(result)
                    : Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
            })
            .WithName("SignIn")
            .WithSummary("Exchange credentials for a session.");

        auth.MapPost("/refresh", async (
                RefreshRequest request,
                SignInService signIn,
                CancellationToken cancellationToken) =>
            {
                var result = await signIn.RefreshAsync(
                    request, CorrelationId.New(), cancellationToken);

                return result.Success
                    ? Results.Ok(result)
                    : Results.Json(result, statusCode: StatusCodes.Status401Unauthorized);
            })
            .WithName("RefreshSession")
            .WithSummary("Exchange a refresh token for a new session.");
    }

    private static void MapCustody(WebApplication app)
    {
        var custody = app.MapGroup("/api/checkouts")
            .RequireAuthorization(Authorization.CanCheckOut)
            .WithTags("Custody");

        custody.MapPost("/", async (
                CheckoutRequest request,
                ClaimsPrincipal caller,
                CheckoutService checkouts,
                CancellationToken cancellationToken) =>
            {
                var result = await checkouts.RequestAsync(
                    request, caller.RequireUserId(), CorrelationId.New(), cancellationToken);

                // Always 200. A refusal is an outcome the system exists to produce, and the
                // client reads success and message rather than guessing from a status code.
                return Results.Ok(result);
            })
            .WithName("RequestCheckout")
            .WithSummary("Request custody of an asset.");

        custody.MapPost("/{id:guid}/return", async (
                Guid id,
                ClaimsPrincipal caller,
                CheckoutService checkouts,
                CancellationToken cancellationToken) =>
            {
                var result = await checkouts.ReturnAsync(
                    new CheckoutId(id), caller.RequireUserId(), CorrelationId.New(), cancellationToken);

                return Results.Ok(result);
            })
            .WithName("ReturnAsset")
            .WithSummary("Start returning an asset that is out.");

        custody.MapGet("/", async (ICustodyQueries queries, CancellationToken cancellationToken) =>
                Results.Ok(await queries.ListOpenCheckoutsAsync(cancellationToken)))
            .WithName("ListOpenCheckouts")
            .WithSummary("List what is currently out.");
    }

    private static void MapReads(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Custody");

        api.MapGet("/dashboard", async (
                ICustodyQueries queries,
                CancellationToken cancellationToken) =>
                Results.Ok(await queries.GetDashboardAsync(cancellationToken)))
            .WithName("GetDashboard")
            .WithSummary("Cabinet health, open checkouts, uncertain assets and recent events.");

        api.MapGet("/assets", async (
                Guid? assetGroupId,
                ICustodyQueries queries,
                CancellationToken cancellationToken) =>
                Results.Ok(await queries.ListAssetsAsync(
                    assetGroupId is { } group ? new AssetGroupId(group) : null,
                    cancellationToken)))
            .WithName("ListAssets")
            .WithSummary("List assets and where they are.");

        api.MapGet("/cabinets", async (
                ICustodyQueries queries,
                CancellationToken cancellationToken) =>
                Results.Ok(await queries.ListCabinetsAsync(cancellationToken)))
            .WithName("ListCabinets")
            .WithSummary("List cabinets and their link status.");

        api.MapGet("/cabinets/{id:guid}/snapshot", async (
                Guid id,
                ICustodyQueries queries,
                CancellationToken cancellationToken) =>
            {
                var snapshot = await queries.GetCabinetSnapshotAsync(
                    new CabinetId(id), cancellationToken);

                return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
            })
            .WithName("GetCabinetSnapshot")
            .WithSummary("Read one cabinet slot by slot.");

        api.MapGet("/audit-events", async (
                [AsParameters] AuditQueryParameters parameters,
                ICustodyQueries queries,
                CancellationToken cancellationToken) =>
                Results.Ok(await queries.SearchAuditAsync(parameters.ToQuery(), cancellationToken)))
            .RequireAuthorization(Authorization.CanViewAudit)
            .WithName("SearchAuditEvents")
            .WithSummary("Search the audit trail, newest first.");
    }
}

/// <summary>
/// Audit search filters, read from the query string.
/// </summary>
/// <param name="From">Earliest moment to include, UTC.</param>
/// <param name="To">Latest moment to include, UTC.</param>
/// <param name="UserId">Only records about this holder.</param>
/// <param name="AssetId">Only records about this asset.</param>
/// <param name="Type">Only records of this kind.</param>
/// <param name="Take">How many to return.</param>
public sealed record AuditQueryParameters(
    [FromQuery] DateTimeOffset? From,
    [FromQuery] DateTimeOffset? To,
    [FromQuery] Guid? UserId,
    [FromQuery] Guid? AssetId,
    [FromQuery] string? Type,
    [FromQuery] int? Take)
{
    /// <summary>Converts to the application's query shape.</summary>
    /// <returns>The query.</returns>
    public AuditQuery ToQuery() => new(From, To, UserId, AssetId, Type, Take ?? 100);
}
