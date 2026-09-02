using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
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
    private readonly StringBuilder _output = new();

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

        var executable = SimulatorExecutable();

        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };

        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(configPath);

        var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start '{executable}'.");

        var output = new SimulatorProcess(process, configPath);

        // Drained on a background task. A process whose output buffer fills stops writing and
        // then stops doing anything at all, which would look exactly like a hung simulator.
        _ = output.DrainAsync(process.StandardOutput);
        _ = output.DrainAsync(process.StandardError);

        return output;
    }

    /// <summary>Types a command at the simulator.</summary>
    /// <param name="command">What to type.</param>
    /// <returns>A task that completes once it is sent.</returns>
    public async Task SendAsync(string command)
    {
        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync();
    }

    /// <summary>Everything the simulator has printed, for when a test needs to explain itself.</summary>
    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
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

    private async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (_output)
            {
                _output.AppendLine(line);
            }
        }
    }

    /// <summary>
    /// The simulator as it was actually built, beside this test assembly's own output.
    /// </summary>
    /// <remarks>
    /// Not <c>dotnet run</c>. That defaults to a Debug build, so a Release test run — which is
    /// what continuous integration does — would look for output that was never produced and
    /// spend its timeout waiting for a process that had already given up. Taking the
    /// configuration and framework from this assembly's own path keeps the two in step.
    /// </remarks>
    private static string SimulatorExecutable()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        var framework = here.Name;
        var configuration = here.Parent?.Name
            ?? throw new InvalidOperationException("Unexpected test output layout.");

        var root = here;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KeyManagement.slnx")))
        {
            root = root.Parent;
        }

        if (root is null)
        {
            throw new InvalidOperationException("Could not find the solution root.");
        }

        var name = OperatingSystem.IsWindows()
            ? "KeyManagement.DeviceSimulator.exe"
            : "KeyManagement.DeviceSimulator";

        var path = Path.Combine(
            root.FullName, "src", "KeyManagement.DeviceSimulator", "bin", configuration, framework, name);

        // Fails immediately and says why, rather than timing out on a process that never ran.
        return File.Exists(path)
            ? path
            : throw new InvalidOperationException($"The simulator was not built at '{path}'.");
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
        await WaitForAsync(
            async () => await CabinetStatusAsync(client) == "Online", "the cabinet to attach", simulator);

        var items = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
        var target = items!.Single(i => i.SlotPosition == "A01");

        var requested = await client.PostAsJsonAsync(
            "/api/checkouts", new CheckoutRequest(target.Id, null));
        var checkout = await requested.Content.ReadFromJsonAsync<CommandResult<CheckoutSummary>>();
        Assert.True(checkout!.Success);

        await simulator.SendAsync("take A01");
        await WaitForStateAsync(client, target.Id, "CheckedOut", simulator);

        // The power goes off. No goodbye, no flush, no chance to say anything.
        simulator.Kill();
        await WaitForAsync(
            async () => await CabinetStatusAsync(client) == "Offline", "the cabinet to go offline", simulator);

        // The key comes back while nobody is watching. A cabinet buffers this and the server
        // learns about it only when the link returns.
        await using var restarted = await SimulatorProcess.StartAsync(_api, _api.CertificateDirectory);
        await WaitForAsync(
            async () => await CabinetStatusAsync(client) == "Online", "the cabinet to reattach", restarted);

        await restarted.SendAsync("put A01");
        await WaitForStateAsync(client, target.Id, "Available", restarted);

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
        await WaitForAsync(
            async () => await CabinetStatusAsync(client) == "Online", "the cabinet to attach", simulator);

        var items = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
        var target = items!.Single(i => i.SlotPosition == "A03");

        // The link goes, the position changes, the link comes back. Without buffering and
        // replay this change would simply never have happened as far as the server knows.
        await simulator.SendAsync("drop");
        await WaitForAsync(
            async () => await CabinetStatusAsync(client) == "Offline", "the link to drop", simulator);

        await simulator.SendAsync("take A03");
        await Task.Delay(500);

        await simulator.SendAsync("attach");
        await WaitForAsync(
            async () => await CabinetStatusAsync(client) == "Online", "the link to return", simulator);

        // Nothing authorized this removal, so the replayed event still produces an alarm rather
        // than a checkout. Being late does not make it legitimate.
        await WaitForStateAsync(client, target.Id, "Unknown", simulator);

        var trail = await client.GetFromJsonAsync<List<AuditEventSummary>>(
            $"/api/audit-events?assetId={target.Id}&take=50");
        Assert.Contains(trail!, e => e.Type == "UnauthorizedSlotChange");
    }

    private static async Task<string> CabinetStatusAsync(HttpClient client)
    {
        var cabinets = await client.GetFromJsonAsync<List<CabinetSummary>>("/api/cabinets");
        return cabinets!.Single().Status;
    }

    private static Task WaitForStateAsync(
        HttpClient client, Guid itemId, string state, SimulatorProcess simulator) =>
        WaitForAsync(
            async () =>
            {
                var items = await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
                return items!.Single(i => i.Id == itemId).CustodyState == state;
            },
            $"the item to reach {state}",
            simulator);

    // Generous, because starting a process is slower than anything else here and a flaky
    // timeout is worse than a slow test. On failure it reports what the simulator printed and
    // whether it is even alive: a bare timeout says nothing, which is exactly how a run that
    // could not find the executable at all looked from a build log.
    private static async Task WaitForAsync(
        Func<Task<bool>> condition,
        string expectation,
        SimulatorProcess simulator)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);

        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Timed out waiting for {expectation}. " +
                    $"The simulator is {(simulator.IsRunning ? "running" : "not running")}. " +
                    $"It printed:{Environment.NewLine}{simulator.Output}");
            }

            await Task.Delay(100);
        }
    }
}
