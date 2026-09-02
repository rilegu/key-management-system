using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeyManagement.DeviceSimulator;

/// <summary>
/// What the simulator is pretending to be.
/// </summary>
public sealed class SimulatorOptions
{
    /// <summary>Host the cabinets dial.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Port the cabinets dial.</summary>
    public int Port { get; set; } = 5610;

    /// <summary>Name the gateway certificate must be issued to.</summary>
    /// <remarks>
    /// Checked during the handshake. A cabinet that will connect to anything calling itself the
    /// server is a cabinet an attacker can redirect.
    /// </remarks>
    public string ServerName { get; set; } = "localhost";

    /// <summary>Protects the private keys in the certificate files.</summary>
    public string CertificatePassword { get; set; } = string.Empty;

    /// <summary>The authority the gateway's certificate must chain to.</summary>
    public string AuthorityPath { get; set; } = Path.Combine("certs", "device-authority.pfx");

    /// <summary>The cabinets to simulate.</summary>
    public List<CabinetOptions> Cabinets { get; init; } = [];

    private static readonly JsonSerializerOptions ReadOptions =
        new(JsonSerializerDefaults.Web)
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new JsonStringEnumConverter() },
        };

    /// <summary>Reads configuration from a file.</summary>
    /// <param name="path">Where the configuration is.</param>
    /// <returns>The options.</returns>
    /// <exception cref="InvalidOperationException">The file is missing or unreadable.</exception>
    public static SimulatorOptions Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"No simulator configuration at '{path}'.");
        }

        return JsonSerializer.Deserialize<SimulatorOptions>(File.ReadAllText(path), ReadOptions)
            ?? throw new InvalidOperationException($"'{path}' is not a simulator configuration.");
    }
}

/// <summary>
/// One simulated cabinet.
/// </summary>
public sealed class CabinetOptions
{
    /// <summary>The name it was enrolled under. Must match its certificate.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Its certificate, holding the private key that proves who it is.</summary>
    public string CertificatePath { get; set; } = string.Empty;

    /// <summary>Firmware it claims to be running.</summary>
    public string FirmwareVersion { get; set; } = "1.4.2";

    /// <summary>The positions it holds.</summary>
    public List<PositionOptions> Positions { get; init; } = [];
}

/// <summary>
/// One position in a simulated cabinet.
/// </summary>
public sealed class PositionOptions
{
    /// <summary>Position label, as the server knows it.</summary>
    public string Position { get; set; } = string.Empty;

    /// <summary>Whether something is sitting in it at startup.</summary>
    public bool Occupied { get; set; } = true;
}
