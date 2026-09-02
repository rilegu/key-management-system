using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KeyManagement.DeviceSimulator;

// A cabinet, as a program. It dials the server, proves who it is with a certificate, reports
// what happens at its positions and does what it is told. It decides nothing about custody.

var configPath = Argument(args, "--config") ?? "simulator.json";
var host = Argument(args, "--host");
var port = Argument(args, "--port");
var password = Argument(args, "--certificate-password");

SimulatorOptions options;

try
{
    options = SimulatorOptions.Load(configPath);
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

// Command-line arguments win, so one configuration file can point at a different deployment.
options.Host = host ?? options.Host;
options.Port = port is null ? options.Port : int.Parse(port, System.Globalization.CultureInfo.InvariantCulture);
options.CertificatePassword = password ?? options.CertificatePassword;

if (options.Cabinets.Count == 0)
{
    Console.Error.WriteLine($"'{configPath}' declares no cabinets.");
    return 1;
}

X509Certificate2 authority;

try
{
    authority = Load(options.AuthorityPath, options.CertificatePassword);
}
catch (Exception exception) when (exception is CryptographicException or IOException)
{
    Console.Error.WriteLine(
        $"Could not read the device authority at '{options.AuthorityPath}': {exception.Message}");
    Console.Error.WriteLine(
        "Run the server once with --issue-cabinet-certificate to create it.");
    return 1;
}

var cabinets = new List<CabinetDevice>();

foreach (var cabinet in options.Cabinets)
{
    try
    {
        cabinets.Add(new CabinetDevice(
            cabinet, options, Load(cabinet.CertificatePath, options.CertificatePassword), authority));
    }
    catch (Exception exception) when (exception is CryptographicException or IOException)
    {
        Console.Error.WriteLine(
            $"Could not read the certificate for '{cabinet.Name}' at '{cabinet.CertificatePath}': {exception.Message}");
        return 1;
    }
}

using var stopping = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

Console.WriteLine(
    $"Simulating {cabinets.Count} cabinet(s) against {options.Host}:{options.Port}. Type help for commands.");

var running = cabinets.Select(c => c.RunAsync(stopping.Token)).ToList();
var console = new SimulatorConsole(cabinets).RunAsync(stopping, stopping.Token);

try
{
    await console;
    await Task.WhenAll(running);
}
catch (OperationCanceledException)
{
    // Asked to stop.
}
finally
{
    foreach (var cabinet in cabinets)
    {
        await cabinet.DisposeAsync();
    }

    authority.Dispose();
}

return 0;

static string? Argument(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static X509Certificate2 Load(string path, string password) =>
    X509CertificateLoader.LoadPkcs12FromFile(
        path, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
