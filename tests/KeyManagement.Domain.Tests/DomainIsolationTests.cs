using System.Reflection;
using KeyManagement.Domain;

namespace KeyManagement.Domain.Tests;

/// <summary>
/// The custody rules are only trustworthy if they can be exercised without a database, a web
/// host or a UI. A project reference added in a hurry would quietly break that, so it is
/// asserted rather than left to review.
/// </summary>
public sealed class DomainIsolationTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Microsoft.Data.Sqlite",
        "Avalonia",
        "CommunityToolkit.Mvvm",
    ];

    [Fact]
    public void Domain_references_no_infrastructure_or_ui_assembly()
    {
        var referenced = typeof(DomainAssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        var violations = referenced
            .Where(name => ForbiddenPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(violations);
    }
}
