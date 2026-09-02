using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KeyManagement.Infrastructure.Security;

/// <summary>
/// What a certificate is for.
/// </summary>
public enum CertificatePurpose
{
    /// <summary>The gateway proves it is the server a cabinet meant to reach.</summary>
    Server = 0,

    /// <summary>A cabinet proves which cabinet it is.</summary>
    Cabinet = 1,
}

/// <summary>
/// Issues the certificates the device link authenticates with.
/// </summary>
/// <remarks>
/// <para>
/// A cabinet's identity is its certificate. Nothing it says about itself is taken on trust:
/// the name in <c>Hello</c> is checked against the common name of the certificate it presented,
/// and the certificate is checked against the one that cabinet was enrolled with.
/// </para>
/// <para>
/// Built on <see cref="CertificateRequest"/> rather than on a shell tool, so issuance is
/// cross-platform, needs nothing installed, and can be tested directly.
/// </para>
/// </remarks>
public static class CabinetCertificates
{
    /// <summary>Common name of the authority this creates.</summary>
    public const string AuthorityName = "Key Management device authority";

    /// <summary>How long an issued certificate is valid for.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(825);

    // Client and server authentication. Without these a certificate is usable for the wrong
    // half of the handshake, which is the sort of thing that works in a test and fails in a
    // deployment.
    private static readonly Oid ClientAuthentication = new("1.3.6.1.5.5.7.3.2");
    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1");

    /// <summary>Creates the authority that signs everything else.</summary>
    /// <param name="now">The moment validity starts from.</param>
    /// <returns>The authority, with its private key.</returns>
    /// <remarks>
    /// One authority per deployment. Its private key is what lets a new cabinet be enrolled, so
    /// it belongs wherever the deployment keeps secrets and nowhere near source control.
    /// </remarks>
    public static X509Certificate2 CreateAuthority(DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest(
            $"CN={AuthorityName}", key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));

        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        // A minute of leeway. A certificate that is not yet valid because two machines disagree
        // about the time is a confusing way to spend an afternoon.
        using var authority = request.CreateSelfSigned(now.AddMinutes(-1), now.Add(Lifetime));

        return Usable(authority);
    }

    /// <summary>
    /// Returns a certificate whose private key a TLS stack will actually accept.
    /// </summary>
    /// <remarks>
    /// A key created in memory has no container behind it, and Windows refuses such a
    /// certificate as a TLS credential — for either end of the handshake — with an error that
    /// says nothing about keys. A round trip through PKCS#12 gives it one. The password is
    /// random and never leaves this method; it protects the bytes only while they are in hand.
    /// </remarks>
    private static X509Certificate2 Usable(X509Certificate2 certificate)
    {
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pkcs12, password),
            password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    /// <summary>Issues a certificate for a cabinet or for the gateway itself.</summary>
    /// <param name="authority">The signing authority, with its private key.</param>
    /// <param name="commonName">The cabinet's name, or the gateway's host name.</param>
    /// <param name="purpose">Which half of the handshake it is for.</param>
    /// <param name="now">The moment validity starts from.</param>
    /// <returns>The certificate, with its private key.</returns>
    /// <exception cref="ArgumentException">The common name is empty.</exception>
    public static X509Certificate2 Issue(
        X509Certificate2 authority,
        string commonName,
        CertificatePurpose purpose,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest(
            $"CN={commonName}", key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [purpose == CertificatePurpose.Cabinet ? ClientAuthentication : ServerAuthentication],
                critical: false));

        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        if (purpose == CertificatePurpose.Server)
        {
            // The name a cabinet dials has to appear here, or its own validation of the server
            // fails no matter how good the chain is.
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(commonName);
            names.AddIpAddress(System.Net.IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }

        var serial = RandomNumberGenerator.GetBytes(16);

        // A certificate may not outlive the authority that signed it. Without this clamp, one
        // issued near the end of the authority's life is refused outright — including one
        // issued moments after the authority itself, which is the common case on a fresh
        // deployment and fails by a single second.
        var notBefore = now.AddMinutes(-1);
        var notAfter = now.Add(Lifetime);
        if (notAfter > authority.NotAfter)
        {
            notAfter = authority.NotAfter;
        }

        using var issued = request.Create(authority, notBefore, notAfter, serial);
        using var withKey = issued.CopyWithPrivateKey(key);

        return Usable(withKey);
    }

    /// <summary>The identifier a cabinet is enrolled under.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>An uppercase hexadecimal SHA-256 fingerprint.</returns>
    /// <remarks>
    /// SHA-256, not the <see cref="X509Certificate2.Thumbprint"/> property, which is still SHA-1.
    /// This value is what decides which cabinet a connection is, so it should not rest on a
    /// hash whose collision resistance is gone.
    /// </remarks>
    public static string ThumbprintOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    /// <summary>The common name a certificate was issued to.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>The common name, or an empty string if it carries none.</returns>
    public static string CommonNameOf(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? string.Empty;
    }

    /// <summary>Writes a certificate and its private key to a file.</summary>
    /// <param name="certificate">What to write.</param>
    /// <param name="path">Where to write it.</param>
    /// <param name="password">Protects the private key.</param>
    public static void Save(X509Certificate2 certificate, string path, string password)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));
    }

    /// <summary>Reads a certificate and its private key back.</summary>
    /// <param name="path">Where it was written.</param>
    /// <param name="password">Protects the private key.</param>
    /// <returns>The certificate.</returns>
    /// <remarks>
    /// Deliberately not <see cref="X509KeyStorageFlags.EphemeralKeySet"/>. On Windows, Schannel
    /// cannot use a key that was never given a container, so a certificate loaded that way
    /// negotiates fine as a client and fails as a server — with the peer seeing nothing but a
    /// closed connection.
    /// </remarks>
    public static X509Certificate2 Load(string path, string password) =>
        X509CertificateLoader.LoadPkcs12FromFile(
            path,
            password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

    /// <summary>Whether a certificate was signed by the given authority and is currently valid.</summary>
    /// <param name="certificate">The certificate presented.</param>
    /// <param name="authority">The authority it must chain to.</param>
    /// <param name="now">The moment to judge validity at.</param>
    /// <returns><see langword="true"/> when it chains to the authority and is in date.</returns>
    /// <remarks>
    /// Built against the given authority explicitly rather than the machine's trust store. A
    /// cabinet is trusted because this deployment issued it a certificate, not because some
    /// public authority did.
    /// </remarks>
    public static bool ChainsTo(X509Certificate2 certificate, X509Certificate2 authority, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(authority);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;

        return chain.Build(certificate);
    }
}
