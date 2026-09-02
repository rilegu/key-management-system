using System.Security.Cryptography.X509Certificates;
using KeyManagement.Application.Abstractions;
using KeyManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KeyManagement.Infrastructure.Security;

/// <summary>
/// Where the device authority and its certificates live.
/// </summary>
public sealed class DeviceCertificateOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "DeviceCertificates";

    /// <summary>Directory holding the authority and the issued certificates.</summary>
    /// <remarks>Excluded from source control; see the repository's ignore rules.</remarks>
    public string Directory { get; set; } = "certs";

    /// <summary>Protects the private keys on disk.</summary>
    /// <remarks>
    /// Supplied by configuration. Files under <see cref="Directory"/> are only as safe as the
    /// filesystem they sit on, and this stops a copied file being immediately usable.
    /// </remarks>
    public string Password { get; set; } = string.Empty;

    /// <summary>Path to the authority.</summary>
    public string AuthorityPath => Path.Combine(Directory, "device-authority.pfx");

    /// <summary>Path to the gateway's own certificate.</summary>
    public string GatewayPath => Path.Combine(Directory, "device-gateway.pfx");

    /// <summary>Path to a cabinet's certificate.</summary>
    /// <param name="cabinetName">The cabinet.</param>
    /// <returns>Where its certificate is written.</returns>
    public string CabinetPath(string cabinetName) =>
        Path.Combine(Directory, $"cabinet-{Sanitise(cabinetName)}.pfx");

    private static string Sanitise(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));
}

/// <summary>
/// Issues a cabinet its certificate and records the fingerprint against it.
/// </summary>
/// <remarks>
/// Enrolment is a deliberate act. Someone runs this for a cabinet they are installing, copies
/// the resulting file to it, and from then on that cabinet is the only thing that can attach
/// under that name. Nothing enrols itself.
/// </remarks>
public sealed class CabinetEnrolment
{
    private readonly KeyManagementDbContext _context;
    private readonly DeviceCertificateOptions _options;
    private readonly IClock _clock;

    /// <summary>Creates the enrolment service.</summary>
    /// <param name="context">The database.</param>
    /// <param name="options">Where certificates live.</param>
    /// <param name="clock">The current time.</param>
    public CabinetEnrolment(
        KeyManagementDbContext context,
        DeviceCertificateOptions options,
        IClock clock)
    {
        _context = context;
        _options = options;
        _clock = clock;
    }

    /// <summary>Loads the authority, creating it and the gateway certificate on first use.</summary>
    /// <returns>The authority, with its private key.</returns>
    public X509Certificate2 EnsureAuthority()
    {
        if (File.Exists(_options.AuthorityPath))
        {
            return CabinetCertificates.Load(_options.AuthorityPath, _options.Password);
        }

        var now = _clock.UtcNow;
        var authority = CabinetCertificates.CreateAuthority(now);
        CabinetCertificates.Save(authority, _options.AuthorityPath, _options.Password);

        using var gateway = CabinetCertificates.Issue(
            authority, "localhost", CertificatePurpose.Server, now);
        CabinetCertificates.Save(gateway, _options.GatewayPath, _options.Password);

        return authority;
    }

    /// <summary>Issues a certificate to a cabinet and enrols its fingerprint.</summary>
    /// <param name="cabinetName">The cabinet, which must already exist.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Where the certificate was written, and its fingerprint.</returns>
    /// <exception cref="InvalidOperationException">No cabinet is enrolled under that name.</exception>
    public async Task<(string Path, string Thumbprint)> IssueAsync(
        string cabinetName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cabinetName);

        var cabinet = await _context.Cabinets
            .SingleOrDefaultAsync(c => c.Name == cabinetName, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No cabinet named '{cabinetName}' exists. Create it before issuing a certificate.");

        using var authority = EnsureAuthority();
        using var certificate = CabinetCertificates.Issue(
            authority, cabinetName, CertificatePurpose.Cabinet, _clock.UtcNow);

        var path = _options.CabinetPath(cabinetName);
        CabinetCertificates.Save(certificate, path, _options.Password);

        var thumbprint = CabinetCertificates.ThumbprintOf(certificate);

        // Re-issuing replaces the fingerprint, so the previous certificate stops working. That
        // is how a cabinet is re-keyed, and it means a lost certificate is revoked by issuing
        // another rather than by anything more elaborate.
        cabinet.EnrolCertificate(thumbprint);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return (path, thumbprint);
    }
}
