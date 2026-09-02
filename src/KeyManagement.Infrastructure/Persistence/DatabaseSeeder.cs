using KeyManagement.Application.Abstractions;
using KeyManagement.Domain.Access;
using KeyManagement.Domain.Assets;
using KeyManagement.Domain.Cabinets;
using Microsoft.EntityFrameworkCore;

namespace KeyManagement.Infrastructure.Persistence;

/// <summary>
/// Puts a usable starting state into an empty database: the four roles, an administrator, and
/// a small worked example of groups, assets and a cabinet.
/// </summary>
/// <remarks>
/// Seeded at runtime rather than through EF's <c>HasData</c>, because the administrator's
/// password hash carries a random salt. Baking one into a migration would either freeze a
/// single salt into source control or make the migration differ on every scaffold.
/// </remarks>
public sealed class DatabaseSeeder
{
    private readonly KeyManagementDbContext _context;
    private readonly IPasswordHasher _passwordHasher;

    /// <summary>Creates the seeder.</summary>
    /// <param name="context">The database to seed.</param>
    /// <param name="passwordHasher">Hashes the initial administrator password.</param>
    public DatabaseSeeder(KeyManagementDbContext context, IPasswordHasher passwordHasher)
    {
        _context = context;
        _passwordHasher = passwordHasher;
    }

    /// <summary>
    /// Seeds an empty database. Does nothing if any holder already exists.
    /// </summary>
    /// <param name="administratorPassword">Initial password for the administrator account.</param>
    /// <param name="administratorPin">PIN the administrator enters at a cabinet keypad.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether anything was written.</returns>
    public async Task<bool> SeedAsync(
        string administratorPassword,
        string administratorPin,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorPin);

        if (await _context.Users.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var administrator = new Role(
            "Administrator",
            Permissions.ManageUsers | Permissions.CheckoutAsset |
            Permissions.AcknowledgeAlarm | Permissions.ViewAudit);
        var supervisor = new Role(
            "Supervisor",
            Permissions.CheckoutAsset | Permissions.AcknowledgeAlarm | Permissions.ViewAudit);
        var holder = new Role("Holder", Permissions.CheckoutAsset);
        var auditor = new Role("Auditor", Permissions.ViewAudit);
        _context.Roles.AddRange(administrator, supervisor, holder, auditor);

        var plantRoom = new AssetGroup("Plant room", "Boiler house, risers and roof plant.");
        var vehicles = new AssetGroup("Vehicles", "Pool vehicle keys.");
        _context.AssetGroups.AddRange(plantRoom, vehicles);

        var assets = new[]
        {
            new Asset("PR-001", "Boiler house main door", plantRoom.Id),
            new Asset("PR-002", "Riser cupboard, levels 1 to 4", plantRoom.Id),
            new Asset("PR-003", "Roof plant enclosure", plantRoom.Id),
            new Asset("VH-001", "Pool van, registration on fob", vehicles.Id),
            new Asset("VH-002", "Pool car, registration on fob", vehicles.Id),
        };
        _context.Assets.AddRange(assets);

        var cabinet = new Cabinet("Reception", "Main building, ground floor");

        // No certificate is enrolled here. A cabinet is enrolled by issuing it one and
        // recording the fingerprint, which is a deliberate act by a person rather than
        // something a seeder should invent.
        for (var position = 1; position <= 10; position++)
        {
            var slot = cabinet.AddSlot($"A{position:D2}");

            // Only the seeded assets get a home; the rest of the slots stay empty, which is
            // also the normal state of a real cabinet.
            if (position <= assets.Length)
            {
                slot.Assign(assets[position - 1].Id);
            }
        }

        _context.Cabinets.Add(cabinet);

        var admin = new User("admin", "Administrator", _passwordHasher.Hash(administratorPassword));
        admin.SetPinHash(_passwordHasher.Hash(administratorPin));
        admin.Grant(administrator);
        admin.GrantGroup(plantRoom.Id);
        admin.GrantGroup(vehicles.Id);
        _context.Users.Add(admin);

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
