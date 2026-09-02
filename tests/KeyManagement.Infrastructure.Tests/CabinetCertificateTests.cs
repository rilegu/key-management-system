using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KeyManagement.Infrastructure.Persistence;
using KeyManagement.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KeyManagement.Infrastructure.Tests;

/// <summary>
/// The certificates a cabinet's identity rests on.
/// </summary>
public sealed class CabinetCertificateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_issued_certificate_chains_to_its_authority()
    {
        using var authority = CabinetCertificates.CreateAuthority(Now);
        using var cabinet = CabinetCertificates.Issue(
            authority, "Reception", CertificatePurpose.Cabinet, Now);

        Assert.True(CabinetCertificates.ChainsTo(cabinet, authority, Now.AddDays(1)));
        Assert.Equal("Reception", CabinetCertificates.CommonNameOf(cabinet));
    }

    [Fact]
    public void A_certificate_from_a_different_authority_does_not_chain()
    {
        using var ours = CabinetCertificates.CreateAuthority(Now);
        using var theirs = CabinetCertificates.CreateAuthority(Now);
        using var forged = CabinetCertificates.Issue(
            theirs, "Reception", CertificatePurpose.Cabinet, Now);

        // Same name, valid signature, wrong signer. This is the case a shared secret could not
        // distinguish at all.
        Assert.False(CabinetCertificates.ChainsTo(forged, ours, Now.AddDays(1)));
    }

    [Fact]
    public void An_issued_certificate_never_outlives_its_authority()
    {
        // Found by a test failing over a single second: issuing "now plus the lifetime" against
        // an authority created a moment earlier produces a certificate the platform refuses.
        using var authority = CabinetCertificates.CreateAuthority(Now);
        using var late = CabinetCertificates.Issue(
            authority, "Reception", CertificatePurpose.Cabinet, Now.AddDays(800));

        Assert.True(late.NotAfter <= authority.NotAfter);
    }

    [Fact]
    public void A_cabinet_certificate_is_for_client_authentication()
    {
        using var authority = CabinetCertificates.CreateAuthority(Now);
        using var cabinet = CabinetCertificates.Issue(
            authority, "Reception", CertificatePurpose.Cabinet, Now);
        using var gateway = CabinetCertificates.Issue(
            authority, "localhost", CertificatePurpose.Server, Now);

        Assert.Contains(Usages(cabinet), oid => oid == "1.3.6.1.5.5.7.3.2");
        Assert.Contains(Usages(gateway), oid => oid == "1.3.6.1.5.5.7.3.1");
    }

    [Fact]
    public void Two_certificates_never_share_a_fingerprint()
    {
        using var authority = CabinetCertificates.CreateAuthority(Now);
        using var first = CabinetCertificates.Issue(
            authority, "Reception", CertificatePurpose.Cabinet, Now);
        using var second = CabinetCertificates.Issue(
            authority, "Reception", CertificatePurpose.Cabinet, Now);

        // Re-issuing for the same cabinet produces a different identity, which is what makes
        // re-keying a cabinet possible and retires the previous certificate.
        Assert.NotEqual(
            CabinetCertificates.ThumbprintOf(first), CabinetCertificates.ThumbprintOf(second));
    }

    [Fact]
    public async Task Enrolling_records_the_fingerprint_against_the_cabinet()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var directory = Path.Combine(Path.GetTempPath(), $"kms-cert-{Guid.CreateVersion7():N}");

        try
        {
            await database.WithContextAsync(async context =>
            {
                context.Cabinets.Add(new Domain.Cabinets.Cabinet("Reception", "Ground floor"));
                await context.SaveChangesAsync();
            });

            string thumbprint;

            await using (var scope = database.CreateScope())
            {
                var enrolment = new CabinetEnrolment(
                    scope.ServiceProvider.GetRequiredService<KeyManagementDbContext>(),
                    new DeviceCertificateOptions { Directory = directory, Password = "test" },
                    scope.ServiceProvider.GetRequiredService<Application.Abstractions.IClock>());

                (_, thumbprint) = await enrolment.IssueAsync("Reception");
            }

            await database.WithContextAsync(async context =>
            {
                var cabinet = await context.Cabinets.SingleAsync(c => c.Name == "Reception");
                Assert.Equal(thumbprint, cabinet.CertificateThumbprint);
            });
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or DirectoryNotFoundException)
            {
                // Cleanup must not fail a passing test.
            }
        }
    }

    [Fact]
    public async Task A_cabinet_that_does_not_exist_cannot_be_enrolled()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var directory = Path.Combine(Path.GetTempPath(), $"kms-cert-{Guid.CreateVersion7():N}");

        await using var scope = database.CreateScope();
        var enrolment = new CabinetEnrolment(
            scope.ServiceProvider.GetRequiredService<KeyManagementDbContext>(),
            new DeviceCertificateOptions { Directory = directory, Password = "test" },
            scope.ServiceProvider.GetRequiredService<Application.Abstractions.IClock>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => enrolment.IssueAsync("Nowhere"));
    }

    private static IEnumerable<string> Usages(X509Certificate2 certificate) =>
        certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(e => e.EnhancedKeyUsages.Cast<Oid>())
            .Select(o => o.Value ?? string.Empty);
}
