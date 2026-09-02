using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using KeyManagement.Application.Abstractions;
using KeyManagement.Infrastructure.Security;

namespace KeyManagement.Server.Devices;

/// <summary>
/// Wraps an accepted connection in mutual TLS.
/// </summary>
/// <remarks>
/// <para>
/// The gateway presents its own certificate and requires one back. A connection that offers no
/// client certificate, or one this deployment did not issue, never reaches the frame reader —
/// it is refused during negotiation.
/// </para>
/// <para>
/// Validation is against the deployment's own authority, explicitly, rather than the machine's
/// trust store. A cabinet is trusted because this installation enrolled it, not because a
/// public authority vouched for it.
/// </para>
/// </remarks>
public sealed class DeviceTlsHandshake
{
    private readonly X509Certificate2 _gatewayCertificate;
    private readonly X509Certificate2 _authority;
    private readonly IClock _clock;

    /// <summary>Creates the handshake.</summary>
    /// <param name="gatewayCertificate">What the gateway presents.</param>
    /// <param name="authority">What a cabinet's certificate must chain to.</param>
    /// <param name="clock">The current time, for validity.</param>
    public DeviceTlsHandshake(
        X509Certificate2 gatewayCertificate,
        X509Certificate2 authority,
        IClock clock)
    {
        _gatewayCertificate = gatewayCertificate;
        _authority = authority;
        _clock = clock;
    }

    /// <summary>Negotiates TLS and returns the encrypted stream with the peer's certificate.</summary>
    /// <param name="inner">The accepted connection.</param>
    /// <param name="timeout">How long negotiation may take.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The encrypted stream and the certificate the cabinet presented.</returns>
    /// <exception cref="AuthenticationException">The peer offered nothing acceptable.</exception>
    public async Task<(SslStream Stream, X509Certificate2 ClientCertificate)> AuthenticateAsync(
        Stream inner,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inner);

        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, ValidateCabinet);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = _gatewayCertificate,
                    ClientCertificateRequired = true,

                    // TLS 1.2 is the floor and 1.3 is preferred. Everything below it is broken,
                    // and this link talks to devices we issue certificates to, so there is no
                    // legacy peer to accommodate.
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (ssl.RemoteCertificate is not { } presented)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new AuthenticationException("The cabinet presented no certificate.");
        }

        return (ssl, X509CertificateLoader.LoadCertificate(presented.Export(X509ContentType.Cert)));
    }

    private bool ValidateCabinet(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return false;
        }

        // The platform's own verdict is ignored on purpose: it judges against the machine's
        // trust store, which knows nothing about this deployment's authority.
        using var presented = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        return CabinetCertificates.ChainsTo(presented, _authority, _clock.UtcNow);
    }
}
