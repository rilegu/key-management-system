using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KeyManagement.Desktop.Services;
using KeyManagement.Desktop.ViewModels;

namespace KeyManagement.Desktop.Tests;

/// <summary>Answers every request with a canned response.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;
    private readonly string _contentType;

    public StubHandler(HttpStatusCode status, string body, string contentType = "application/json") =>
        (_status, _body, _contentType) = (status, body, contentType);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, _contentType),
        });
}

/// <summary>
/// What the client does with a response that is not the happy path.
/// </summary>
/// <remarks>
/// A server fault used to reach the JSON reader, which threw an exception no screen caught and
/// the process died on the spot. These pin the behaviour that replaced it.
/// </remarks>
public sealed class KeyManagementClientErrorTests
{
    private static KeyManagementClient ClientFor(HttpStatusCode status, string body, string contentType = "application/json") =>
        new(new HttpClient(new StubHandler(status, body, contentType))
        {
            BaseAddress = new Uri("http://localhost"),
        });

    [Fact]
    public async Task A_server_fault_is_reported_rather_than_parsed()
    {
        // Exactly what a 500 returns in development: an HTML page. Reading it as JSON is what
        // took the client down when the administration screen opened.
        using var client = ClientFor(
            HttpStatusCode.InternalServerError,
            "<html><body>System.InvalidOperationException: ...</body></html>",
            "text/html");

        var exception = await Assert.ThrowsAsync<KeyManagementClientException>(
            () => client.ListHoldersAsync());

        Assert.Contains("500", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_forbidden_response_still_says_so_in_words()
    {
        using var client = ClientFor(HttpStatusCode.Forbidden, string.Empty, "text/plain");

        var exception = await Assert.ThrowsAsync<KeyManagementClientException>(
            () => client.ListHoldersAsync());

        Assert.Contains("permission", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_success_carrying_something_other_than_json_is_reported_not_thrown_raw()
    {
        using var client = ClientFor(HttpStatusCode.OK, "not json at all", "text/plain");

        await Assert.ThrowsAsync<KeyManagementClientException>(() => client.ListHoldersAsync());
    }

    [Fact]
    public async Task A_refused_sign_in_is_still_read_from_the_body()
    {
        // Sign-in refusal is a 401 carrying the envelope. The status check must not swallow it,
        // or a wrong password would report a transport problem instead of a wrong password.
        using var client = ClientFor(
            HttpStatusCode.Unauthorized,
            """{"success":false,"message":"That sign-in was not accepted.","correlationId":"01a06246-6a63-771e-b828-b7e1c14ac29f","state":null,"data":null}""");

        var result = await client.SignInAsync("admin", "wrong");

        Assert.False(result.Success);
        Assert.Equal("That sign-in was not accepted.", result.Message);
    }
}

/// <summary>A screen whose load throws, standing in for a defect in any real one.</summary>
public sealed class ThrowingViewModel : ViewModelBase
{
    /// <summary>Runs work that fails.</summary>
    public Task BoomAsync() => RunAsync(_ => throw new InvalidOperationException("boom"));
}

/// <summary>
/// The screen base class must absorb a defect rather than let it end the process.
/// </summary>
public sealed class ViewModelFailureTests
{
    [Fact]
    public async Task An_unexpected_failure_becomes_a_message_instead_of_a_crash()
    {
        var screen = new ThrowingViewModel();

        await screen.BoomAsync();

        Assert.False(screen.IsBusy);
        Assert.False(string.IsNullOrWhiteSpace(screen.ErrorMessage));
    }
}
