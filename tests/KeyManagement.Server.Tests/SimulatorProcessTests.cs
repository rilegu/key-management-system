using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using KeyManagement.Contracts;
using KeyManagement.Infrastructure.Security;

namespace KeyManagement.Server.Tests;

/// <summary>
/// The simulator, run as the program it is.
/// </summary>
/// <remarks>
/// Started for real, driven through its standard input, and killed outright. Everything the
/// device link is supposed to survive — a process disappearing mid-session, positions moving
/// while nobody is watching, a reconnect that has to replay — happens here because it actually
/// happens, not because a test arranged for a fake to pretend.
/// </remarks>
public sealed class SimulatorProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _configPath;

    private SimulatorProcess(Process process, string configPath)
    {
        _process = process;
        _configPath = configPath;
    }

    /// <summary>Whether the process is still alive.</summary>
    public bool IsRunning => !_process.HasExited;

    /// <summary>Writes a configuration and starts the simulator against a gateway.</summary>
    /// <param name="api">The running server.</param>
    /// <param name="certificateDirectory">Where the cabinet's certificate was written.</param>
    /// <returns>The running simulator.</returns>
    public static async Task<SimulatorProcess> StartAsync(
        CabinetGatewayApi api,
        string certificateDirectory)
    {
        ArgumentNullException.ThrowIfNull(api);

        var configPath = Path.Combine(
            Path.GetTempPath(), $"kms-simulator-{Guid.CreateVersion7():N}.json");

        var config = new
        {
            host = "127.0.0.1",
            port = api.GatewayPort,
            serverName = "localhost",
            certificatePassword = CabinetGatewayApi.CertificatePassword,
            authorityPath = Path.Combine(certificateDirectory, "device-authority.pfx"),
            cabinets = new[]
            {
                new
                {
                    name = "Reception",
                    certificatePath = Path.Combine(certificateDirectory, "cabinet-reception.pfx"),
                    firmwareVersion = "1.4.2",
                    positions = new[]
                    {
                        new { position = "A01", occupied = true },
                        new { position = "A02", occupied = true },
                        new { position = "A03", occupied = true },
                    },
                },
            },
        };

        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config));

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = SolutionRoot(),
        };

        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(Path.Combine("src", "KeyManagement.DeviceSimulator"));
        start.ArgumentList.Add("--");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(configPath);

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the simulator.");

        return new SimulatorProcess(process, configPath);
    }

    /// <summary>Types a command at the simulator.</summary>
    /// <param name="command">What to type.</param>
    /// <returns>A task that completes once it is sent.</returns>
    public async Task SendAsync(string command)
    {
        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync();
    }

    /// <summary>Kills the process outright, the way a power cut would.</summary>
    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Kill();
        _process.Dispose();

        try
        {
            File.Delete(_configPath);
        }
        catch (IOException)
        {
            // In the temp directory; cleanup must not fail a passing test.
        }

        await ValueTask.CompletedTask;
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KeyManagement.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the solution root.");
    }
}

/// <summary>
/// A real cabinet program, disconnected and reconnected.
/// </summary>
public sealed class SimulatorProcessTests : IClassFixture<CabinetGatewayApi>
{
    private readonly CabinetGatewayApi _api;

    /// <summary>Creates the tests.</summary>
    /// <param name="api">The running server and its listener.</param>
    public SimulatorProcessTests(CabinetGatewayApi api) => _api = api;

    [Fact]
    public async Task A_real_cabinet_attaches_takes_an_item_and_reconciles_after_a_restart()
    {
        var client = await _api.SignInAsync();

        await using var simulator = await SimulatorProcess.StartAsync(_api, _api.CertificateDirectory);
        await WaitForAsync(async () => await CabinetStatusAsync(client) == "Online");

        var items = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
        var target = items!.Single(i => i.SlotPosition == "A01");

        var requested = await client.PostAsJsonAsync(
            "/api/checkouts", new CheckoutRequest(target.Id, null));
        var checkout = await requested.Content.ReadFromJsonAsync<CommandResult<CheckoutSummary>>();
        Assert.True(checkout!.Success);

        await simulator.SendAsync("take A01");
        await WaitForStateAsync(client, target.Id, "CheckedOut");

        // The power goes off. No goodbye, no flush, no chance to say anything.
        simulator.Kill();
        await WaitForAsync(async () => await CabinetStatusAsync(client) == "Offline");

        // The key comes back while nobody is watching. A cabinet buffers this and the server
        // learns about it only when the link returns.
        await using var restarted = await SimulatorProcess.StartAsync(_api, _api.CertificateDirectory);
        await WaitForAsync(async () => await CabinetStatusAsync(client) == "Online");

        await restarted.SendAsync("put A01");
        await WaitForStateAsync(client, target.Id, "Available");

        var trail = await client.GetFromJsonAsync<List<AuditEventSummary>>(
            $"/api/audit-events?assetId={target.Id}&take=50");

        Assert.Contains(trail!, e => e.Type == "CheckoutCompleted");
        Assert.Contains(trail!, e => e.Type == "ReturnCompleted");
    }

    [Fact]
    public async Task Events_that_happen_while_the_link_is_down_are_replayed_when_it_returns()
    {
        var client = await _api.SignInAsync();

        await using var simulator = await SimulatorProcess.StartAsync(_api, _api.CertificateDirectory);
        await WaitForAsync(async () => await CabinetStatusAsync(client) == "Online");

        var items = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
        var target = items!.Single(i => i.SlotPosition == "A03");

        // The link goes, the position changes, the link comes back. Without buffering and
        // replay this change would simply never have happened as far as the server knows.
        await simulator.SendAsync("drop");
        await WaitForAsync(async () => await CabinetStatusAsync(client) == "Offline");

        await simulator.SendAsync("take A03");
        await Task.Delay(500);

        await simulator.SendAsync("attach");
        await WaitForAsync(async () => await CabinetStatusAsync(client) == "Online");

        // Nothing authorized this removal, so the replayed event still produces an alarm rather
        // than a checkout. Being late does not make it legitimate.
        await WaitForStateAsync(client, target.Id, "Unknown");

        var trail = await client.GetFromJsonAsync<List<AuditEventSummary>>(
            $"/api/audit-events?assetId={target.Id}&take=50");
        Assert.Contains(trail!, e => e.Type == "UnauthorizedSlotChange");
    }

    private static async Task<string> CabinetStatusAsync(HttpClient client)
    {
        var cabinets = await client.GetFromJsonAsync<List<CabinetSummary>>("/api/cabinets");
        return cabinets!.Single().Status;
    }

    private static async Task WaitForStateAsync(HttpClient client, Guid itemId, string state) =>
        await WaitForAsync(async () =>
        {
            var items = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
            return items!.Single(i => i.Id == itemId).CustodyState == state;
        });

    // Generous, because starting a process is slower than anything else here and a flaky
    // timeout is worse than a slow test.
    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        while (!await condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(100, timeout.Token);
        }
    }
}
