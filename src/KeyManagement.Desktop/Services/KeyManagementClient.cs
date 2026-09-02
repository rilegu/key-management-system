using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KeyManagement.Contracts;

namespace KeyManagement.Desktop.Services;

/// <summary>
/// Talks to the server over HTTPS.
/// </summary>
public sealed class KeyManagementClient : IKeyManagementClient, IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    /// <summary>Creates the client.</summary>
    /// <param name="http">Configured with the server's base address.</param>
    public KeyManagementClient(HttpClient http) => _http = http;

    /// <inheritdoc />
    public SessionResponse? Session { get; private set; }

    /// <inheritdoc />
    public async Task<CommandResult<SessionResponse>> SignInAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
                () => _http.PostAsJsonAsync(
                    "/api/auth/login", new LoginRequest(userName, password), Json, cancellationToken))
            .ConfigureAwait(false);

        // A refusal comes back as 401 with a populated body, so the body is read either way
        // rather than treating the status code as the whole answer.
        var result = await ReadAsync<CommandResult<SessionResponse>>(response, cancellationToken)
            .ConfigureAwait(false);

        if (result.Success && result.Data is { } session)
        {
            Session = session;
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", session.AccessToken);
        }

        return result;
    }

    /// <inheritdoc />
    public void SignOut()
    {
        Session = null;
        _http.DefaultRequestHeaders.Authorization = null;
    }

    /// <inheritdoc />
    public Task<DashboardSummary> GetDashboardAsync(CancellationToken cancellationToken = default) =>
        GetAsync<DashboardSummary>("/api/dashboard", cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssetSummary>> ListItemsAsync(
        CancellationToken cancellationToken = default) =>
        await GetAsync<List<AssetSummary>>("/api/assets", cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CabinetSummary>> ListCabinetsAsync(
        CancellationToken cancellationToken = default) =>
        await GetAsync<List<CabinetSummary>>("/api/cabinets", cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<CabinetSnapshot?> GetCabinetSnapshotAsync(
        Guid cabinetId,
        CancellationToken cancellationToken = default)
    {
        var path = string.Format(
            CultureInfo.InvariantCulture, "/api/cabinets/{0}/snapshot", cabinetId);

        var response = await SendAsync(() => _http.GetAsync(path, cancellationToken))
            .ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadAsync<CabinetSnapshot>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CheckoutSummary>> ListOpenCheckoutsAsync(
        CancellationToken cancellationToken = default) =>
        await GetAsync<List<CheckoutSummary>>("/api/checkouts", cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<CommandResult<CheckoutSummary>> RequestItemAsync(
        Guid itemId,
        DateTimeOffset? curfew,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
                () => _http.PostAsJsonAsync(
                    "/api/checkouts", new CheckoutRequest(itemId, curfew), Json, cancellationToken))
            .ConfigureAwait(false);

        return await ReadAsync<CommandResult<CheckoutSummary>>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CommandResult<CheckoutSummary>> ReturnItemAsync(
        Guid checkoutId,
        CancellationToken cancellationToken = default)
    {
        var path = string.Format(
            CultureInfo.InvariantCulture, "/api/checkouts/{0}/return", checkoutId);

        var response = await SendAsync(
                () => _http.PostAsync(path, content: null, cancellationToken))
            .ConfigureAwait(false);

        return await ReadAsync<CommandResult<CheckoutSummary>>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HolderSummary>> ListHoldersAsync(
        CancellationToken cancellationToken = default) =>
        await GetAsync<List<HolderSummary>>("/api/users", cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RoleSummary>> ListRolesAsync(
        CancellationToken cancellationToken = default) =>
        await GetAsync<List<RoleSummary>>("/api/roles", cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssetGroupSummary>> ListGroupsAsync(
        CancellationToken cancellationToken = default) =>
        await GetAsync<List<AssetGroupSummary>>("/api/asset-groups", cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<CommandResult> CreateHolderAsync(
        CreateHolderRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync("/api/users", request, cancellationToken);

    /// <inheritdoc />
    public async Task<CommandResult> AmendHolderAsync(
        Guid holderId,
        AmendHolderRequest request,
        CancellationToken cancellationToken = default)
    {
        var path = string.Format(CultureInfo.InvariantCulture, "/api/users/{0}", holderId);
        var response = await SendAsync(
                () => _http.PatchAsJsonAsync(path, request, Json, cancellationToken))
            .ConfigureAwait(false);

        return await ReadAsync<CommandResult>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<CommandResult> SetHolderGroupAsync(
        Guid holderId,
        GrantRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            string.Format(CultureInfo.InvariantCulture, "/api/users/{0}/groups", holderId),
            request,
            cancellationToken);

    /// <inheritdoc />
    public Task<CommandResult> CreateGroupAsync(
        CreateGroupRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync("/api/asset-groups", request, cancellationToken);

    /// <inheritdoc />
    public Task<CommandResult> CreateItemAsync(
        CreateItemRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync("/api/assets", request, cancellationToken);

    private async Task<CommandResult> PostAsync<T>(
        string path,
        T body,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
                () => _http.PostAsJsonAsync(path, body, Json, cancellationToken))
            .ConfigureAwait(false);

        return await ReadAsync<CommandResult>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AlarmSummary>> ListAlarmsAsync(
        bool activeOnly = true,
        CancellationToken cancellationToken = default) =>
        await GetAsync<List<AlarmSummary>>(
                $"/api/alarms?activeOnly={(activeOnly ? "true" : "false")}", cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<CommandResult> AcknowledgeAlarmAsync(
        Guid alarmId,
        CancellationToken cancellationToken = default)
    {
        var path = string.Format(
            CultureInfo.InvariantCulture, "/api/alarms/{0}/acknowledge", alarmId);

        var response = await SendAsync(() => _http.PostAsync(path, content: null, cancellationToken))
            .ConfigureAwait(false);

        return await ReadAsync<CommandResult>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> ExportActivityAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var response = await SendAsync(
                () => _http.GetAsync(AuditPath("/api/audit-events/export", query), cancellationToken))
            .ConfigureAwait(false);

        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden)
        {
            throw new KeyManagementClientException("You do not have permission to do that.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEventSummary>> SearchActivityAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await GetAsync<List<AuditEventSummary>>(
                AuditPath("/api/audit-events", query), cancellationToken)
            .ConfigureAwait(false);
    }

    // Shared by the search and the export, so what is exported is always what was on screen.
    private static string AuditPath(string route, AuditQuery query)
    {
        var path = new System.Text.StringBuilder(route)
            .Append("?take=")
            .Append(query.Take.ToString(CultureInfo.InvariantCulture));

        if (query.From is { } from)
        {
            path.Append("&from=").Append(Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            path.Append("&type=").Append(Uri.EscapeDataString(query.Type));
        }

        if (query.AssetId is { } assetId)
        {
            path.Append("&assetId=").Append(assetId.ToString());
        }

        return path.ToString();
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        var response = await SendAsync(() => _http.GetAsync(path, cancellationToken))
            .ConfigureAwait(false);

        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    // A transport failure is the one thing a view model cannot do anything sensible with, so it
    // becomes a single exception type carrying a sentence a person can act on.
    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return await send().ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new KeyManagementClientException(
                "The server could not be reached. Check the connection and try again.", exception);
        }
        catch (TaskCanceledException exception)
        {
            throw new KeyManagementClientException(
                "The server took too long to answer.", exception);
        }
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden)
        {
            throw new KeyManagementClientException(
                "You do not have permission to do that.");
        }

        var payload = await response.Content
            .ReadFromJsonAsync<T>(Json, cancellationToken)
            .ConfigureAwait(false);

        return payload ?? throw new KeyManagementClientException(
            "The server returned an answer this client did not understand.");
    }
}
